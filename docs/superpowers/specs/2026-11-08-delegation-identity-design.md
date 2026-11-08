# Delegation identity (S17) and delegation loops (S19 part)

Date: 2026-11-08. Status: implemented by the plan `docs/superpowers/plans/2026-11-08-delegation-identity.md`.
Gap: S17 in `docs/2026-11-07-a2a-gap-analysis.md` (no on-behalf-of identity; intermediaries forward raw credentials), and the actorChain hop limit part of S19.

## 1. Goal

An agent that delegates to another agent can say who it acts for, can get a smaller credential for the next hop, and cannot forward the caller's token by accident. A delegation loop stops with an error. Plain A2A v1.0 peers see no change.

## 2. What the issue says

A2A issue #2028 (open, not merged into the spec; read 2026-11-08) proposes an optional extension that reserves `actorChain` on `Message.metadata`, echoed on `Task.metadata`:

```json
{ "actorChain": {
    "origin": { "sub": "user:alice@example.com" },
    "actors": [ { "sub": "agent:coordinator", "scopes": ["repo:read", "ci:trigger"] },
                { "sub": "agent:rebase-worker", "scopes": ["repo:read"] } ] } }
```

Points the thread agrees on: the chain is **audit, not authorization**; a delegating agent appends exactly one hop and must not change earlier hops; `(iss, sub)` is the per-hop identity; a hop may carry `proof_ref`. Not settled: the extension URI and namespace, scope narrowing rules, the exact `proof_ref` format.

Because the shape is unsettled, this is an additive, ignorable extension. Fields the SDK has no property for are kept as received.

## 3. Decisions (all my call unless noted)

**D1. Model lives in `A2A.Protocol`, namespace `A2A.Delegation`.** Client and server both need it, and it is pure data. Types: `ActorChain` (`Origin`, `Actors`) and `ActorChainEntry` (`Sub`, `Iss`, `Scopes`). Both have `[JsonExtensionData]` so `proof_ref`, `session` and any later field round-trip. `ActorChain.AppendHop` returns a new chain and shares the earlier entries, so it cannot change them.

**D2. Key and URI are constants.** `ActorChain.MetadataKey` = `actorChain` (from the issue). `ActorChain.ExtensionUri` = `https://github.com/roman-ambinder/a2a/extensions/actor-chain/v1`: the issue has no URI yet and says a community URI is allowed. Nothing is published, so it can change if the issue settles on another one. `ActorChain.CreateExtension()` returns the `AgentExtension` (not required) for a card. The SDK does not add it to cards by itself, so existing cards and their signatures do not change.

**D3. Hop limit is `A2ALimits.MaxActorChainHops`, default 8, on by default.** Like every other limit. Hops are the entries in `actors`. More hops than the limit is `InvalidRequest` (the code `MaxPartsPerMessage` uses), with metadata `maxActorChainHops`, before the agent runs and with nothing stored, on SendMessage and SendStreamingMessage on all bindings. The default leaves room above the depth of 4 that the OAuth actor profile draft requires. The check reads only the array length; a request without the key pays one dictionary lookup.

The limit is a brake on loops of honest agents, not a security boundary: a caller can drop the chain. The docs say so.

**D4. Tolerant unless negotiated.** A value under `actorChain` that does not have the shape is another party's private metadata: ignored, not echoed, no error. If the caller activated the extension (`ActorChain.ExtensionUri` in `ServerCallContext.ActivatedExtensions`, so requested and declared on the card), a value without the shape is `InvalidParams`. The hop limit applies whenever `actors` is an array, activated or not, because a loop must stop even between peers that never negotiated.

**D5. Echo on the task.** A well-formed chain is copied as received to `Task.metadata["actorChain"]`: when the task is created, and when a follow-up carries one (the latest wins; a follow-up without one leaves it). The raw `JsonElement` is copied, not re-serialized. The chain in each message's metadata stays in history, so the full record is kept.

**D6. The server never hands the inbound token to agent code.** `ServerCallContext.InboundBearerToken` has a public `init` (any binding can set it) and an `internal` get (only `A2A.Server` can read it). `AgentRequestContext.ExchangeTokenAsync` is the only use, so even with exchange turned on, an executor receives a new down-scoped token and never the caller's. The ASP.NET Core binding (also used by gRPC) sets it only when an `ITokenExchanger` is registered, the caller is authenticated, and the header is `Bearer <token>`. It lives on the per-call context in memory and is never stored, logged or put in telemetry. An execution recovered on another instance has no token, so `CanExchangeToken` is false.

**D7. `ITokenExchanger` is in `A2A.Protocol`** (`A2A.Delegation`) because `A2A.Server` and `A2A.Client` do not reference each other. Registered with `A2AServerBuilder.AddTokenExchanger`. `AgentRequestContext.ExchangeTokenAsync(audience, scopes, cancellationToken)` needs an audience: an exchange with no target is not a down-scope. No exchanger, or no token: `CanExchangeToken` is false and the call throws `InvalidOperationException`.

**D8. `OAuthTokenExchanger` in `A2A.Client` (`A2A.Client.Delegation`)** is the RFC 8693 implementation, so hosts do not hand-write the form POST. It requires HTTPS (loopback allowed), never logs tokens, caps the response at 64 KiB, and refuses a redirected response. Errors throw `TokenExchangeException` with only the RFC 6749 `error` code. `A2ARequestOptions.UseBearerToken(TokenExchangeResult)` puts the result on a call.

**D9. "Never re-forwards by default" is a property we pin with tests**, not a runtime check: `A2A.Client` has no ambient credential source, and D6 gives agent code no token to copy. Tests: an A→B→C call over HTTP where C sees no `Authorization`; a reflection test that no public getter on `ServerCallContext` or `AgentRequestContext` returns the token; a source rule that `src/A2A.Client*` does not mention `IHttpContextAccessor`. A host can still read the header itself from its own `HttpContext`; the docs say not to.

## 4. Non-goals

- Enforcing scope narrowing or resolving `proof_ref`: unsettled in the issue, and a caller-written chain proves nothing.
- Using the chain for authorization, or cycle detection beyond the hop limit.
- A built-in DI registration for `OAuthTokenExchanger` in the client DI package (parallel work there).
- The S19 interrupted-state timeout: already done (`InterruptedTimeout`).

## 5. Wire compatibility

No new fields, headers or errors for a request without `actorChain`. A plain v1.0 peer ignores the metadata key. A plain v1.0 client that uses the key for something else is not affected (D4). Cards do not change unless the host lists the extension (D2). Parity tests and `interop/run.sh` stay as they are.
