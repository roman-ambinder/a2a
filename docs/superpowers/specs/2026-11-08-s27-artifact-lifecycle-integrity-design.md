# S27: artifact lifecycle and optional integrity

Closes gap S27. Sources: A2A#1231 (open, TSC review), A2A#1140 (open proposal). Metadata merge rules are S28 and do not change here.

## Lifecycle rules

Applied per `artifactId` within a task.

| Stored artifact | Event | Result |
|---|---|---|
| none | `append=false` | Create. |
| none | `append=true` | Create. #1231 agrees; today's behavior. Python and Java drop it, Go fails. |
| open | `append=false` | Replace. |
| open | `append=true` | Append parts, merge metadata as today. |
| sealed | `append=false` | Replace. The new artifact is open unless this event has `lastChunk=true`. |
| sealed | `append=true` | Rejected. See below. |

`lastChunk=true` seals the artifact the event leaves behind.

One choice differs from the last .NET proposal in #1231, which also rejects `append=false` after a seal. The TSC feedback in the same issue allows replacing, and the issue's correction use case needs it. Replace is an explicit act, so it cannot hide a bug the way a stray append can.

The conformance suites (a2a-tck, ACTS) have no test for any of these cases. The agents in `conformance/` and `interop/` never touch an artifact after `lastChunk`, so nothing there changes.

### Rejecting an append to a sealed artifact

**`TaskUpdater` (main path).** `AddArtifactAsync` throws `InvalidOperationException` at the call, before anything is queued:

> Artifact 'x' is sealed: lastChunk was already sent. Send it with append: false to replace it, or use a new artifact id.

The executor can catch this and go on. If it does not, the task ends `FAILED` with the existing text "The agent failed to process the request.", and the exception is logged. This is the same way the updater reports other misuse.

The updater knows what this run sent, and what earlier runs stored. The engine seeds it from `AgentRequestContext.Task` and from the task passed to `CancelAsync`.

**Engine (backstop).** Two executions of one task can race, and only the engine sees the final order. `ApplyAndStoreAsync` drops an append to a sealed artifact before commit, inside the retry loop. The event is not stored, published or pushed. A new warning `ArtifactAppendAfterSeal` names the task and artifact. The task is not failed, because the other execution may be healthy.

### Where the seal is stored

The stored artifact gets `metadata["a2a.artifact.sealed"] = true` (`A2AProtocol.ArtifactSealedKey`). Only the server writes it, as with `a2a.approval.consumed`. It needs no store schema change, so it works on Postgres and Redis and survives a restart.

It is visible in `GetTask` and in snapshots. That is useful: a snapshot has no `lastChunk`, so this is how a reader tells a finished artifact from a partial one.

The events on the wire are not changed. The engine stores a new `Artifact` instance with the key, because the encoded event shares the agent's instance. If an agent sets this key itself, the engine removes it from the stored artifact unless `lastChunk=true`.

## Empty parts

- `parts: []` is allowed only with `append=true` on an artifact that exists. It seals the artifact or updates its metadata. #1231 supports this (`yarolegovich`, `Tehsmash`); the spec text still says at least one part, so a strict peer may reject such an event. Send the last parts with `lastChunk=true` when you can.
- `parts: []` otherwise (`append=false`, or an unknown artifact) stays invalid. It would store an artifact with no content. `TaskUpdater` throws `ArgumentException` at the call. The engine keeps `A2AValidator.ValidateArtifact` as the backstop.
- A part with empty content (`text: ""`) is a normal part. It passes through and adds no bytes to a hash.
- A null entry in `parts` stays invalid.

## Optional integrity

Off unless the producer uses the helper. No hash is computed otherwise. This detects corruption and bad reassembly: lost, repeated or reordered chunks. It is not authenticity. Anyone who can change the content can change an unsigned hash. Signatures from #1140 are out of scope.

### What #1140 settles

The issue is an open proposal. Comments agree on two things, and this design uses both: the value format `sha256:<64 lowercase hex>`, and RFC 8785 (JCS) for canonical JSON. Field placement and signatures are not settled. It uses `metadata`, so it is ignored by peers that do not know it.

