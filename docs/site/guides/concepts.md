# Core concepts

The words this SDK and the A2A protocol use. Read it before the feature guides, which use these terms without explaining them. The definitions here follow the protocol definition the SDK is built against, [`a2a.proto`](https://github.com/roman-ambinder/a2a/blob/main/docs/a2a.proto).

## Agent and Agent Card

An **agent** is a service that accepts work over A2A. Its public description is the **Agent Card**, served at `GET /.well-known/agent-card.json`. The card says:

- which **interfaces** the agent offers: a URL and a protocol binding (JSON-RPC, HTTP+JSON or gRPC) for each. A client uses the first interface in the card's order that it supports.
- which **capabilities** it has, for example streaming and push notifications. A streaming call, or a `GetExtendedAgentCard` call, that the card does not declare is answered with `UnsupportedOperation`; a push config call is answered with `PushNotificationNotSupported`.
- its **skills**, the things it says it can do.
- the **security schemes** a caller must satisfy.
- optionally a **signature**, so a client can check who published the card. See [Agent Card signing](agent-card-signing.md).

The server refuses to serve a card without a `Name`. `AgentCard.Create(name, description, version)` fills in the other basics (the version defaults to `1.0.0`). The server completes an empty `SupportedInterfaces` list from the bindings you mapped.

## Message, part and role

A **message** is one unit of communication. It has a `messageId`, a **role** (`ROLE_USER` for client to server, `ROLE_AGENT` for server to client) and one or more **parts**. It can carry a `contextId`, a `taskId`, metadata, the URIs of extensions it uses, and `referenceTaskIds`.

A **part** holds one of four kinds of content:

| Kind | Holds |
|---|---|
| `text` | A string |
| `raw` | Bytes (a file). In JSON it is a base64 string |
| `url` | A URL that points to the file's content |
| `data` | Structured JSON: an object, array, string, number, boolean or null |

A part can also carry a `filename`, a `mediaType` (MIME type) and metadata.

## Task

A **task** is the unit of work. A client sends a message. The agent either answers with a message directly (a stateless reply) or creates a task. A task has:

- an `id`, and a `contextId` that groups related tasks and messages;
- a **status**: a state and an optional message;
- **artifacts**, the outputs;
- a record of the interaction: the `history` field and, in A2A 1.1, the newer `timeline` (1.1 marks `history` deprecated);
- metadata;
- in A2A 1.1, a `generation` counter that goes up by exactly 1 for every state-changing mutation. Each mutation maps to one event. A 1.0 caller never sees it.

### Task states

| State | Kind | Meaning |
|---|---|---|
| `SUBMITTED` | Non-terminal | The task was accepted |
| `WORKING` | Non-terminal | The agent is processing it |
| `INPUT_REQUIRED` | Interrupted | The agent needs more input from the user |
| `AUTH_REQUIRED` | Interrupted | Authentication is needed to continue |
| `COMPLETED` | Terminal | Finished successfully |
| `FAILED` | Terminal | Finished with an error |
| `CANCELED` | Terminal | Canceled before completion |
| `REJECTED` | Terminal | The agent decided not to do the task |

Terminal states end the task: nothing leaves one (the SDK's state machine lets a terminal state win, first writer first). By default only `INPUT_REQUIRED` and `AUTH_REQUIRED` tasks accept another user message on the same task id. `FollowUpPolicy.AllowSteering` also lets callers steer `SUBMITTED` and `WORKING` tasks.

## Artifact

An **artifact** is a task output. It has an `artifactId` (unique within the task), an optional name and description, and at least one part. An agent can add or update artifacts while it works, so a result can stream in chunks. Large raw parts can be kept in object storage instead of the task. See [Artifact storage](artifact-storage.md).

## Context

A **context** (`contextId`) groups the tasks and messages of one conversation. In this SDK a context belongs to the principal that created it, and another principal gets `InvalidParams` "contextId not found", unless <xref:A2A.Server.A2AServerOptions.ContextAccess> lets others join. See [Conversation memory](conversation-memory.md) and [Spec decisions](spec-decisions.md).

## Operations

The service in `a2a.proto` has these operations:

| Operation | What it does |
|---|---|
| `SendMessage` | Send a message. Returns a message or a task |
| `SendStreamingMessage` | The same, as a stream of events |
| `SendLiveMessage` | A two-way stream (the proto declares a stream in both directions). A2A 1.1 only, experimental |
| `GetTask`, `ListTasks`, `CancelTask` | Read, list and cancel tasks |
| `SubscribeToTask` | Follow an existing task as a stream |
| `Create`, `Get`, `List`, `DeleteTaskPushNotificationConfig` | Manage webhooks for a task |
| `GetExtendedAgentCard` | A fuller card for authenticated callers |

The same operations are offered on all three bindings. See [Bindings](bindings.md).

## Events

A stream carries events: a task snapshot, a status update, an artifact update, or a message. A2A 1.1 adds a message update (for example a client's follow-up) and a stream error. Each stored event gets a sequence number in the task's event log, so a client can resume a stream after a drop. See [Streaming and resume](streaming.md).

## Tenant

A **tenant** scopes a request to one agent in a host that serves several. Task ids are unique per tenant only. See [Multi-tenancy](multi-tenancy.md).

## Extensions

An **extension** is an optional, URI-named addition to the protocol, declared on the card and activated per request through the `A2A-Extensions` header. This SDK ships [skill selection](skill-selection.md) and [structured elicitation](task-safety.md) as extensions. A required extension is enforced, and the server echoes the activated ones in the `A2A-Extensions` response header.

## Next

[Architecture](architecture.md) shows how the SDK is split into packages. [How a request flows](request-lifecycle.md) follows one `SendMessage` through the server.
