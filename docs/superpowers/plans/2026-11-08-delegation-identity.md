# Delegation Identity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Carry `metadata["actorChain"]`, enforce a server-side hop limit, add an RFC 8693 token-exchange seam, and pin that inbound bearer tokens are never forwarded by default.

**Architecture:** Chain model and `ITokenExchanger` in `A2A.Protocol`; hop limit and echo inside the server's request preparation; inbound token captured by the ASP.NET Core binding and readable only inside `A2A.Server`; `OAuthTokenExchanger` in `A2A.Client`.

**Tech Stack:** .NET 8/10, System.Text.Json source generation, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-08-delegation-identity-design.md` (D-numbers below refer to it).

## Global Constraints

- Build: `dotnet build A2A.slnx -c Release` must give 0 warnings. Warnings are errors.
- Every new public member has an XML summary and a line in the project's `PublicAPI.Unshipped.txt`.
- Public `CancellationToken` parameters are named `cancellationToken`.
- JSON through `A2AJson` / `A2AJsonContext` only; add new types with `[JsonSerializable]`. No reflection serialization.
- No `InternalsVisibleTo` between shipped packages. `A2A.Client` and `A2A.Server` do not reference each other.
- Namespaces follow folders: `src/A2A.Protocol/Delegation` is `A2A.Delegation`; `src/A2A.Client/Delegation` is `A2A.Client.Delegation`.
- Do not touch `A2ACardResolver` or `src/A2A.Client.DependencyInjection` (parallel sessions work there).
- Tokens never appear in logs, telemetry, stores, exception messages or `ToString()`.
- Do not change parity tests or `interop/`.
- Code comments and docs use plain, short English.

## Review Focus

1. A plain peer that uses `actorChain` for its own private data (a string, or an object with no `actors`): no error, no echo. Test in Task 3.
2. A prior hop with fields the SDK does not model (`proof_ref`, `session`) must survive `AppendHop` and `SetActorChain` unchanged. Test in Task 1.
3. A follow-up without a chain keeps the task's echo; one with a chain replaces it. Test in Task 3.
4. The inbound token must not reach the stored task, the event log or `ToString()`. Test in Task 4.
5. `Authorization: Basic ...`, `Bearer` with no value, or an unauthenticated caller: nothing captured, `CanExchangeToken` false. Test in Task 4.

---

### Task 1: Actor chain model

**Files:**
- Create: `src/A2A.Protocol/Delegation/ActorChain.cs`
- Modify: `src/A2A.Protocol/Json/A2AJsonContext.cs`, `src/A2A.Protocol/PublicAPI.Unshipped.txt`
- Test: `tests/A2A.Tests/Delegation/ActorChainTests.cs`

**Interfaces:**
- Produces (namespace `A2A.Delegation`):
  - `sealed class ActorChainEntry`: `required string Sub`, `string? Iss`, `List<string>? Scopes`, `[JsonExtensionData] Dictionary<string, JsonElement>? AdditionalProperties`.
  - `sealed class ActorChain`: `const string MetadataKey = "actorChain"`, `const string ExtensionUri = "https://github.com/roman-ambinder/a2a/extensions/actor-chain/v1"`, `ActorChainEntry? Origin`, `List<ActorChainEntry> Actors` (default empty), `[JsonExtensionData] AdditionalProperties`, `static ActorChain Start(ActorChainEntry origin)`, `ActorChain AppendHop(ActorChainEntry hop)`, `static AgentExtension CreateExtension()` (`Uri = ExtensionUri`, `Required = false`, short description).
  - `static class ActorChainMessageExtensions`: `ActorChain? GetActorChain(this Message message)`, `ActorChain? GetActorChain(this AgentTask task)`, `Message SetActorChain(this Message message, ActorChain chain)` (returns the same message; writes the key, creating `Metadata` when null).
  - JSON names: `origin`, `actors`, `sub`, `iss`, `scopes`.
  - `static bool TryReadActorChain(Dictionary<string, JsonElement>? metadata, out ActorChain? chain)` on `ActorChain`: true only for a well-formed chain (object, `actors` array of objects each with string `sub`; `origin` absent or such an object). Never throws on bad shape: a `JsonException` from deserializing means false.

- [ ] **Step 1: Write failing tests** in `ActorChainTests`:
  - `Round_trips_the_issue_2028_shape`: parse the spec's JSON from a message's metadata; `Origin.Sub == "user:alice@example.com"`, `Actors.Count == 2`, `Actors[0].Scopes` is `["repo:read","ci:trigger"]`.
  - `AppendHop_keeps_earlier_hops_and_does_not_change_the_source`: source has 1 hop; result has 2; source still has 1; `result.Actors[0]` is the same instance as `source.Actors[0]`.
  - `Unknown_hop_fields_survive_append_and_set`: a hop with `"proof_ref":"sha256:abc"` and `"session":"s1"`, parse, `AppendHop`, `SetActorChain` on a new message, serialize that message with `A2AJson`, assert the output's first hop has `proof_ref` = `sha256:abc` and `session` = `s1`.
  - `GetActorChain_returns_null_for_other_shapes` (Theory: `"text"`, `42`, `{}`, `{"actors":"x"}`, `{"actors":[1]}`, `{"actors":[{"iss":"x"}]}`): returns null and does not throw.
  - `GetActorChain_returns_null_without_metadata`.
  - `CreateExtension_is_optional_and_names_the_uri`.
- [ ] **Step 2: Run** `dotnet test tests/A2A.Tests -f net10.0 --filter ActorChainTests`. Expected: FAIL to compile (types missing).
- [ ] **Step 3: Implement** in `ActorChain.cs`. `SetActorChain` writes `JsonSerializer.SerializeToElement(chain, A2AJson.GetTypeInfo<ActorChain>())`. Add `[JsonSerializable(typeof(ActorChain))]` and `ActorChainEntry` to the context. Update `PublicAPI.Unshipped.txt`.
- [ ] **Step 4: Run** the same filter. Expected: PASS. Then `dotnet build A2A.slnx -c Release`: 0 warnings.
- [ ] **Step 5: Commit** `feat(protocol): add the actorChain model (A2A #2028)`.

### Task 2: Token exchange seam and RFC 8693 client

**Files:**
- Create: `src/A2A.Protocol/Delegation/ITokenExchanger.cs`, `src/A2A.Client/Delegation/OAuthTokenExchanger.cs`, `src/A2A.Client/Delegation/DelegationRequestOptionsExtensions.cs`
- Modify: `src/A2A.Protocol/PublicAPI.Unshipped.txt`, `src/A2A.Client/PublicAPI.Unshipped.txt`
- Test: `tests/A2A.Tests/Delegation/OAuthTokenExchangerTests.cs`

**Interfaces:**
- Produces in `A2A.Delegation` (`A2A.Protocol`):
  - `interface ITokenExchanger { ValueTask<TokenExchangeResult> ExchangeAsync(TokenExchangeRequest request, CancellationToken cancellationToken); }`
  - `static class TokenTypes`: `AccessToken = "urn:ietf:params:oauth:token-type:access_token"`, `Jwt = "urn:ietf:params:oauth:token-type:jwt"`.
  - `sealed class TokenExchangeRequest` (init properties): `required string SubjectToken`, `string SubjectTokenType = TokenTypes.AccessToken`, `string? Audience`, `string? Resource`, `IReadOnlyList<string>? Scopes`, `string? RequestedTokenType`, `string? ActorToken`, `string? ActorTokenType`. `ToString()` returns the type name only.
  - `sealed class TokenExchangeResult` (init): `required string AccessToken`, `string TokenType = "Bearer"`, `string? IssuedTokenType`, `TimeSpan? ExpiresIn`, `string? Scope`. `ToString()` returns the type name only.
  - `sealed class TokenExchangeException : Exception`: `TokenExchangeException(string message, string? error = null, Exception? inner = null)`, `string? Error`.
- Produces in `A2A.Client.Delegation`:
  - `sealed class OAuthTokenExchangerOptions`: `required Uri TokenEndpoint`, `string? ClientId`, `string? ClientSecret`, `Func<HttpRequestMessage, CancellationToken, ValueTask>? ConfigureRequest`.
  - `sealed class OAuthTokenExchanger(HttpClient httpClient, OAuthTokenExchangerOptions options) : ITokenExchanger`.
  - `static class DelegationRequestOptionsExtensions`: `A2ARequestOptions UseBearerToken(this A2ARequestOptions options, TokenExchangeResult token)` sets `Headers["Authorization"] = "Bearer " + AccessToken`; throws `InvalidOperationException` when `TokenType` is not `Bearer` (ignoring case).

- [ ] **Step 1: Write failing tests** in `OAuthTokenExchangerTests`, using a fake `HttpMessageHandler` that records the request and returns a canned response:
  - `Posts_the_rfc8693_form`: asserts POST to the endpoint, `application/x-www-form-urlencoded`, fields `grant_type=urn:ietf:params:oauth:grant-type:token-exchange`, `subject_token`, `subject_token_type`, `audience`, `resource`, `scope` (space-joined), `requested_token_type`, `actor_token`, `actor_token_type`; absent optionals are not sent.
  - `Reads_the_token_response`: `{"access_token":"new","issued_token_type":"...","token_type":"Bearer","expires_in":300,"scope":"a b"}` maps to the result; `ExpiresIn == 300s`.
  - `Sends_client_credentials_as_basic_auth`: `ClientId`/`ClientSecret` become `Authorization: Basic` (each value form-url-encoded before base64, RFC 6749 §2.3.1).
  - `Error_response_throws_with_only_the_error_code`: 400 `{"error":"invalid_target","error_description":"secret detail"}`; `TokenExchangeException.Error == "invalid_target"`; message does not contain `secret detail` or the subject token.
  - `Refuses_http_endpoint` (ctor throws `ArgumentException` for `http://example.com/token`); `Allows_loopback_http`.
  - `Refuses_a_redirected_response`: handler sets `response.RequestMessage` to a different URI; throws.
  - `Refuses_an_oversized_response`: a body over 64 KiB; throws.
  - `Missing_access_token_throws`.
  - `ToString_hides_tokens`: request and result `ToString()` do not contain the token text.
  - `UseBearerToken_sets_the_header` and `UseBearerToken_refuses_other_token_types`.
- [ ] **Step 2: Run** `dotnet test tests/A2A.Tests -f net10.0 --filter OAuthTokenExchangerTests`. Expected: FAIL to compile.
- [ ] **Step 3: Implement.** Parse with `JsonDocument` from a size-capped read (`HttpCompletionOption.ResponseHeadersRead`, read at most 64 KiB + 1). `https` or loopback only. Throw `TokenExchangeException` with a fixed message and the `error` code only, truncated to 128 characters and limited to RFC 6749 characters (`%x20-21 / %x23-5B / %x5D-7E`), else null. `ConfigureRequest` runs last. Update both PublicAPI files.
- [ ] **Step 4: Run** the filter. Expected: PASS. Then `dotnet build A2A.slnx -c Release`: 0 warnings.
- [ ] **Step 5: Commit** `feat: add ITokenExchanger and an RFC 8693 token exchange client`.

### Task 3: Hop limit and task echo

**Files:**
- Create: `src/A2A.Server/Pipeline/ActorChainValidator.cs` (internal)
- Modify: `src/A2A.Server/A2ALimits.cs`, `src/A2A.Server/A2ARequestHandler.cs` (`PrepareAsync`), `src/A2A.Server/Execution/TaskExecutionEngine.cs` (creation of `initial`), `src/A2A.Server/PublicAPI.Unshipped.txt`
- Test: `tests/A2A.Tests/Delegation/ActorChainServerTests.cs`

**Interfaces:**
- Consumes: Task 1 (`ActorChain.MetadataKey`, `ExtensionUri`, `TryReadActorChain`).
- Produces: `A2ALimits.MaxActorChainHops` (`int`, default 8; XML doc says what counts as a hop and that this is a loop brake, not a security boundary).
- Internal: `ActorChainValidator.Validate(Message message, A2ALimits limits, ServerCallContext call)` and `ActorChainValidator.WellFormedChain(Message message) -> JsonElement?`.

- [ ] **Step 1: Write failing tests** in `ActorChainServerTests`, as `[Theory, MemberData(nameof(Bindings))]` over `BindingTests.Bindings` where a test goes through a binding (see `tests/A2A.Tests/Pipeline/OperationFilterTests.cs` and `TestHost.cs` for the pattern):
  - `Chain_at_the_limit_is_accepted`: 8 hops, default limit: agent runs.
  - `Chain_over_the_limit_is_refused_before_the_agent_runs`: 9 hops: `A2AException` with `InvalidRequest`; agent call count 0; no task stored.
  - `Streaming_chain_over_the_limit_is_refused`: same through SendStreamingMessage.
  - `Limit_is_configurable`: `Limits.MaxActorChainHops = 2`; 3 hops refused, 2 accepted.
  - `Message_without_a_chain_is_not_affected`.
  - `Private_metadata_under_the_key_is_ignored_and_not_echoed` (Review Focus 1): values `"text"` and `{"foo":1}`: accepted; task `Metadata` has no `actorChain`.
  - `Malformed_chain_is_refused_when_the_extension_is_activated`: card declares `ActorChain.CreateExtension()`, request lists the URI in `A2A-Extensions`, value `{"foo":1}`: `InvalidParams`.
  - `Well_formed_chain_is_echoed_on_a_new_task`: task `Metadata["actorChain"]` equals the sent JSON, including an unknown hop field.
  - `Follow_up_replaces_or_keeps_the_echo` (Review Focus 3): a follow-up with a different chain replaces it; a follow-up with none keeps it.
- [ ] **Step 2: Run** `dotnet test tests/A2A.Tests -f net10.0 --filter ActorChainServerTests`. Expected: FAIL.
- [ ] **Step 3: Implement.**
  - `Validate`: return at once when `message.Metadata` is null or lacks the key. If the value is an object with array `actors` and its length is over the limit, throw `new A2AException(A2AErrorCode.InvalidRequest, $"The actor chain has more than {limit} hops") { Metadata = new() { ["maxActorChainHops"] = limit (invariant string) } }`. Then, if `call.ActivatedExtensions` contains `ActorChain.ExtensionUri` and the chain is not well-formed, throw `A2AException.InvalidParams("metadata.actorChain is not a valid actor chain")`.
  - Call it in `PrepareAsync` right after `RequestLimitValidator.Validate`.
  - Echo in `PrepareAsync` (follow-up branch, next to the `consume` line): when `WellFormedChain(message)` has a value, `(next.Metadata ??= [])[ActorChain.MetadataKey] = value`.
  - Echo at creation in `TaskExecutionEngine`: `Metadata = WellFormedChain(historyMessage)` into a new dictionary when present. If the new-task message is not available there as `historyMessage`, pass it from the `Execution`.
- [ ] **Step 4: Run** the filter, then `dotnet test tests/A2A.Tests -f net10.0`. Expected: all PASS. `dotnet build A2A.slnx -c Release`: 0 warnings.
- [ ] **Step 5: Commit** `feat(server): enforce the actor chain hop limit and echo the chain on the task`.

### Task 4: Inbound token capture, exchange on the agent context, no-forward guarantee, docs

**Files:**
- Modify: `src/A2A.Server/ServerCallContext.cs`, `src/A2A.Server/AgentExecutor.cs`, `src/A2A.Server/A2ARequestHandler.cs`, `src/A2A.Server/ServiceCollectionExtensions.cs`, `src/A2A.Server.AspNetCore/HttpPlumbing.cs`, `src/A2A.Server/PublicAPI.Unshipped.txt`, `README.md`, `docs/2026-11-07-a2a-gap-analysis.md`
- Test: `tests/A2A.Tests/Delegation/TokenExchangeServerTests.cs`, `tests/A2A.Tests/PackageRulesTests.cs`

**Interfaces:**
- Consumes: Task 2 (`ITokenExchanger`, `TokenExchangeRequest`, `TokenExchangeResult`, `UseBearerToken`), Task 1 chain.
- Produces:
  - `ServerCallContext.InboundBearerToken`: `public string? InboundBearerToken { internal get; init; }`. XML doc: set by a binding only when a token exchanger is registered; readable only by the server core.
  - `AgentRequestContext.CanExchangeToken` (`bool`).
  - `AgentRequestContext.ExchangeTokenAsync(string audience, IReadOnlyList<string>? scopes = null, CancellationToken cancellationToken = default) -> ValueTask<TokenExchangeResult>`. Throws `ArgumentException` for an empty audience; `InvalidOperationException` when `CanExchangeToken` is false. Calls the exchanger with `SubjectToken` = inbound token, `Audience`, `Scopes`.
  - `A2AServerBuilder.AddTokenExchanger<T>()` (singleton, replaces an earlier one) and `AddTokenExchanger(ITokenExchanger exchanger)`.
  - Internal init on `AgentRequestContext`: `internal ITokenExchanger? TokenExchanger { get; init; }`, set in `PrepareAsync`.
  - `A2ARequestHandler` constructor: add a trailing optional `ITokenExchanger? tokenExchanger = null`.

- [ ] **Step 1: Write failing tests** in `TokenExchangeServerTests` (use a recording `ITokenExchanger` as the second implementation; authenticate with the test scheme `TestHost` offers or a test auth handler):
  - `Without_an_exchanger_the_token_is_not_captured`: `CanExchangeToken` false; `ExchangeTokenAsync` throws `InvalidOperationException`.
  - `Exchanger_receives_the_inbound_token_and_the_target`: binding tests over all three bindings; `SubjectToken` equals the caller's bearer; `Audience` and `Scopes` as passed.
  - `Empty_audience_is_refused`.
  - `Nothing_is_captured_for` (Review Focus 5; Theory: `Basic abc`, `Bearer`, `Bearer   `, unauthenticated caller): `CanExchangeToken` false.
  - `No_public_getter_returns_the_inbound_token`: reflection on `ServerCallContext`: `typeof(ServerCallContext).GetProperty("InboundBearerToken")!.GetGetMethod(nonPublic: false)` is null; and `AgentRequestContext` has no public property named `InboundBearerToken` or `SubjectToken`.
  - `Inbound_token_is_not_stored` (Review Focus 4): a unique token string; after a run, the serialized task and the event log hold no copy of it.
  - `Intermediary_does_not_forward_the_callers_token`: host B's executor calls a stub downstream with a default `A2AClient` (a recording `HttpMessageHandler`); the downstream request has no `Authorization` header, though the caller sent one.
  - `Exchanged_token_is_what_reaches_the_next_agent`: B calls `ExchangeTokenAsync`, then `new A2ARequestOptions().UseBearerToken(result)`; downstream sees `Bearer <exchanged>`, not the inbound token.
  - `PackageRulesTests.Client_sources_do_not_read_ambient_http_context`: no file under `src/A2A.Client*` (skipping `bin`/`obj`) contains `IHttpContextAccessor`.
- [ ] **Step 2: Run** `dotnet test tests/A2A.Tests -f net10.0 --filter "TokenExchangeServerTests|Client_sources_do_not"`. Expected: FAIL.
- [ ] **Step 3: Implement.** In `HttpPlumbing.CreateCallContext`, only when `principal` is not anonymous and `services.GetService<ITokenExchanger>()` is not null: read the `Authorization` header, accept scheme `Bearer` ignoring case, trim, ignore empty. Confirm the gRPC binding builds its context through this method; if not, set it there too. Pass the token to `AgentRequestContext` through `Call`; no store or log touches it.
- [ ] **Step 4: Update docs.** README: a new `Extending` bullet for `ITokenExchanger`; a `Behavior and spec decisions` Security bullet on delegation: `actorChain` is audit, not authorization; hop limit; agent code never gets the inbound token; exchange flow; do not copy the `Authorization` header from your own `HttpContext`; example of `ctx.Message.GetActorChain()?.AppendHop(...)`, `SetActorChain`, `ExchangeTokenAsync`, `UseBearerToken`. Gap analysis: mark S17 done and S19's hop-limit part done, in the file's existing style.
- [ ] **Step 5: Run** full gates: `dotnet build A2A.slnx -c Release` (0 warnings), `dotnet test tests/A2A.Tests -f net10.0`, `dotnet test tests/A2A.Grpc.Tests`. Expected: PASS.
- [ ] **Step 6: Commit** `feat(server): token exchange on the agent context; never expose or forward the inbound token`.
