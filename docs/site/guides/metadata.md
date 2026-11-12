# Metadata keys

A2A messages, tasks, artifacts and events carry a free-form `metadata` object. The spec does not reserve any key in it. This page lists the keys this SDK reads or writes, and what to do with keys of your own. Read it before you add metadata, so your keys do not clash with the SDK's.

## Keys the SDK uses

| Key | Where | Written by | Meaning | More |
|---|---|---|---|---|
| `a2a.message.recordedAt` | Message (history) | Server | When the message was added to the task history, UTC ISO 8601. Informational: the history order is the list order. Read it with `GetRecordedAt()`. Constant `A2AProtocol.MessageRecordedAtKey`. | [Spec decisions](spec-decisions.md) |
| `a2a.artifact.sealed` | Artifact | Server | `true` once the artifact's last chunk was stored. No more chunks can be appended. Constant `A2AProtocol.ArtifactSealedKey`. | [Streaming](streaming.md) |
| `sha256` | Artifact | Agent, through `AddHashedArtifactAsync` | Hash of the artifact's content so far: `sha256:` and 64 lowercase hex digits. Experimental (`A2AEXP003`). | [Streaming](streaming.md) |
| `a2a.cancelRequested` | CancelTask response | Server | The cancel was requested, but the execution has not stopped yet. Never stored. Constant `A2AProtocol.CancelRequestedKey`. | [Task safety](task-safety.md) |
| `a2a.approval.hash` | Status message and follow-up message | Agent and client, through `PendingApproval` | The hash that binds an approval to the exact pending operation. Constant `A2AProtocol.ApprovalHashKey`. | [Task safety](task-safety.md) |
| `a2a.approval.consumed` | Task | Server | The id of the status message whose approval was used. An approval is used at most once. Constant `A2AProtocol.ApprovalConsumedKey`. | [Task safety](task-safety.md) |
| `https://github.com/roman-ambinder/a2a/extensions/elicitation/v1` | Status message (the form) and user message (the answer) | Agent and client, through the elicitation helpers | A structured input form and its answer. The key is the extension URI, `Elicitation.MetadataKey`. | [Task safety](task-safety.md) |
| `a2a.elicitation.answered` | Task | Server | The id of the status message whose form was answered. Constant `Elicitation.AnsweredKey`. | [Task safety](task-safety.md) |
| `skillId` | Message | Client, through `SelectSkill` | The skill the caller asks for. A hint, not an authorization decision. Experimental (`A2AEXP001`). | [Skill selection](skill-selection.md) |
| `actorChain` | Message, echoed on the task | Client and server | The chain of actors on whose behalf the call is made. Nobody verifies it: treat it as untrusted text. Experimental (`A2AEXP002`). | [Authentication and ownership](auth-and-ownership.md) |
| `sha256`, `sizeBytes` | Part (a `url` part the server made from a large raw part, or an upload) | Server, with an `ArtifactObjectStore` registered | The hash of the stored object's bytes and its size. | [Artifact storage](artifact-storage.md) |
| `a2a.followUp.pendingStart` | Task | Server | Marks a committed follow-up whose agent has not started yet, so a retry can start it. Internal. | [Task safety](task-safety.md) |
| `a2a.usage` | Task | Server, from `ReportUsageAsync` | Usage so far: `{"tokens": n, "costUnits": x}`. | [Execution limits](execution-limits.md) |
| `traceparent`, `tracestate` | Message | Client, with `A2AClientOptions.PropagateTraceInMetadata` | The W3C trace context of the send, for when a proxy drops the headers. The server reads it only when no `traceparent` header came, and the idempotency fingerprint ignores it. The key names are the W3C header names; the spec reserves no key for this. | [Observability](observability.md) |

The server owns the keys it writes on tasks. An agent cannot set `a2a.approval.consumed`, `a2a.cancelRequested`, `a2a.elicitation.answered`, `a2a.followUp.pendingStart`, `actorChain` or `a2a.usage` through event metadata, and the server removes an `a2a.artifact.sealed` that an agent sets on a chunk that is not the last one. How event metadata merges into the task (shallow, last write wins) is in [Spec decisions](spec-decisions.md).

## Cost, progress, priority and batches

- **Cost.** Report usage with `ReportUsageAsync`. The total is stored in the task's `a2a.usage`, and `A2ALimits.MaxTaskTokens` and `A2ALimits.MaxTaskCostUnits` can stop a task that goes over. See [Execution limits](execution-limits.md).
- **Progress.** Neither the spec nor this SDK defines a progress key. Send progress as a working status with a message, for example `StartWorkAsync("Reading 3 of 10 files")`. Clients get it as an ordinary status update, so it needs no agreement between agents.
- **Priority and batches.** Neither the spec nor this SDK defines them, and the server does not read any such key. If you add one, it is an agreement between your agents only.

## Keys of your own

- Put your keys under a name you own, such as a URI or a reverse domain name (`com.example.region`). Plain names like `priority` can collide with another agent's.
- Do not use the keys in the table above for anything else, and avoid the `a2a.` prefix.
- Metadata is visible to the other party and, for task metadata, to everyone who can read the task. Do not put secrets in it.
- Metadata a peer sent is untrusted input, like card text: check it before you act on it.
