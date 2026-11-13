# Streaming and resume

A streaming call sends the events of a task as they happen: status changes, artifact chunks and messages. Over JSON-RPC and HTTP+JSON the events travel as Server-Sent Events (SSE). Use streaming for long tasks. If the connection drops, the client resumes the stream and no event is lost or repeated.

## Turn it on

Streaming is on by default. The served card turns an unset `Capabilities.Streaming` into `true`, because every executor can stream: the engine turns its updates into events. Set it to `false` to refuse streaming calls. The server then answers them with `UnsupportedOperation`, and a client that read the card fails fast before it sends anything. The client also refuses to stream to another SDK's agent whose card leaves the flag unset.

[!code-csharp[](../../../samples/Snippets/Streaming.cs#Capability)]

## Send and stream

<xref:A2A.Client.A2AClient.SendStreamingMessageAsync*> sends a message and returns the events of the task, in order:

[!code-csharp[](../../../samples/EchoClient/Program.cs#Send)]

`evt.GetText()` is the text an artifact chunk or a message carries, and `evt.State` is the task state of a status update or a task snapshot, so one check covers both.

The first event is the task, or a direct message. A direct message is the only event, and the stream ends. Events of other executions of the same task, such as the `CANCELED` event of a `CancelTask`, arrive on the same stream.

When you only need the text, <xref:A2A.Client.A2AClientTextExtensions.StreamTextAsync*> yields it chunk by chunk. It stops when the task ends or needs input or authorization, and throws <xref:A2A.Client.TaskNotCompletedException> if the task did not complete. On a conversation (<xref:A2A.Client.A2AConversation>), `StreamTextAsync` throws nothing for a task state: it sets `LastReply`, and the next send answers a task that waits for input:

[!code-csharp[](../../../samples/Snippets/Streaming.cs#StreamText)]

## Follow a task

To watch a task you did not start on this connection, use <xref:A2A.Client.A2AClient.ObserveTaskAsync*>. It subscribes, and when the connection drops it reconnects and resumes. This example starts a task without waiting for it, then follows it:

[!code-csharp[](../../../samples/EchoClient/Program.cs#Observe)]

`ObserveTaskAsync` works like this:

- It reconnects after 0.5, 1, 2, 4 and 8 seconds. A longer `Retry-After` from the server wins. After the fifth failed reconnect, the error is thrown.
- A reconnect that delivers a new event gives the client a fresh set of attempts.
- It sends the last event id it saw in the `Last-Event-ID` header. The server continues after that event.
- It drops any event whose sequence it already delivered.
- Without event ids (gRPC, or a peer that sends none), an A2A 1.1 client resumes with `lastSeenGeneration`: the last generation it saw.
- On A2A 1.1, it follows the events' `generation` (spec 1.1 §3.2.7). A jump says the stream missed a change: after that event the client calls GetTask and yields the task as a `TaskEvent`. An event whose generation is already reflected (a replay, or one that snapshot covers) is dropped. A server that sends generation 0, or one generation on two update events in a row, does not implement it, and the client stops using it for that stream. Turn the GetTask off with <xref:A2A.Client.A2AClientOptions.ReconcileGenerationGaps> = false. A 1.0 wire has no generations, so nothing changes there.
- If the task is already finished, you get its final snapshot as a `TaskEvent`.
- The same options (headers, credentials) are used on every reconnect. `SendStreamingMessageAsync` resumes in the same way once the first event has shown the task id.

<xref:A2A.Client.A2AClient.SubscribeToTaskAsync*> is the plain call. It subscribes once and does not reconnect:

[!code-csharp[](../../../samples/Snippets/Streaming.cs#Subscribe)]

## Event ids and resume on the server

Every stored task event has an SSE id: `{taskId}:{sequence}`. The sequence counts per task from 1 (<xref:A2A.Server.Streaming.StreamCursor>). A direct message is not stored and has no id.

Both `SubscribeToTask` and `SendStreamingMessage` read `Last-Event-ID`. For SubscribeToTask, the server answers like this:

| `Last-Event-ID` | The server sends |
|---|---|
| Absent | A task snapshot, then live events. A task that is already terminal is refused with `UnsupportedOperation`. |
| A cursor, and the events after it are still retained | Those events, then live events |
| A cursor older than the retained events, past the last event, or no events retained | A task snapshot stamped with the last sequence, then live events |
| A cursor at the last event of a terminal task | A task snapshot, then the stream ends |
| A value that is not a cursor for this task | A task snapshot, then live events |

If events are trimmed while the server reads them, it sends a snapshot instead of skipping them.

On `SendStreamingMessage`, a valid cursor makes the call a reconnect. The server resumes that task, authorized as `SubscribeToTask`, and does not send the message again. The header is ignored, and the message is sent normally, when it does not parse or when the message names another task.

## When a stream closes

| Stream | It ends when |
|---|---|
| SendStreamingMessage | The task reaches a terminal state, also one set by another execution such as `CancelTask`. Or the executor returns and no other execution of the task is running. |
| SubscribeToTask | The task reaches a terminal state |
| Either | The client disconnects, or a stream limit below is reached |

A task in `INPUT_REQUIRED` or `AUTH_REQUIRED` keeps a send stream open while the executor is still running. Stopping a stream never stops the task.

When a stream ends before a terminal event, for example at `MaxStreamDuration` or `StreamIdleTimeout`, the client treats it like a dropped connection and resumes it after the backoff. It does not resume after a terminal event or a direct message.

## A connection that goes silent

A NAT, proxy or load balancer can drop a connection without closing it. Nothing arrives, and without a timer the read would wait until the operating system gives up on the connection, which can take hours. The server's keep-alive comments (`KeepAliveInterval`, below) let the client tell a quiet task from a dead connection.

<xref:A2A.Client.A2AClientOptions.StreamKeepAliveTimeout> is how long an SSE stream may receive nothing at all, no event and no keep-alive, before the client treats the connection as dropped:

- The default is 60 seconds: four times the server's default `KeepAliveInterval` of 15 seconds, so a few late keep-alives do not count. Null or `Timeout.InfiniteTimeSpan` turns it off. Keep it above the server's interval.
- The timer starts only after the first keep-alive comment on a connection (any SSE comment line). A peer that sends no keep-alives is never timed out, however long its task stays quiet, so the default is safe with such peers.
- Any bytes reset it, events and keep-alives alike. It counts only while the client waits for bytes: time your code spends on an event does not count. One timer serves the whole stream.
- When it expires, the read is aborted and the stream fails with an `IOException` whose inner exception is a `TimeoutException`. `ObserveTaskAsync` and `SendStreamingMessageAsync` (once the task id is known) treat it like any dropped connection: after the backoff they reconnect with `Last-Event-ID`, or with `lastSeenGeneration` on A2A 1.1, as described in [Follow a task](#follow-a-task). With this SDK's servers no event is lost or repeated. `SubscribeToTaskAsync` does not reconnect and throws the `IOException`.
- Your own cancellation is still an `OperationCanceledException` with your token, and never causes a reconnect.
- It applies to JSON-RPC and HTTP+JSON, and to the v0.3 bindings. For gRPC, see below.

## The replay buffer

The event store keeps the last events of each task so that a client can resume. These options are on <xref:A2A.Server.StreamingOptions>, under `A2AServerOptions.Streaming`:

| Option | Default | Meaning |
|---|---|---|
| `ReplayBufferSize` | 1000 | Events kept per task. Older events are dropped. With the in-memory store, each event costs about twice its model size, because the encoded JSON is kept too. |
| `TerminalRetention` | 10 minutes | How long the events of a finished task are kept |
| `KeepAliveInterval` | 15 seconds | Interval of SSE comments (`: ka`) on an idle stream. Zero turns them off. |
| `ChannelCapacity` | 256 | Size of each per-task event queue. Values below 1 count as 1. |

[!code-csharp[](../../../samples/Snippets/Streaming.cs#Options)]

When a queue is full, the agent's next `TaskUpdater` write waits until the server catches up. A stream that falls behind drops its queued events and fills the gap from the event store, in order. A snapshot replaces events the store no longer has. Push delivery keeps every event.

### When the event log is gone

If a client resumes after the events were trimmed or purged, the server sends a task snapshot instead. The client yields the snapshot as a `TaskEvent`. If the snapshot has an id with a sequence below the client's cursor, the server lost its log and started over. The client then takes the snapshot and continues from its sequence. A snapshot of a terminal task ends the stream. Treat a `TaskEvent` in the middle of a stream as a state to replace what you know, not as one more step.

## Artifact chunks

An agent can send an artifact in chunks. Each chunk is an artifact update event with an `artifactId`. The flag `append` says if the parts go to an artifact that exists. The flag `lastChunk` says it is the final chunk.

The easy way is <xref:A2A.Server.TaskUpdater.OpenArtifact*>. It returns an <xref:A2A.Server.ArtifactWriter> that keeps the id and the flags for you. The first write creates the artifact with its name, description and metadata. Each later write appends. `CompleteAsync` on the writer, or disposing it, sends the last chunk: an empty append with `lastChunk`. A terminal state of the task completes the open writers first, so the artifact is sealed before the task ends. A writer that never wrote sends nothing.

[!code-csharp[](../../../samples/Snippets/Streaming.cs#Writer)]

The same with `AddArtifactAsync`, which gives you full control over each event:

[!code-csharp[](../../../samples/Snippets/Streaming.cs#Chunks)]

### The lifecycle

The server applies each event to the artifact with the same id in the task. `lastChunk: true` seals the artifact that the event leaves behind.

| Stored artifact | Event | Result |
|---|---|---|
| none | `append: false` | The artifact is created. |
| none | `append: true` | The artifact is created. |
| open | `append: false` | The artifact is replaced. |
| open | `append: true` | The parts are added. Metadata is merged as before. |
| sealed | `append: false` | The artifact is replaced. The new one is open, unless this event has `lastChunk: true`. |
| sealed | `append: true` | Rejected. |

Replacing is on purpose. It lets an agent correct an artifact it already finished. It is an explicit act, so it cannot hide a bug the way a stray append can.

### Append to a sealed artifact

`TaskUpdater.AddArtifactAsync` throws `InvalidOperationException` at the call. Nothing is queued. The message says what to do: send the artifact with `append: false` to replace it, or use a new artifact id.

Your executor can catch the exception and go on. If it does not, the task ends as `FAILED` with the text "The agent failed to process the request.", and the error is logged. The updater also knows the artifacts of earlier runs. A follow-up message cannot append to an artifact that an earlier run sealed.

Two runs of one task can race. The server checks again before it stores an event. An append to a sealed artifact is dropped: it is not stored, streamed or pushed. The server logs a warning (`ArtifactAppendAfterSeal`) and does not fail the task, because the other run may be healthy.

### Where the seal is stored

The server stores the seal in the artifact's metadata: `"a2a.artifact.sealed": true`. It is the constant `A2AProtocol.ArtifactSealedKey`. Only the server writes it. If an agent sets the key itself, the server removes it, unless the event has `lastChunk: true`. The events on the wire do not carry the key.

You see the key in `GetTask` and in task snapshots. A snapshot has no `lastChunk`, so the key is how you tell a finished artifact from a partial one.

### Empty parts

- `parts: []` is allowed only with `append: true` on an artifact that exists. It seals the artifact or updates its metadata. The A2A text still says an artifact needs at least one part, so a strict peer may reject such an event. Send the last parts with `lastChunk: true` when you can.
- `parts: []` in any other case is invalid. `AddArtifactAsync` throws `ArgumentException`.
- A part with empty content, such as `text: ""`, is a normal part.
- A null entry in `parts` is invalid.

### The optional hash

A producer can add a hash so that the consumer can check the content. It finds corruption and bad reassembly: lost, repeated or reordered chunks. It does not prove who wrote the content. Anyone who can change the content can change an unsigned hash. Nothing is hashed unless the producer asks for it.

The hash is in `metadata["sha256"]`. The value is `sha256:` and 64 lowercase hex digits. The hash covers the content bytes of the parts, in order, with nothing between them:

| Part | Bytes that are hashed |
|---|---|
| text | The UTF-8 bytes of `text`. No byte order mark. No Unicode normalization. |
| raw | The decoded bytes, not the base64 text. |
| url | The UTF-8 bytes of the URL string as it was sent. The content behind the URL is not fetched or hashed. |
| data | The UTF-8 bytes of the data in canonical JSON form (RFC 8785). |

These are not hashed: part `metadata`, `filename` and `mediaType`, and the artifact's `name`, `description`, `metadata` and `extensions`.

Because nothing goes between the parts, a file sent as raw chunks has the plain SHA-256 of the file. Moving the border between two parts does not change the hash.

The value on each event is the hash of the artifact so far: every part from the first chunk up to and including this event. The value on the last chunk is the hash of the final artifact. A seal-only event repeats it. `append: false` starts over. This way the stored artifact always has a hash that matches its stored parts, and the first chunk that does not match is the first bad one.

A number outside the range of a double cannot be put in canonical JSON form. The producer then gets an `InvalidOperationException`, and the consumer reports `Unverifiable`.

#### Producer

Use `TaskUpdater.AddHashedArtifactAsync` for every chunk. It takes the same arguments as `AddArtifactAsync`, sets `sha256` for you, and overwrites any `sha256` in your metadata. It does not keep the content: the hash continues from chunk to chunk. For an append to an artifact that an earlier run stored, it first hashes the stored parts. The hash API (`AddHashedArtifactAsync`, `ArtifactVerifier`, `ArtifactHash`) is marked `[Experimental]` (`A2AEXP003`). To use it, add `<NoWarn>A2AEXP003</NoWarn>` to your project. See [Versioning](versioning.md#a2aexp003).

[!code-csharp[](../../../samples/Snippets/Streaming.cs#HashedChunks)]

Do not mix plain and hashed chunks for one artifact. A plain `AddArtifactAsync` with `append: true` on an artifact that is being hashed throws, because the hash would be wrong. A hashed append to an artifact that this run sent without the hash helper throws too.

#### Consumer

Give `ArtifactVerifier` each event of one task, in order. For an artifact update, it returns one result for the chunk. For a task snapshot, it starts over and returns one result per artifact.

[!code-csharp[](../../../samples/Snippets/Streaming.cs#VerifyStream)]

For a task from `GetTask`, check each artifact with `ArtifactVerifier.Verify`:

[!code-csharp[](../../../samples/Snippets/Streaming.cs#VerifyTask)]

Each result has one of these statuses:

| Status | Meaning |
|---|---|
| `NoHash` | There is no `sha256` in the metadata. |
| `Verified` | The hash matches. |
| `Mismatch` | The hash is different. `Expected` and `Actual` tell you both values. |
| `Unverifiable` | The value is not a string, or not `sha256:` and 64 lowercase hex digits (an unknown scheme is never trusted), or the parts could not be hashed. |

The verifier never throws on bad content and never ends the stream. You decide what to do with a bad result. The client does not enforce the seal.

## Stream limits

These are on <xref:A2A.Server.A2ALimits>. See also [Limits and rate limiting](limits-and-rate-limiting.md).

| Limit | Default | When reached |
|---|---|---|
| `MaxConcurrentStreamsPerTask` | 10 | The new stream is refused with `RATE_LIMITED`. Counted per task and caller. |
| `MaxConcurrentStreamsPerPrincipal` | 50 | The new stream is refused with `RATE_LIMITED`. Anonymous callers count per remote IP. |
| `MaxStreamDuration` | 1 hour | The stream ends normally. Clients resume. Zero or `Timeout.InfiniteTimeSpan` turns it off. |
| `StreamReauthInterval` | 5 minutes | The caller is authorized again. If denied, the stream ends with the error a new request would get (`TaskNotFound` for a task-scoped stream, else `PermissionDenied`). Zero or `Timeout.InfiniteTimeSpan` turns it off. |
| `StreamIdleTimeout` | off | A stream that sent no event for this long ends normally, and clients resume. Keep-alive comments do not count. Zero or `Timeout.InfiniteTimeSpan` turns it off. |
| `MaxEventBytes` | 4 MB | An event above this is never stored or sent. The task fails and the agent is stopped. Send large artifacts in chunks. |

The stream counters are per process. Opening a stream also counts against the rate limit like any other request.

## Live exchange (A2A 1.1)

A live exchange is one stream that carries input both ways while the task runs (1.1 §3.1.12). It needs
`A2AServerOptions.EnableProtocolV11()` and the `streaming` capability, and the caller must send `A2A-Version: 1.1`.
The API is experimental (`A2AEXP005`).

[!code-csharp[](../../../samples/Snippets/Streaming.cs#LiveExchange)]

- The first request binds the exchange to one task: a message (new task or follow-up) or a `subscribe` to an existing task
  (`OpenLiveAsync(StreamRequest)`). Later requests send messages or client artifacts to that task.
- A refused request (wrong task, malformed, a late `subscribe`, the agent busy) is a `StreamErrorEvent` with the code of the
  unary operation and the `referenceId` of the message or artifact. The exchange stays open.
- gRPC uses the bidi `SendLiveMessage` call. JSON-RPC (`SendLiveMessage`) and HTTP+JSON (`POST /message:live`) split it by
  the `Accept` header: the call with `Accept: text/event-stream` is the downlink (the stream); every other call is an
  uplink that must name its task and answers with the task. On HTTP a refused uplink call fails, and the same refusal
  also shows on the caller's own downlinks of the task, so `SendAsync` throws and `Events` has the `StreamErrorEvent`.
  A refused call of another caller (who gets `TaskNotFound`) never reaches the owner's downlinks.
- Each later request on a gRPC stream takes a token from the caller's [rate limit](limits-and-rate-limiting.md#rate-limit),
  as an HTTP uplink call does. Without a token the request is refused with `RESOURCE_EXHAUSTED`, and the stream stays open.

### Input for a running agent

By default a message to a `WORKING` task is refused with `UnsupportedOperation` (gRPC `FAILED_PRECONDITION`), as any follow-up to a running task is. An agent that reads
`AgentRequestContext.LiveInput` takes such messages while it runs instead:

[!code-csharp[](../../../samples/Snippets/Streaming.cs#LiveInput)]

- From the first read on, a message for the task (from a live exchange or any SendMessage) goes to that execution, in any
  non-terminal state. No second execution starts.
- The buffer holds `A2ALimits.LiveInputCapacity` (32) items. When it is full the message is refused with
  `RESOURCE_EXHAUSTED` (`RateLimited`, retry after one second) and nothing is stored.
- Input reaches only an execution on the same server instance.

## On the wire

The response has `Content-Type: text/event-stream`. Each frame has an `id:` line (for stored events) and a `data:` line.

- JSON-RPC: `data` is a JSON-RPC response. An error after the stream started is a last JSON-RPC error frame.
- HTTP+JSON: `data` is the event. An error after the stream started is a last frame named `error` with a `google.rpc.Status` body. The client accepts both forms.
- The client skips an event type it does not know instead of ending the stream.
- HTTP+JSON subscribe is `GET` or `POST /tasks/{id}:subscribe`. The server accepts both. The client uses `POST` unless you set `RestSubscribeMethod` on <xref:A2A.Client.A2AClientOptions>.
- Streaming calls have no client timeout and are not retried as unary calls are. Resume is separate and is described above.

## gRPC

gRPC streams carry no event ids, so there is no `Last-Event-ID` resume on gRPC. The client transport does not implement <xref:A2A.Client.Streaming.IResumableTransport>. What a reconnect gets depends on the protocol version:

- **A2A 1.0.** No resume. A new `SubscribeToTask` gives a task snapshot, then live events. `ObserveTaskAsync` still reconnects over gRPC, but each reconnect subscribes again from the start, and events can be delivered again. Design the consumer to tolerate repeats. The same holds for a peer that sends no SSE ids.
- **A2A 1.1.** `SubscribeToTaskRequest` has `last_seen_generation`. `ObserveTaskAsync` (and `SendStreamingMessageAsync` once it knows the task id) reconnects with the last generation it saw, and the server continues after it without a task snapshot. If the server no longer has the events after it, the stream starts with a snapshot instead. The server resumes from its event log (<xref:A2A.Server.StreamingOptions.ReplayBufferSize>), not from the task's stored timeline, which keeps only the newest <xref:A2A.Server.A2ALimits.MaxTimelineEntries> entries: a generation older than the oldest kept entry still resumes without a snapshot while its events are retained, and the snapshot carries the kept timeline. An event whose generation the client already saw is dropped, so nothing repeats. This needs a server that sends generations: one that sends 0, or one generation on two update events in a row, turns it off for that stream, and a reconnect then gets a snapshot as on 1.0. The client sends `lastSeenGeneration` only when its `ProtocolVersion` is 1.1 or later and the interface it calls is 1.1 (a fallback to an older interface sends none), and the server reads it only from a 1.1 caller.

Over gRPC no frame has an event id, so no reconnect counts as progress: the five reconnect attempts are not renewed by new events. The server-side stream limits apply as on the other bindings. A custom transport can implement `IResumableTransport` to get `Last-Event-ID` resume.

gRPC streams have no keep-alive messages, so `StreamKeepAliveTimeout` does not apply: a timer on a quiet gRPC stream could not tell a quiet task from a dead connection. HTTP/2 pings do that instead. A channel the SDK makes itself (no `GrpcChannelOptions` given) pings after 5 minutes without a frame from the server while a call is open, and closes the connection if the answer does not come within 20 seconds. The stream then fails with `UNAVAILABLE` and `ObserveTaskAsync` reconnects. The delay is not shorter because gRPC servers in Go, Java and C core refuse more frequent pings by default (they answer `GOAWAY` with `too_many_pings`). The SDK's own server (Kestrel) does not limit pings: with your own `GrpcChannelOptions`, set `KeepAlivePingDelay` and `KeepAlivePingTimeout` on the `SocketsHttpHandler` you pass as `HttpHandler`.

## What can go wrong

- `UnsupportedOperation` on subscribe: the task is terminal. Call `GetTask`, or use `ObserveTaskAsync`, which returns the final snapshot.
- `RATE_LIMITED` when opening a stream: too many streams for this task or caller. The error carries a retry hint of one second.
- A resume returns a snapshot, not the missed events: the replay buffer was too small, or the task finished more than `TerminalRetention` ago.
- Events repeat after a reconnect: the peer sends no event ids, or you use gRPC on A2A 1.0 (on 1.1 the client resumes by generation).
- A proxy buffers the stream: the server sends `X-Accel-Buffering: no` and `Cache-Control: no-cache`. Check the proxy's own buffering settings and its idle timeout against `KeepAliveInterval`. A proxy that buffers also holds back the keep-alives: raise `StreamKeepAliveTimeout` or fix the buffering, or the client reconnects every time it expires.
- A stream fails with an `IOException` about `StreamKeepAliveTimeout`: nothing arrived for that long after the server had sent keep-alives. The connection was dropped on the way, or the server's `KeepAliveInterval` is longer than the client's timeout.

## See also

- [Bindings](bindings.md)
- [Push notifications](push-notifications.md): events delivered to a webhook instead of a stream.
- [Retention](retention.md): when task data is purged.
- [Durable stores](durable-stores.md): the event log across restarts and instances.
- [Limits and rate limiting](limits-and-rate-limiting.md)
