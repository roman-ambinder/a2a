# Streaming and resume

A streaming call sends the events of a task as they happen: status changes, artifact chunks and messages. Over JSON-RPC and HTTP+JSON the events travel as Server-Sent Events (SSE). Use streaming for long tasks. If the connection drops, the client resumes the stream and no event is lost or repeated.

## Turn it on

The card must declare streaming. Without it, the server refuses streaming calls with `UnsupportedOperation`, and the client fails fast before it sends anything.

[!code-csharp[](../snippets/Streaming.cs#Capability)]

## Send and stream

<xref:A2A.Client.A2AClient.SendStreamingMessageAsync*> sends a message and returns the events of the task, in order:

[!code-csharp[](../snippets/Streaming.cs#Send)]

The first event is the task, or a direct message. A direct message is the only event, and the stream ends. Events of other executions of the same task, such as the `CANCELED` event of a `CancelTask`, arrive on the same stream.

## Follow a task

To watch a task you did not start on this connection, use <xref:A2A.Client.A2AClient.ObserveTaskAsync*>. It subscribes, and when the connection drops it reconnects and resumes. This example starts a task without waiting for it, then follows it:

[!code-csharp[](../snippets/Streaming.cs#Observe)]

`ObserveTaskAsync` works like this:

- It reconnects after 0.5, 1, 2, 4 and 8 seconds. After the fifth failed reconnect, the error is thrown.
- A reconnect that delivers a new event gives the client a fresh set of attempts.
- It sends the last event id it saw in the `Last-Event-ID` header. The server continues after that event.
- It drops any event whose sequence it already delivered.
- If the task is already finished, you get its final snapshot as a `TaskEvent`.
- The same options (headers, credentials) are used on every reconnect. `SendStreamingMessageAsync` resumes in the same way once the first event has shown the task id.

<xref:A2A.Client.A2AClient.SubscribeToTaskAsync*> is the plain call. It subscribes once and does not reconnect:

[!code-csharp[](../snippets/Streaming.cs#Subscribe)]

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

When a stream ends before a terminal event, for example at `MaxStreamDuration`, the client treats it like a dropped connection and resumes it after the backoff. It does not resume after a terminal event or a direct message.

## The replay buffer

The event store keeps the last events of each task so that a client can resume. These options are on <xref:A2A.Server.StreamingOptions>, under `A2AServerOptions.Streaming`:

| Option | Default | Meaning |
|---|---|---|
| `ReplayBufferSize` | 1000 | Events kept per task. Older events are dropped. With the in-memory store, each event costs about twice its model size, because the encoded JSON is kept too. |
| `TerminalRetention` | 10 minutes | How long the events of a finished task are kept |
| `KeepAliveInterval` | 15 seconds | Interval of SSE comments (`: ka`) on an idle stream. Zero turns them off. |
| `ChannelCapacity` | 256 | Size of each per-task event queue. Values below 1 count as 1. |

[!code-csharp[](../snippets/Streaming.cs#Options)]

When a queue is full, the agent's next `TaskUpdater` write waits until the server catches up. A stream that falls behind drops its queued events and fills the gap from the event store, in order. A snapshot replaces events the store no longer has. Push delivery keeps every event.

### When the event log is gone

If a client resumes after the events were trimmed or purged, the server sends a task snapshot instead. The client yields the snapshot as a `TaskEvent`. If the snapshot has an id with a sequence below the client's cursor, the server lost its log and started over. The client then takes the snapshot and continues from its sequence. A snapshot of a terminal task ends the stream. Treat a `TaskEvent` in the middle of a stream as a state to replace what you know, not as one more step.

## Artifact chunks

An agent can send an artifact in chunks. Each chunk is an artifact update event with an `artifactId`. The flag `append` says if the parts go to an artifact that exists. The flag `lastChunk` says it is the final chunk.

[!code-csharp[](../snippets/Streaming.cs#Chunks)]

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

Use `TaskUpdater.AddHashedArtifactAsync` for every chunk. It takes the same arguments as `AddArtifactAsync`, sets `sha256` for you, and overwrites any `sha256` in your metadata. It does not keep the content: the hash continues from chunk to chunk. For an append to an artifact that an earlier run stored, it first hashes the stored parts.

[!code-csharp[](../snippets/Streaming.cs#HashedChunks)]

Do not mix plain and hashed chunks for one artifact. A plain `AddArtifactAsync` with `append: true` on an artifact that is being hashed throws, because the hash would be wrong. A hashed append to an artifact that this run sent without the hash helper throws too.

#### Consumer

Give `ArtifactVerifier` each event of one task, in order. For an artifact update, it returns one result for the chunk. For a task snapshot, it starts over and returns one result per artifact.

[!code-csharp[](../snippets/Streaming.cs#VerifyStream)]

For a task from `GetTask`, check each artifact with `ArtifactVerifier.Verify`:

[!code-csharp[](../snippets/Streaming.cs#VerifyTask)]

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
| `StreamReauthInterval` | 5 minutes | The caller is authorized again. If denied, the stream ends. Zero or `Timeout.InfiniteTimeSpan` turns it off. |
| `MaxEventBytes` | 4 MB | An event above this is never stored or sent. The task fails and the agent is stopped. Send large artifacts in chunks. |

The stream counters are per process. Opening a stream also counts against the rate limit like any other request.

## On the wire

The response has `Content-Type: text/event-stream`. Each frame has an `id:` line (for stored events) and a `data:` line.

- JSON-RPC: `data` is a JSON-RPC response. An error after the stream started is a last JSON-RPC error frame.
- HTTP+JSON: `data` is the event. An error after the stream started is a last frame named `error` with a `google.rpc.Status` body. The client accepts both forms.
- The client skips an event type it does not know instead of ending the stream.
- HTTP+JSON subscribe is `GET` or `POST /tasks/{id}:subscribe`. The server accepts both. The client uses `POST` unless you set `RestSubscribeMethod` on <xref:A2A.Client.A2AClientOptions>.
- Streaming calls have no client timeout and are not retried as unary calls are. Resume is separate and is described above.

## gRPC

gRPC streams carry no event ids, so resume is not supported on gRPC. After a lost stream, call `SubscribeToTask` again. You get a task snapshot, then live events. The server-side stream limits apply as on the other bindings.

The client transport does not implement <xref:A2A.Client.Streaming.IResumableTransport>. `ObserveTaskAsync` still reconnects over gRPC, but each reconnect subscribes again from the start, and events can be delivered again. Design the consumer to tolerate repeats. The same holds for a peer that sends no SSE ids. A custom transport can implement `IResumableTransport` to get resume.

## What can go wrong

- `UnsupportedOperation` on subscribe: the task is terminal. Call `GetTask`, or use `ObserveTaskAsync`, which returns the final snapshot.
- `RATE_LIMITED` when opening a stream: too many streams for this task or caller. The error carries a retry hint of one second.
- A resume returns a snapshot, not the missed events: the replay buffer was too small, or the task finished more than `TerminalRetention` ago.
- Events repeat after a reconnect: the peer sends no event ids, or you use gRPC.
- A proxy buffers the stream: the server sends `X-Accel-Buffering: no` and `Cache-Control: no-cache`. Check the proxy's own buffering settings and its idle timeout against `KeepAliveInterval`.

## See also

- [Bindings](bindings.md)
- [Push notifications](push-notifications.md): events delivered to a webhook instead of a stream.
- [Retention](retention.md): when task data is purged.
- [Durable stores](durable-stores.md): the event log across restarts and instances.
- [Limits and rate limiting](limits-and-rate-limiting.md)