#1140 hashes the JCS of a whole artifact object. That does not work for chunks: it needs the whole artifact in memory, and it would include the hash itself. So JCS is used for data parts only.

### Bytes hashed

SHA-256 over the content bytes of the parts, in order, with nothing between them:

| Part | Bytes |
|---|---|
| text | UTF-8 of `text`. No BOM, no Unicode normalization. |
| raw | The decoded bytes, not the base64. |
| url | UTF-8 of the URL string as sent on the wire (`Uri.OriginalString`). The URL is protected. The content behind it is not fetched or hashed. |
| data | UTF-8 of the RFC 8785 form of `data` (`JsonCanonicalizer`). |

Not hashed: part `metadata`, `filename`, `mediaType`; artifact `name`, `description`, `metadata`, `extensions`.

With no separator, a file sent as raw chunks hashes to the plain SHA-256 of the file. The cost is that moving a part boundary does not change the hash, which is fine for this purpose.

A number outside the double range in a data part cannot be put in JCS form. The producer helper throws, and the consumer reports `Unverifiable`.

### Cumulative value in `metadata.sha256`

`metadata["sha256"]` on each event is the hash of the artifact **so far**: every part from the start up to and including this event. The hash on the last chunk is the hash of the final artifact. A seal-only event repeats it.

A hash of only the chunk would be wrong after the existing metadata merge: the stored artifact would keep the hash of its last chunk. With a cumulative value, one key covers each chunk and the final artifact, and the stored `metadata.sha256` always matches the stored parts. The first chunk that mismatches is the first bad one.

`append=false` starts over.

### Producer

`TaskUpdater.AddHashedArtifactAsync(parts, artifactId, name, description, append, lastChunk, metadata, ct)`, returning the artifact id. It has the same rules as `AddArtifactAsync` and overwrites any `sha256` in `metadata`.

The hash continues across chunks in the updater with `IncrementalHash`, so nothing is buffered. For an append to an artifact from an earlier run, it first hashes the stored parts. State is released on seal and when the agent returns.

Plain `AddArtifactAsync(append: true)` on an artifact this updater is hashing throws `InvalidOperationException`, because it would make the hash wrong.

### Consumer

`A2A.Client.ArtifactVerifier` (`IDisposable`):

- `IReadOnlyList<ArtifactVerification> Observe(StreamResponse evt)`. Feed it each event. An artifact update returns one result. A task snapshot resets state and returns one result per artifact. Other events return none.
- `static ArtifactVerification Verify(Artifact artifact)`, for a task from `GetTask`.

`ArtifactVerification(ArtifactId, Status, Expected, Actual)`, with `Status`:

| Status | Meaning |
|---|---|
| `NoHash` | No `sha256` in the metadata. |
| `Verified` | Match. |
| `Mismatch` | Different hash. `Expected` and `Actual` are set. |
| `Unverifiable` | Value is not a string or not `sha256:` + 64 lowercase hex (fail closed on an unknown scheme, as #1140 asks), or the parts could not be hashed. |

The verifier never throws on bad content and does not end the stream. The caller decides what to do. The client does not enforce the seal.

`ArtifactHash` in `A2A.Protocol` (`A2A.Integrity`) holds what both sides share: `MetadataKey`, `Compute(parts)`, `Update(IncrementalHash, parts)`, `Format(digest)`.

## Not doing

Signatures, `schemaRef` and `links` from #1140. Metadata merge changes (S28). Name, description or extensions merge changes from #1231. Fetching URL content. Seal enforcement on the client.

## Tests

- Hash vectors for text, raw, url, data and multi-part, from values computed outside .NET (`shasum`, Python).
- `TaskUpdater`: append after seal throws; replace after seal works; empty-parts rules; mixing plain append with hashed throws.
- Follow-up run: seal is still enforced, and hashing continues over stored parts.
- Engine: backstop drop when the updater is bypassed; agent-set seal key removed.
- All three bindings (JSON-RPC, HTTP+JSON, gRPC): a seal-only event with empty parts round-trips; hashed chunks verify through `ArtifactVerifier`; a tampered chunk reports `Mismatch`; `GetTask` verifies and shows the seal key.
