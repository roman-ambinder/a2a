# SDK Design Principles Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reshape the A2A .NET SDK so it meets the four principles (easy, performant, extendable, modular) above the P0 safety floor, with CI gates that keep it there.

**Architecture:** Seven stages in the spec's order: enforcement gates first (public API snapshot, docs as errors, benchmarks, AOT publish), then package moves, then the store contract, then splitting `A2ARequestHandler` behind `IA2ARequestHandler`, then the operation filter pipeline with telemetry and the operation table, then the single-encode event path and bounded channels, then the one-call surface. Wire behaviour never changes; `tests/A2A.Tests/Parity` is the regression net for every stage.

**Tech Stack:** .NET 8 and 10, System.Text.Json source generation, ASP.NET Core, gRPC, xUnit, BenchmarkDotNet, Microsoft.CodeAnalysis.PublicApiAnalyzers.

**Spec:** `docs/superpowers/specs/2026-11-07-sdk-design-principles-design.md` (referred to as "spec" below; `D1`…`D29` are its decisions).

## Global Constraints

- Targets `net8.0;net10.0`; `TreatWarningsAsErrors`, `IsAotCompatible`, `Nullable` stay on for every `src/` project.
- Serialization only through `A2AJsonContext` (source generated). No reflection serializer path, no `JsonSerializerOptions` without a resolver.
- Runtime dependencies: `A2A.Protocol` none; `A2A.Client`, `A2A.Server` only `Microsoft.Extensions.*.Abstractions` and `Microsoft.Extensions.Options`; gRPC packages may add Google/Grpc packages.
- Every public type and member gets an XML `<summary>` once Task 2 removes the `CS1591` suppression.
- Wire behaviour fixed: every stage ends with `dotnet build A2A.slnx -c Release` clean and `dotnet test tests/A2A.Tests -f net10.0` green with the parity suite unchanged (test bodies in `tests/A2A.Tests/Parity` may not be edited except to rename a type they reference).
- New rules from `D16`: no `A2AJson.Clone` outside tests; no `SerializeToElement` on a response path; no unbounded channel; no `ToList` inside a per-event loop.
- Refactor, not rewrite. Public API may break (nothing is published). Commit after every task; commit messages in Conventional Commits form, body only when the "why" is not obvious.
- Verification per task is `dotnet build A2A.slnx -c Release` (both TFMs) then `dotnet test tests/A2A.Tests -f net10.0` unless the task says otherwise. Run net8 tests in CI only (no ASP.NET Core 8 runtime locally).

## Review Focus

1. **A store that returns the same instance twice** (Task 10): a second `GetAsync` after an `UpdateAsync` must observe the new state, and the handler must never mutate what `GetAsync` returned. Test: `TaskStoreOwnershipTests.Handler_never_mutates_returned_record` in Task 10.
2. **A slow SSE subscriber on a fast agent** (Task 24): with bounded `DropWrite` bus channels the subscriber must still receive every event in order, filled from the store. Test: `BoundedChannelTests.Slow_subscriber_receives_all_events_in_order` in Task 24.
3. **An extension operation with no explicit policy** (Task 19): must answer `MethodNotFound` or a denial over JSON-RPC and REST, never run. Test: `OperationTableTests.Extension_without_policy_is_denied` in Task 19.
4. **An event of exactly `MaxEventBytes`** (Task 21): the once-encoded buffer path must accept it; one byte more must fail the task as today. Test: `EncodedEventTests.Event_at_limit_is_accepted_one_over_fails` in Task 21.
5. **A card with empty `SupportedInterfaces` behind a reverse proxy** (Task 28): the completed card must use `A2AHttpOptions.PublicBaseUrl`, not the Kestrel address. Test: `CardCompletionTests.PublicBaseUrl_wins_over_server_address` in Task 28.

---

## Stage 1: measuring stick

### Task 1: Public API snapshot

**Files:**
- Modify: `Directory.Build.props`
- Modify: `Directory.Packages.props`
- Create: `src/<Project>/PublicAPI.Shipped.txt` and `src/<Project>/PublicAPI.Unshipped.txt` for all seven `src/` projects

**Interfaces:**
- Produces: a build that fails with `RS0016` when a public symbol is added without a matching line in `PublicAPI.Unshipped.txt`.

- [ ] **Step 1: Add the analyzer package** to `Directory.Packages.props` (`Microsoft.CodeAnalysis.PublicApiAnalyzers`, version `3.3.4`) and reference it in the `src/` property group of `Directory.Build.props` with `PrivateAssets="all"`. Add `<AdditionalFiles Include="PublicAPI.Shipped.txt" /> <AdditionalFiles Include="PublicAPI.Unshipped.txt" />` to the same group.
- [ ] **Step 2: Build and expect failure**. Run `dotnet build A2A.slnx -c Release 2>&1 | grep -c RS0016`. Expected: a number greater than 0.
- [ ] **Step 3: Generate the Unshipped files**. Create each `PublicAPI.Shipped.txt` containing only `#nullable enable`. Run `dotnet format analyzers A2A.slnx --diagnostics RS0016 --severity info` to populate `PublicAPI.Unshipped.txt`; if the formatter cannot fix all entries, create them from the analyzer's reported text.
- [ ] **Step 4: Build clean**. Run `dotnet build A2A.slnx -c Release`. Expected: 0 warnings, 0 errors.
- [ ] **Step 5: Prove the gate**. Add a throwaway `public static class Probe {}` to `src/A2A.Protocol/A2AProtocol.cs`, build, expect `error RS0016`; remove it.
- [ ] **Step 6: Commit** `chore: snapshot the public API with PublicApiAnalyzers`.

### Task 2: XML docs as errors

**Files:**
- Modify: `Directory.Build.props:22-24` (remove `CS1591` from `NoWarn`)
- Modify: every `src/**/*.cs` with an undocumented public member (about 170 members; the largest files are `src/A2A.Protocol/Models/AgentCard.cs`, `Models/Requests.cs`, `Models/Core.cs`, `src/A2A.Server/AgentExecutor.cs`, `src/A2A.Server/A2AServerOptions.cs`, DI extension classes)

- [ ] **Step 1: Remove the suppression** and build. Run `dotnet build A2A.slnx -c Release 2>&1 | grep -c CS1591`. Record the count.
- [ ] **Step 2: Document `A2A.Protocol`** public members. Every summary states what the member is and, for options, its default. Request DTOs cite the spec section (for example `GetTaskRequest`: "spec §7.3"). Build; `CS1591` count for `A2A.Protocol` is 0.
- [ ] **Step 3: Document `A2A.Server`, `A2A.Server.AspNetCore`** likewise. Build.
- [ ] **Step 4: Document `A2A.Client`, `A2A.Grpc`, `A2A.Client.Grpc`, `A2A.Server.Grpc`**. Build: 0 warnings, 0 errors.
- [ ] **Step 5: Commit** `docs: document every public member; CS1591 is an error`.

### Task 3: Benchmarks with a committed baseline

**Files:**
- Create: `tests/A2A.Benchmarks/A2A.Benchmarks.csproj` (net10.0 only, `BenchmarkDotNet` `0.14.0` added to `Directory.Packages.props`, references `A2A.Server`, `A2A.Server.AspNetCore`, `A2A.Client`, `Microsoft.AspNetCore.TestHost`)
- Create: `tests/A2A.Benchmarks/Program.cs`, `SendMessageBenchmark.cs`, `StreamingTaskBenchmark.cs`, `JsonRpcEnvelopeBenchmark.cs`, `CardVerifyBenchmark.cs`
- Create: `tests/A2A.Benchmarks/baseline.json`, `tests/A2A.Benchmarks/compare.py`
- Modify: `.github/workflows/ci.yml` (new job `benchmarks`)
- Modify: `A2A.slnx` (add the project)

**Interfaces:**
- Produces: `baseline.json` with `{ "<BenchmarkName>": { "meanNs": <double>, "allocatedBytes": <long> } }` per benchmark; `compare.py <results-dir> <baseline.json> --threshold 0.10` exits 1 when any benchmark's mean or allocation exceeds baseline by more than 10 percent.

- [ ] **Step 1: Create the project** with `[MemoryDiagnoser]` on every class and a `ShortRunJob` config selected by `--job short`.
- [ ] **Step 2: Write the four benchmarks**: `SendMessageBenchmark.RoundTrip` (in-process `TestServer`, JSON-RPC `SendMessage` with a 3-word reply); `StreamingTaskBenchmark.ThousandEvents` (executor emits 1,000 artifact-append events, client consumes the SSE stream); `JsonRpcEnvelopeBenchmark.Encode` (serialize one `JsonRpcResponse.Success` carrying a `StatusUpdateEvent`); `CardVerifyBenchmark.Verify` (`AgentCardVerifier.VerifyAsync` on a signed ES256 card with a pinned key). Each benchmark's `[GlobalSetup]` builds its host once.
- [ ] **Step 3: Run once** with `dotnet run -c Release --project tests/A2A.Benchmarks -- --job short --exporters json`. Expected: four results, a `BenchmarkDotNet.Artifacts/results/*.json` directory.
- [ ] **Step 4: Write `compare.py`** (Python 3 standard library only) and generate `baseline.json` from the step 3 results. Run `python3 tests/A2A.Benchmarks/compare.py BenchmarkDotNet.Artifacts/results tests/A2A.Benchmarks/baseline.json --threshold 0.10`. Expected: exit 0.
- [ ] **Step 5: Add the CI job** `benchmarks` on `ubuntu-latest`, needs `build-test`, runs steps 3 and 4. Threshold is `0.10` (spec `D15`).
- [ ] **Step 6: Commit** `test: BenchmarkDotNet suite with committed baseline and CI comparison`.

### Task 4: AOT publish job and analyzer level

**Files:**
- Modify: `Directory.Build.props` (`<AnalysisLevel>latest-recommended</AnalysisLevel>` in the `src/` group)
- Modify: `.github/workflows/ci.yml` (new job `aot-publish`)

- [ ] **Step 1: Set `AnalysisLevel`**, build, fix every new warning in `src/` (expected classes: `CA1848` logging performance, `CA2007` is not raised because `ConfigureAwait` is already used; suppress `CA1848` project-wide in the props with a comment until Task 18 introduces `LoggerMessage`).
- [ ] **Step 2: Add the CI job** running `dotnet publish samples/EchoAgent -c Release -f net10.0 -r linux-x64 -p:PublishAot=true` and the same for `samples/EchoClient`, each followed by `test ! -s warnings.txt` where the publish output is grepped for `IL2` and `IL3` warning codes.
- [ ] **Step 3: Run the publish locally** for `EchoClient` on `osx-arm64`. Expected: `0 Warning(s)`.
- [ ] **Step 4: Commit** `ci: AOT publish gate for the samples; latest-recommended analyzers`.

## Stage 2: modular moves

### Task 5: `A2A.Security.Http` package and `IJwksFetcher`

**Files:**
- Create: `src/A2A.Security.Http/A2A.Security.Http.csproj` (references `A2A.Protocol`; no package references)
- Move: `src/A2A.Protocol/Net/PrivateAddressGuard.cs` → `src/A2A.Security.Http/PrivateAddressGuard.cs` (namespace `A2A.Security.Http`, class becomes `public static`)
- Create: `src/A2A.Protocol/Signing/IJwksFetcher.cs`
- Create: `src/A2A.Security.Http/GuardedJwksFetcher.cs`
- Modify: `src/A2A.Protocol/Signing/AgentCardVerifier.cs` (constructor takes `IJwksFetcher?`; static `GuardedJwksClient` removed)
- Modify: `src/A2A.Client/A2ACardResolver.cs:106`, `src/A2A.Client/A2A.Client.csproj`, `src/A2A.Server/Push/PushNotifications.cs`, `src/A2A.Server/A2A.Server.csproj` (reference the new package)
- Modify: `src/A2A.Protocol/A2A.Protocol.csproj` (drop `InternalsVisibleTo` for `A2A.Client` and `A2A.Server`; make `TextSanitizer` public in namespace `A2A.Text`)
- Create: `tests/A2A.Tests/PackageRulesTests.cs`
- Test: `tests/A2A.Tests/CardTrustTests.cs` (existing verifier tests keep passing)

**Interfaces:**
- Produces: `public interface IJwksFetcher { ValueTask<JwkSet> FetchAsync(Uri jku, CancellationToken ct); }` in `A2A.Signing`. `public AgentCardVerifier(CardTrustPolicy policy, IJwksFetcher? jwksFetcher = null)`; with `null`, any `jku` signature yields `Unverified` with reason `"no JWKS fetcher configured"`. `public sealed class GuardedJwksFetcher(HttpMessageInvoker? invoker = null) : IJwksFetcher` in `A2A.Security.Http`, keeping the 5-fetch cap and 60 s negative cache. `PushUrlValidator` and `A2ACardResolver` reference `A2A.Security.Http.PrivateAddressGuard`.

- [ ] **Step 1: Write `PackageRulesTests`** that reads every `src/*/*.csproj`: `Protocol_has_no_package_references`, `Client_and_Server_reference_only_extensions_abstractions` (allowed prefixes `Microsoft.Extensions.` ending in `.Abstractions`, plus `Microsoft.Extensions.Options`), `No_InternalsVisibleTo_targets_a_src_assembly`, `Security_Http_has_no_package_references`. Locate the repo root by walking up from `AppContext.BaseDirectory` to `A2A.slnx`.
- [ ] **Step 2: Run** `dotnet test tests/A2A.Tests -f net10.0 --filter PackageRulesTests`. Expected: FAIL on `No_InternalsVisibleTo_targets_a_src_assembly` and `Client_and_Server_reference_only_extensions_abstractions` (`Microsoft.Extensions.Http` is present).
- [ ] **Step 3: Create the package, move the guard, add `IJwksFetcher` and `GuardedJwksFetcher`, rewire the verifier and resolver.** `A2ACardResolver` constructs `new GuardedJwksFetcher(_options.JwksHttpClient)` when `Trust.AllowedJkuHosts` is non-empty.
- [ ] **Step 4: Remove `InternalsVisibleTo` for `A2A.Client` and `A2A.Server` from `A2A.Protocol.csproj`**, make `TextSanitizer` public, build. The `A2A.Server` and `A2A.Client` tests still reference `Microsoft.Extensions.Http`, so only the IVT rule passes at this point; mark the package-reference test `[Fact(Skip = "Task 6, Task 8")]` and remove the skip there.
- [ ] **Step 5: Run** full build and tests. Expected: green; `PackageRulesTests` green except the skipped one.
- [ ] **Step 6: Commit** `refactor: A2A.Security.Http holds network guards; verifier takes IJwksFetcher`.

### Task 6: `A2A.Client.DependencyInjection` package

**Files:**
- Create: `src/A2A.Client.DependencyInjection/A2A.Client.DependencyInjection.csproj` (references `A2A.Client`; packages `Microsoft.Extensions.Http`, `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Options`)
- Move: `src/A2A.Client/DependencyInjection.cs` → `src/A2A.Client.DependencyInjection/A2AClientServiceCollectionExtensions.cs` (namespace stays `A2A.Client`)
- Modify: `src/A2A.Client/A2A.Client.csproj` (drop `Microsoft.Extensions.Http`, `Microsoft.Extensions.DependencyInjection.Abstractions`; keep `Microsoft.Extensions.Logging.Abstractions`)
- Modify: `tests/A2A.Tests/A2A.Tests.csproj`, `samples/EchoClient/EchoClient.csproj`, `interop/dotnet/InteropClient/InteropClient.csproj`, `src/A2A.Client.Grpc/A2A.Client.Grpc.csproj` if it uses `AddA2AClient` (reference the new package)
- Modify: `README.md` package table

- [ ] **Step 1: Un-skip the client half** of `Client_and_Server_reference_only_extensions_abstractions` by splitting it into `Client_references_only_logging_abstractions` (allowed: exactly `Microsoft.Extensions.Logging.Abstractions`) and `Server_references_only_extensions_abstractions` (still skipped until Task 8). Run: FAIL for the client test.
- [ ] **Step 2: Create the package and move the file.** `BearerTokenHandler` moves with it. `A2ANamedClientOptions` and `IA2AClientFactory` move with it.
- [ ] **Step 3: Build and test.** Expected: green, client package test passes.
- [ ] **Step 4: Commit** `refactor: client DI registration moves to A2A.Client.DependencyInjection`.

### Task 7: Namespaces follow assemblies

**Files:**
- Move: `RestErrorBody` (and `RestError`) from `src/A2A.Protocol/Errors/A2AException.cs:259` → `src/A2A.Server.AspNetCore/RestErrorBody.cs` (namespace `A2A.Server.AspNetCore`). The client's REST transport reads errors through `A2AException`'s existing parsing of the `google.rpc.Status` shape; check `src/A2A.Client/Transports/HttpJsonTransport.cs:140` and keep a private copy of the DTO there if it deserializes `RestErrorBody`.
- Modify: `src/A2A.Server/Authorization/A2AAuthorizationHandler.cs`, `A2AOperation.cs` (namespace `A2A.Server.Authorization`); update usings in `A2ARequestHandler.cs`, `ServiceCollectionExtensions.cs`, tests.
- Modify: `PublicAPI.Unshipped.txt` of the affected projects.

- [ ] **Step 1: Write `NamespaceRulesTests.Every_public_type_lives_in_a_namespace_owned_by_its_assembly`** in `tests/A2A.Tests/PackageRulesTests.cs`: for each shipped assembly, each public type's namespace must start with the assembly's root namespace (`A2A` for `A2A.Protocol`; `A2A.Client` for `A2A.Client`; etc.) and `A2A.Protocol` types must not be in a namespace starting with `A2A.Server` or `A2A.Client`. Run: FAIL on `RestErrorBody`.
- [ ] **Step 2: Move the types**, fix usings, update `PublicAPI.Unshipped.txt`.
- [ ] **Step 3: Build and test.** Expected: green.
- [ ] **Step 4: Commit** `refactor: REST error body and authorization types move to their owning assemblies`.

### Task 8: Server core without a container

**Files:**
- Create: `src/A2A.Server/IAgentExecutorFactory.cs`
- Create: `src/A2A.Server/ScopedAgentExecutorFactory.cs`
- Modify: `src/A2A.Server/Push/PushNotifications.cs:87-96,181` (`HttpPushNotificationSender` takes `HttpMessageInvoker`)
- Modify: `src/A2A.Server/ServiceCollectionExtensions.cs` (register the factory; supply the invoker from `IHttpClientFactory`)
- Modify: `src/A2A.Server/A2ARequestHandler.cs:24,41` (`IAgentExecutorFactory` instead of `IServiceScopeFactory`; `A2AServerOptions` instead of `IOptions<A2AServerOptions>`; DI registers `TryAddSingleton(sp => sp.GetRequiredService<IOptions<A2AServerOptions>>().Value)` so every core type can take the plain options)
- Modify: `src/A2A.Server/A2A.Server.csproj` (drop `Microsoft.Extensions.Http`, keep `DependencyInjection.Abstractions`, `Logging.Abstractions`, `Options`)
- Test: `tests/A2A.Tests/PackageRulesTests.cs` (un-skip the server test)

**Interfaces:**
- Produces: `public interface IAgentExecutorFactory { ValueTask<AgentExecutorLease> RentAsync(CancellationToken ct); }` with `public readonly struct AgentExecutorLease(IAgentExecutor executor, IAsyncDisposable? scope) : IAsyncDisposable`. `public sealed class ScopedAgentExecutorFactory(IServiceScopeFactory scopes) : IAgentExecutorFactory`. `public HttpPushNotificationSender(HttpMessageInvoker http, PushConfigStore store, IPushSecretProtector protector, A2AServerOptions options, ILogger<HttpPushNotificationSender> logger)`.

- [ ] **Step 1: Un-skip `Server_references_only_extensions_abstractions`.** Run: FAIL (`Microsoft.Extensions.Http`).
- [ ] **Step 2: Add the factory, change the sender and handler constructors**, register `ScopedAgentExecutorFactory` with `TryAddSingleton`, and build the invoker in DI as `new HttpMessageInvoker(PushUrlValidator.CreateHandler(options.PushNotifications), disposeHandler: true)`. `A2A.Server` no longer calls `AddHttpClient`.
- [ ] **Step 3: Build and test.** Expected: green. Push delivery tests in `PushDeliveryTests.cs` still pass (they replace the handler through `RecordingHandler`; adapt `TestHost.cs:141` to construct the sender's invoker from the recording handler).
- [ ] **Step 4: Commit** `refactor: server core takes plain dependencies; no IHttpClientFactory or IServiceScopeFactory in the core`.

## Stage 3: store contract

### Task 9: Structural copy on the models

**Files:**
- Create: `src/A2A.Protocol/Models/ModelCopies.cs`
- Test: `tests/A2A.Tests/ModelCopyTests.cs`

**Interfaces:**
- Produces: `public static class ModelCopies` with `AgentTask CopyForUpdate(this AgentTask task)` (new `AgentTask`, new `History` and `Artifacts` lists holding the same `Message`/`Artifact` instances, new `Status` holding the same `Message`, new `Metadata` dictionary with the same `JsonElement` values), `Artifact CopyWithParts(this Artifact artifact, IEnumerable<Part> extraParts)` (new `Artifact`, new `Parts` list = old parts followed by `extraParts`, merged `Metadata`), and `AgentTask DeepCopy(this AgentTask task)` (every reachable `Message`, `Artifact`, `Part`, list and dictionary is new; `JsonElement` values are cloned with `JsonElement.Clone()`).

- [ ] **Step 1: Write tests**: `CopyForUpdate_shares_messages_but_not_lists` (reference-equal messages, not-reference-equal lists), `CopyWithParts_leaves_original_untouched`, `DeepCopy_shares_nothing` (walk both graphs, assert no reference equality except strings and enums), `DeepCopy_equals_json_clone` (serialize both, byte-equal).
- [ ] **Step 2: Run** `--filter ModelCopyTests`. Expected: FAIL, type missing.
- [ ] **Step 3: Implement** `ModelCopies`. `Part` subtypes are records, so `DeepCopy` uses `with {}` plus list copies for `DataPart`'s dictionary.
- [ ] **Step 4: Run tests.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(protocol): structural copies for AgentTask and Artifact`.

### Task 10: Stores own their records; no JSON clone

**Files:**
- Modify: `src/A2A.Server/Storage/TaskStore.cs` (XML contract: "the store owns the instance it returns; callers never mutate it")
- Modify: `src/A2A.Server/Storage/InMemoryTaskStore.cs:73` (remove `Copy`), `InMemoryEventStore.cs:15,42`, `PushConfigStore.cs` (`InMemoryPushConfigStore.Copy` removed)
- Modify: `src/A2A.Server/A2ARequestHandler.cs` (all 14 `A2AJson.Clone` sites; `Apply` becomes `static AgentTask? ApplyToCopy(AgentTask current, StreamResponse evt)` returning a `CopyForUpdate` with the event applied or `null` when rejected; `Shape` builds a new `AgentTask` sharing untouched parts; `AgentRequestContext.Task` receives `record.Task.DeepCopy()`; `IAgentExecutor.CancelAsync` receives a `DeepCopy`)
- Modify: `src/A2A.Server/AgentExecutor.cs` (`TaskUpdater` XML: emitted `Message`/`Artifact` instances must not be mutated afterwards)
- Modify: `src/A2A.Protocol/Json/A2AJson.cs:84` (`Clone<T>` becomes `internal`, moved to `tests/A2A.Tests/TestJson.cs` as a test helper if tests need it)
- Create: `tests/A2A.Tests/Storage/OwnershipCheckingTaskStore.cs` (test double)
- Test: `tests/A2A.Tests/TaskStoreOwnershipTests.cs`, existing `TaskStoreTests.cs`, `TaskLifecycleTests.cs`

**Interfaces:**
- Produces: `TaskStore` contract text; handler free of `A2AJson.Clone`; `A2AJson.Clone` no longer public.

- [ ] **Step 1: Write `OwnershipCheckingTaskStore : TaskStore`** wrapping `InMemoryTaskStore`: on every return it stores `SHA256(A2AJson.SerializeToUtf8Bytes(record.Task))` keyed by `(key, version)`; on every call it re-hashes previously returned instances and throws `InvalidOperationException("record mutated after return")` on a mismatch.
- [ ] **Step 2: Write tests** in `TaskStoreOwnershipTests`: `Handler_never_mutates_returned_record` (full lifecycle through `TestAgentHost` with the checking store registered, 50 events, follow-up message, cancel), `Second_get_after_update_sees_new_state`, `Executor_view_is_detached` (executor mutates `context.Task.Status.State`; stored task is unchanged). Also `A2AJson_Clone_is_not_public` via reflection.
- [ ] **Step 3: Run** `--filter TaskStoreOwnershipTests`. Expected: FAIL (handler mutates; `Clone` is public).
- [ ] **Step 4: Change the stores and the handler** as listed. `Apply` ordering: `ApplyToCopy` → `UpdateAsync(copy, expectedVersion)` → `AppendAsync` → `Publish`, unchanged from today except the copy is structural. The event store stores the instance it was given.
- [ ] **Step 5: Run all tests.** Expected: PASS. Check `grep -rn "A2AJson.Clone" src/` prints nothing.
- [ ] **Step 6: Run benchmarks** (`--job short`) and `compare.py`. Expected: exit 0 and lower allocations on `StreamingTaskBenchmark.ThousandEvents`; update `baseline.json` only downward.
- [ ] **Step 7: Commit** `perf(server): stores own their records; structural copies replace JSON cloning`.

## Stage 4: split the handler

Each extraction below is a pure refactor: move code, keep behaviour, keep the parity suite untouched. The handler keeps its public operation methods until Task 16.

### Task 11: `CallValidator`

**Files:**
- Create: `src/A2A.Server/Pipeline/CallValidator.cs`
- Modify: `src/A2A.Server/A2ARequestHandler.cs:74-138` (`ValidateCall`, `MajorMinor`, extension checks delegate to the validator)
- Modify: `src/A2A.Server/ServiceCollectionExtensions.cs` (`TryAddSingleton<CallValidator>`)
- Test: `tests/A2A.Tests/Pipeline/CallValidatorTests.cs`

**Interfaces:**
- Produces: `public sealed class CallValidator(A2AServerOptions options) { public void Validate(ServerCallContext call); }` throwing the same `A2AException`s the handler throws today (`UnsupportedVersion`, `ExtensionNotSupported`), and populating `call.ActivatedExtensions`.

- [ ] **Step 1: Write tests** `Missing_version_is_rejected_unless_TreatMissingVersionAsCurrent`, `Required_unknown_extension_is_rejected`, `Declared_extension_is_activated` (move assertions from the existing handler tests that cover these; keep those tests too).
- [ ] **Step 2: Run** `--filter CallValidatorTests`. Expected: FAIL.
- [ ] **Step 3: Extract** the class; `A2ARequestHandler.ValidateCall` calls `_validator.Validate(call)`.
- [ ] **Step 4: Run all tests.** PASS.
- [ ] **Step 5: Commit** `refactor(server): CallValidator owns version and extension checks`.

### Task 12: `PushConfigService`

**Files:**
- Create: `src/A2A.Server/Push/PushConfigService.cs`
- Modify: `src/A2A.Server/A2ARequestHandler.cs:1009-1094,1416-1440` (push-config operations delegate)
- Modify: `src/A2A.Server/ServiceCollectionExtensions.cs`
- Test: `tests/A2A.Tests/PushConfigTests.cs` (existing, unchanged)

**Interfaces:**
- Produces: `public sealed class PushConfigService(PushConfigStore store, IPushSecretProtector protector, A2AServerOptions options)` with `ValueTask<TaskPushNotificationConfig> CreateAsync(TaskKey key, TaskPushNotificationConfig config, CancellationToken ct)`, `ValueTask<TaskPushNotificationConfig> GetAsync(TaskKey key, string configId, CancellationToken ct)`, `ValueTask<ListTaskPushNotificationConfigsResponse> ListAsync(TaskKey key, int pageSize, string? pageToken, CancellationToken ct)`, `ValueTask DeleteAsync(TaskKey key, string configId, CancellationToken ct)`, `ValueTask<TaskPushNotificationConfig?> ValidateAndProtectAsync(TaskKey key, TaskPushNotificationConfig config, CancellationToken ct)` (used by SendMessage's inline push config). Redaction, URL validation, capacity check and secret protection live here.

- [ ] **Step 1: Extract** the class; handler methods become one-line delegations after authorization.
- [ ] **Step 2: Run all tests.** PASS (`PushConfigTests` exercise every path).
- [ ] **Step 3: Commit** `refactor(server): PushConfigService owns push-config CRUD, redaction and protection`.

### Task 13: `IdempotencyCoordinator`

**Files:**
- Create: `src/A2A.Server/Idempotency/IdempotencyCoordinator.cs`
- Modify: `src/A2A.Server/A2ARequestHandler.cs:432-575` (`BeginIdempotentAsync`, `ReplayAsync`, `WaitUntilSettledAsync`, `HasStatusEventAfterAsync`, `AbandonAsync`, `RecordAsync` move)
- Modify: `src/A2A.Server/Idempotency/IdempotencyStore.cs` (`IdempotencyClaim` becomes `public sealed class`)
- Test: `tests/A2A.Tests/IdempotencyTests.cs` (existing)

**Interfaces:**
- Produces: `public sealed class IdempotencyCoordinator(IdempotencyStore store, TaskStore tasks, EventStore events, A2AServerOptions options)` with `ValueTask<(IdempotencyClaim Claim, IdempotencyOutcome? Replay)> BeginAsync(SendMessageRequest request, ServerCallContext call, CancellationToken ct)`, `ValueTask<SendMessageResponse> ReplayAsync(IdempotencyOutcome outcome, SendMessageRequest request, ServerCallContext call, Func<TaskKey, long, CancellationToken, ValueTask<AgentTask>> waitUntilSettled, CancellationToken ct)`, `ValueTask RecordAsync(IdempotencyClaim claim, IdempotencyOutcome outcome)`, `ValueTask AbandonAsync(IdempotencyClaim claim)`. `public const int MaxIdempotencyKeyLength = 256` moves here.

- [ ] **Step 1: Extract.** `WaitUntilSettledAsync` stays in the handler for now (it needs the bus and running registry; Task 14 moves it to the engine) and is passed as the delegate.
- [ ] **Step 2: Run all tests.** PASS.
- [ ] **Step 3: Commit** `refactor(server): IdempotencyCoordinator owns claim, replay and settle logic`.

### Task 14: `TaskExecutionEngine`

**Files:**
- Create: `src/A2A.Server/Execution/TaskExecutionEngine.cs`, `src/A2A.Server/Execution/Execution.cs` (the private `Execution` class becomes `internal sealed`), `src/A2A.Server/Execution/TaskLocks.cs` (the 64 striped semaphores)
- Modify: `src/A2A.Server/A2ARequestHandler.cs:1096-1410,1442-1512` (`StartExecution`, `StartRaw`, `ConsumeAsync`, `TryFinishAsync`, `TryFailStoredTaskAsync`, `ApplyAndStoreAsync`, `Publish`, `ApplyToCopy`, `FailureEvent`, `WithTask`, `RunningCompletions`, `WaitUntilSettledAsync`, `HasStatusEventAfterAsync`, `WhenIdleAsync`, `Dispose`, `_running`, `_locks`, `_shutdown`)
- Modify: `src/A2A.Server/ServiceCollectionExtensions.cs`
- Test: `tests/A2A.Tests/Execution/TaskExecutionEngineTests.cs`, existing `HandlerSequenceTests.cs`, `CrossExecutionTests.cs`, `TaskLifecycleTests.cs`

**Interfaces:**
- Produces: `public sealed class TaskExecutionEngine : IAsyncDisposable` constructed with `(TaskStore tasks, EventStore events, IAgentExecutorFactory executors, TaskEventBus bus, IPushNotificationSender push, A2AServerOptions options, ILogger<TaskExecutionEngine> logger)`; methods `Execution Start(AgentRequestContext context, TaskPushNotificationConfig? push, IdempotencyClaim claim)`, `Execution StartCancel(TaskKey key, AgentTask task, ServerCallContext call, Dictionary<string, JsonElement>? metadata)`, `ValueTask<AgentTask> WaitUntilSettledAsync(TaskKey key, long afterSequence, CancellationToken ct)`, `IReadOnlyList<Task> RunningCompletions(TaskKey key)`, `Task WhenIdleAsync()`, `IDisposable Lock(TaskKey key)` (async acquire via `ValueTask<IDisposable> LockAsync(TaskKey key, CancellationToken ct)`). `Execution` exposes `TaskId`, `Completion`, `Outgoing` reader, `Cancel()`, `Detach()`.

- [ ] **Step 1: Write `TaskExecutionEngineTests`** constructing the engine with `new InMemoryTaskStore()`, `new InMemoryEventStore(...)`, a delegate `IAgentExecutorFactory`, `new TaskEventBus()`, a no-op `IPushNotificationSender`, **no `ServiceCollection`**: `Start_runs_executor_and_stores_terminal_state`, `Oversized_event_fails_task_and_stops_executor`, `Store_failure_fails_task` (this is also the spec's no-DI server proof, `D27`).
- [ ] **Step 2: Run** `--filter TaskExecutionEngineTests`. FAIL.
- [ ] **Step 3: Extract** the engine. The handler keeps `SendMessageAsync` orchestration but calls `_engine.Start(...)`, `_engine.WaitUntilSettledAsync(...)`, `_engine.LockAsync(...)`.
- [ ] **Step 4: Run all tests.** PASS.
- [ ] **Step 5: Commit** `refactor(server): TaskExecutionEngine owns executions, locks, apply-and-store, publish`.

### Task 15: `TaskStreamer`

**Files:**
- Create: `src/A2A.Server/Streaming/TaskStreamer.cs`
- Modify: `src/A2A.Server/A2ARequestHandler.cs:225-430,764-1003` (`SendStreamingCoreAsync`, `InOrderAsync`, `SubscribeCoreAsync`, `ReadStartAsync`, `ReadSnapshotAsync`, `CanReplay`, `ReadContiguousAsync`, `IsFinal`, `LimitStreamAsync`, `StreamPosition`, `StreamStart`, `_admission`)
- Modify: `src/A2A.Server/Streaming/StreamAdmission.cs` (becomes `public sealed`)
- Test: existing `StreamingResumeTests.cs`, `HandlerSequenceTests.cs`, `EventStoreTests.cs`

**Interfaces:**
- Produces: `public sealed class TaskStreamer(EventStore events, TaskStore tasks, TaskEventBus bus, TaskExecutionEngine engine, A2AAuthorizationHandler authorization, A2AServerOptions options, TimeProvider time)` with `IAsyncEnumerable<SequencedEvent> StreamExecutionAsync(Execution exec, TaskKey key, ServerCallContext call, string? lastEventId, Action? onCaughtUp, CancellationToken ct)` and `IAsyncEnumerable<SequencedEvent> SubscribeAsync(TaskKey key, ServerCallContext call, long? resumeAfter, Action? onCaughtUp, CancellationToken ct)`. Both already include the limit and re-auth wrapper.

- [ ] **Step 1: Extract.** The handler's streaming operations become: authorize, prepare, `_streamer.StreamExecutionAsync(...)`.
- [ ] **Step 2: Run all tests.** PASS.
- [ ] **Step 3: Confirm size**: `wc -l src/A2A.Server/A2ARequestHandler.cs` under 500.
- [ ] **Step 4: Commit** `refactor(server): TaskStreamer owns the send-stream and subscribe state machines`.

### Task 16: `IA2ARequestHandler`; bindings depend on the interface

**Files:**
- Create: `src/A2A.Server/IA2ARequestHandler.cs`
- Modify: `src/A2A.Server/A2ARequestHandler.cs` (implements the interface; the `internal` `onCaughtUp` overloads become the public interface members; `OpenStreams` stays internal for tests through `InternalsVisibleTo("A2A.Tests")`)
- Modify: `src/A2A.Server/ServiceCollectionExtensions.cs` (`TryAddSingleton<A2ARequestHandler>(); TryAddSingleton<IA2ARequestHandler>(sp => sp.GetRequiredService<A2ARequestHandler>())`)
- Modify: `src/A2A.Server.AspNetCore/JsonRpcEndpoint.cs:13`, `RestEndpoint.cs:15`, `src/A2A.Server.Grpc/A2AGrpcService.cs:19-22` (take `IA2ARequestHandler`)
- Modify: `src/A2A.Server/A2A.Server.csproj` (remove `InternalsVisibleTo` for `A2A.Server.AspNetCore`)
- Test: `tests/A2A.Tests/FakeHandlerBindingTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public interface IA2ARequestHandler
  {
      AgentCard AgentCard { get; }
      void ValidateCall(ServerCallContext call);
      Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, ServerCallContext call, CancellationToken ct = default);
      IAsyncEnumerable<SequencedEvent> SendStreamingMessageAsync(SendMessageRequest request, ServerCallContext call, string? lastEventId = null, Action? onCaughtUp = null, CancellationToken ct = default);
      Task<AgentTask> GetTaskAsync(GetTaskRequest request, ServerCallContext call, CancellationToken ct = default);
      Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, ServerCallContext call, CancellationToken ct = default);
      Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, ServerCallContext call, CancellationToken ct = default);
      IAsyncEnumerable<SequencedEvent> SubscribeToTaskAsync(SubscribeToTaskRequest request, ServerCallContext call, long? resumeAfter = null, Action? onCaughtUp = null, CancellationToken ct = default);
      Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, ServerCallContext call, CancellationToken ct = default);
      Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, ServerCallContext call, CancellationToken ct = default);
      Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, ServerCallContext call, CancellationToken ct = default);
      Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, ServerCallContext call, CancellationToken ct = default);
      Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, ServerCallContext call, CancellationToken ct = default);
  }
  ```

- [ ] **Step 1: Write `FakeHandlerBindingTests`**: a `FakeHandler : IA2ARequestHandler` recording calls and returning canned results, registered with `services.Replace(ServiceDescriptor.Singleton<IA2ARequestHandler>(fake))` in a `TestServer`; tests `JsonRpc_GetTask_reaches_fake`, `Rest_GetTask_reaches_fake`, `JsonRpc_stream_reaches_fake_with_last_event_id` (asserts `lastEventId == "t1:7"` from header `Last-Event-ID: t1:7`).
- [ ] **Step 2: Run.** FAIL (interface missing).
- [ ] **Step 3: Add the interface, change the bindings, remove the IVT**, update `PublicAPI.Unshipped.txt`. `tests/A2A.Tests/TestHost.cs:140` keeps returning the concrete `A2ARequestHandler`.
- [ ] **Step 4: Run all tests including `PackageRulesTests.No_InternalsVisibleTo_targets_a_src_assembly`.** PASS.
- [ ] **Step 5: Commit** `refactor(server): IA2ARequestHandler; bindings depend on the interface; no src-to-src InternalsVisibleTo`.

## Stage 5: pipeline, telemetry, operation table, transport parity

### Task 17: Operation filter pipeline and `A2AOperationDescriptor`

**Files:**
- Create: `src/A2A.Server/Pipeline/IA2AOperationFilter.cs`, `A2AOperationContext.cs`, `A2AOperationDescriptor.cs`, `A2AFilterStage.cs`, `OperationPipeline.cs`, `AuthorizationFilter.cs`
- Modify: `src/A2A.Server/Authorization/A2AAuthorizationHandler.cs` (`AuthorizeAsync(ServerCallContext call, A2AOperationDescriptor op, object request, CancellationToken ct)`), `A2AOperation.cs` (add `Extension`)
- Modify: `src/A2A.Server/A2ARequestHandler.cs` (every operation: `ValidateCall` and `BindTenant` first, then `_pipeline.RunAsync(descriptor, call, request, innermost, ct)`)
- Modify: `src/A2A.Server/ServiceCollectionExtensions.cs` (`A2AServerBuilder.AddOperationFilter<T>(A2AFilterStage stage = A2AFilterStage.AfterAuthorization)`; `AuthorizationFilter` registered as the built-in first filter)
- Modify: `tests/A2A.Tests/Parity/ParityTests.cs:34` and other `A2AAuthorizationHandler` subclasses in tests (parameter type rename only)
- Test: `tests/A2A.Tests/Pipeline/OperationFilterTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record A2AOperationDescriptor(A2AOperation Kind, string Name)
  { public static A2AOperationDescriptor For(A2AOperation kind); public static A2AOperationDescriptor Extension(string name); }
  public sealed class A2AOperationContext { public A2AOperationDescriptor Operation { get; } public ServerCallContext Call { get; } public IDictionary<string, object?> Items { get; } }
  public delegate ValueTask<object?> A2AOperationDelegate(A2AOperationContext context, object request, CancellationToken ct);
  public interface IA2AOperationFilter { ValueTask<object?> InvokeAsync(A2AOperationContext context, object request, A2AOperationDelegate next, CancellationToken ct); }
  public enum A2AFilterStage { BeforeAuthorization, AfterAuthorization }
  ```
  Non-generic shape chosen so the AOT analyzer stays quiet (spec `D19` allows it). Streaming operations pass an `IAsyncEnumerable<SequencedEvent>` as the `object?` result; a filter wraps it to see events. The handler casts the result back.

- [ ] **Step 1: Write tests**: `Filter_after_authorization_sees_only_authorized_calls` (denying auth handler; filter counter stays 0), `Filter_before_authorization_sees_denied_calls`, `Filter_can_short_circuit_with_A2AException` (returns `InvalidParams` to the client on both HTTP bindings), `Filter_sees_tenant_bound_from_body` (JSON-RPC body tenant `"t1"`; context `Call.Tenant == "t1"`), `Filters_run_in_registration_order`, `Streaming_filter_wraps_enumerable_and_counts_events`.
- [ ] **Step 2: Run.** FAIL.
- [ ] **Step 3: Implement** the pipeline. `OperationPipeline.RunAsync<TResponse>(A2AOperationDescriptor op, ServerCallContext call, object request, Func<CancellationToken, ValueTask<TResponse>> innermost, CancellationToken ct)` composes the filters once at construction (array of filters, closure chain built per call).
- [ ] **Step 4: Run all tests.** PASS.
- [ ] **Step 5: Commit** `feat(server): operation filter pipeline; authorization is the first filter; A2AOperationDescriptor`.

### Task 18: Telemetry

**Files:**
- Create: `src/A2A.Server/Telemetry/A2AServerTelemetry.cs` (`ActivitySource("A2A.Server")`, `Meter("A2A.Server")`, counters `a2a.server.requests`, `a2a.server.events`, `a2a.server.streams.opened`, `a2a.server.push.deliveries`), `TelemetryFilter.cs`
- Create: `src/A2A.Client/Telemetry/A2AClientTelemetry.cs` (`ActivitySource("A2A.Client")`)
- Modify: `src/A2A.Client/Transports/HttpTransportBase.cs` (start an activity per call; `traceparent` is injected by `HttpClient`'s `DistributedContextPropagator` when an activity is current, so only the activity is needed), `src/A2A.Client.Grpc/GrpcTransport.cs` (activity per call; Grpc.Net propagates)
- Modify: `src/A2A.Server/Execution/TaskExecutionEngine.cs` (events counter), `Push/PushNotifications.cs` (deliveries counter), `Streaming/TaskStreamer.cs` (streams counter)
- Modify: `src/A2A.Server/ServiceCollectionExtensions.cs` (`TelemetryFilter` registered `AfterAuthorization` before host filters)
- Modify: `Directory.Build.props` (remove the `CA1848` suppression from Task 4; convert the hot-path log calls in the engine and streamer to `[LoggerMessage]` partial methods)
- Test: `tests/A2A.Tests/Telemetry/TelemetryTests.cs`

**Interfaces:**
- Produces: span name `a2a.server.{OperationName}` with tags `a2a.operation`, `a2a.tenant`, `a2a.task_id_hash` (first 16 hex chars of SHA-256 of the task id), `a2a.outcome` (`ok`, `error:{A2AErrorCode}`); client span `a2a.client.{OperationName}`.

- [ ] **Step 1: Write tests** with an `ActivityListener` and `MeterListener`: `Server_span_per_operation_with_tags`, `Client_span_and_traceparent_header_sent` (use `RecordingHandler` to assert the header exists), `Events_counter_matches_emitted_events`, `Grpc_client_span_present`.
- [ ] **Step 2: Run.** FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run all tests.** PASS. Build with `CA1848` enforced: 0 warnings.
- [ ] **Step 5: Commit** `feat: ActivitySource and Meter for server and client; telemetry as the first pipeline filter`.

### Task 19: Operation table; public endpoints; extension operations

**Files:**
- Create: `src/A2A.Server.AspNetCore/Operations/A2AOperationTable.cs`, `A2AExtensionOperation.cs`
- Modify: `src/A2A.Server.AspNetCore/JsonRpcEndpoint.cs` (`public sealed`; the `switch` becomes a table lookup), `RestEndpoint.cs` (`public sealed`; routes come from the table; extension route `POST {prefix}/ext/{name}`)
- Modify: `src/A2A.Server/ServiceCollectionExtensions.cs` (`A2AServerBuilder.AddExtensionOperation(string name, Func<A2AOperationContext, JsonElement, CancellationToken, ValueTask<JsonElement>> handler, string? authorizationPolicy = null, bool allow = false)`)
- Modify: `src/A2A.Server/Authorization/A2AAuthorizationHandler.cs` (`AllowAllAuthorizationHandler` returns `false` for `Kind == Extension` unless the registration set `allow` or a policy; the table exposes `IsExplicitlyAllowed(name)` to it through `A2AOperationContext.Items["a2a.extension.allowed"]`)
- Modify: `src/A2A.Server/A2AServerOptions.cs` (`AgentCard.Capabilities.Extensions` gets an `AgentExtension { Uri = name }` entry per registered operation at startup)
- Test: `tests/A2A.Tests/Operations/OperationTableTests.cs`

**Interfaces:**
- Produces: `public sealed class A2AOperationTable { public IReadOnlyDictionary<string, A2AOperationDescriptor> JsonRpcMethods { get; } public IReadOnlyList<(string Method, string Route, A2AOperationDescriptor Op)> RestRoutes { get; } public IReadOnlyList<A2AExtensionOperation> Extensions { get; } }`; `public sealed record A2AExtensionOperation(string Name, Func<A2AOperationContext, JsonElement, CancellationToken, ValueTask<JsonElement>> Handler, string? AuthorizationPolicy, bool Allow)`.

- [ ] **Step 1: Write tests**: `Builtin_methods_unchanged` (the 11 method names map to the 11 descriptors), `Extension_without_policy_is_denied` (JSON-RPC method `"x.echo"` → error `-32603` with reason `PERMISSION_DENIED`; REST `POST /a2a/ext/x.echo` → 403), `Extension_with_allow_runs_over_both_bindings`, `Extension_appears_in_card_extensions`, `Unknown_method_is_MethodNotFound`, `Grpc_ignores_extensions` (gRPC service has no extra method; a compile-time fact, assert the table's `Extensions` is not consulted by `A2AGrpcService` via a reflection check that the gRPC assembly does not reference `A2AOperationTable`).
- [ ] **Step 2: Run.** FAIL.
- [ ] **Step 3: Implement.** The gRPC binding is exempt (spec `D21`).
- [ ] **Step 4: Run all tests.** PASS.
- [ ] **Step 5: Commit** `feat(aspnetcore): operation table; public endpoints; extension operations denied by default`.

### Task 20: Public resumable transport contract

**Files:**
- Modify: `src/A2A.Client/Streaming/ResumableStream.cs:7-15` (`StreamFrame`, `IResumableTransport` become `public`; namespace `A2A.Client.Streaming`)
- Modify: `src/A2A.Client/IA2ATransportFactory.cs` (XML: implementing `IResumableTransport` enables resume)
- Test: `tests/A2A.Tests/ClientResumeTests.cs` (add `Third_party_resumable_transport_gets_resume`)

- [ ] **Step 1: Write the test**: a test transport implementing both `IA2AClient` and `IResumableTransport`, created through a custom `IA2ATransportFactory` registered in `A2AClientOptions.TransportFactories`; after a simulated drop, assert the second `SubscribeToTaskAsync` call received `lastEventId == "t1:3"`.
- [ ] **Step 2: Run.** FAIL (types inaccessible).
- [ ] **Step 3: Make public**, update `PublicAPI.Unshipped.txt`.
- [ ] **Step 4: Run.** PASS.
- [ ] **Step 5: Commit** `feat(client): IResumableTransport and StreamFrame are public`.

## Stage 6: encode once, bounded, indexed

### Task 21: Encoded event payload (`D9`)

**Files:**
- Modify: `src/A2A.Server/Streaming/SequencedEvent.cs` (`public readonly record struct SequencedEvent(long Sequence, StreamResponse Event, ReadOnlyMemory<byte> Json)`; `Json` is the ProtoJSON of `Event`, empty when not yet encoded)
- Create: `src/A2A.Server/Streaming/EventEncoder.cs`
- Modify: `src/A2A.Server/Execution/TaskExecutionEngine.cs` (size check uses the encoded bytes; same bytes stored and published), `Storage/EventStore.cs` (`AppendAsync(TaskKey task, SequencedEvent evt, ...)` taking the encoded event; returns the sequence), `Storage/InMemoryEventStore.cs`, `Push/PushNotifications.cs:242` (writes `evt.Json`), `src/A2A.Server.AspNetCore/HttpPlumbing.cs:145-157,230` (`WriteEventAsync(HttpContext, string? id, ReadOnlyMemory<byte> json)`; one `WriteAsync` of a pooled frame built with `id:`, `data:` and terminators; REST SSE writes `evt.Json` directly)
- Test: `tests/A2A.Tests/Streaming/EncodedEventTests.cs`

**Interfaces:**
- Produces: `public static class EventEncoder { public static SequencedEvent Encode(long sequence, StreamResponse evt); public static int MeasureUtf8(StreamResponse evt); }`. `MeasureUtf8` writes to a counting `Stream` subclass that discards bytes.

- [ ] **Step 1: Write tests**: `Event_at_limit_is_accepted_one_over_fails` (set `Limits.MaxEventBytes` to the exact encoded size of a known event; assert accepted; add one byte of text; assert task FAILED with `OversizedEventText`), `Encoded_json_equals_fresh_serialization`, `Sse_frame_bytes_equal_previous_format` (frame is `id: {id}\ndata: {json}\n\n`), `Push_body_equals_event_json`.
- [ ] **Step 2: Run.** FAIL.
- [ ] **Step 3: Implement.** The executor's `TaskUpdater` still writes `StreamResponse`; the engine encodes once in `ConsumeAsync` after `ApplyToCopy` succeeds. `MarkTerminal`, replay and gap-fill paths carry the stored bytes.
- [ ] **Step 4: Run all tests** and benchmarks (`compare.py` exit 0; update baseline downward).
- [ ] **Step 5: Commit** `perf(server): encode each event once; SSE and push write the stored bytes`.

### Task 22: One encode per JSON-RPC envelope (`D10`)

**Files:**
- Create: `src/A2A.Protocol/JsonRpc/JsonRpcWriter.cs`
- Modify: `src/A2A.Server.AspNetCore/JsonRpcEndpoint.cs:153-166` (use the writer; streaming frames wrap `evt.Json`)
- Modify: `src/A2A.Client/Transports/JsonRpcTransport.cs:115-117` (request envelope written with the writer)
- Test: `tests/A2A.Tests/JsonRpcWriterTests.cs`

**Interfaces:**
- Produces: `public static class JsonRpcWriter { public static byte[] Success(JsonElement? id, ReadOnlySpan<byte> resultJson); public static void WriteSuccess<T>(Utf8JsonWriter w, JsonElement? id, T result, JsonTypeInfo<T> info); public static byte[] Request<T>(string id, string method, T @params, JsonTypeInfo<T> info); }`. Output is byte-identical to today's `JsonRpcResponse.Success` serialization (property order `jsonrpc`, `id`, `result`).

- [ ] **Step 1: Write tests**: `Success_bytes_equal_legacy_serialization` for a `StatusUpdateEvent` and for `null` id, `Request_bytes_equal_legacy_serialization`.
- [ ] **Step 2: Run.** FAIL.
- [ ] **Step 3: Implement**; remove `SerializeToElement` from both response paths. Add a `PackageRulesTests.No_SerializeToElement_on_response_paths` grep test over `src/A2A.Server.AspNetCore` and `src/A2A.Client/Transports`.
- [ ] **Step 4: Run all tests.** PASS.
- [ ] **Step 5: Commit** `perf: JSON-RPC envelopes written once with Utf8JsonWriter`.

### Task 23: One pass over bodies (`D11`)

**Files:**
- Modify: `src/A2A.Server.AspNetCore/HttpPlumbing.cs:288-371` (`ReadBodyAsync` returns `ReadOnlyMemory<byte>` from an `ArrayPool` buffer via a disposable `PooledBody`; `ParseBody` replaced by `static void CheckDepthAndDuplicates(ReadOnlySpan<byte> json, int maxDepth)` as one `Utf8JsonReader` pass, then `JsonSerializer.Deserialize(span, typeInfo)`)
- Modify: `src/A2A.Server.AspNetCore/JsonRpcEndpoint.cs:39`, `RestEndpoint.cs:228`
- Modify: `src/A2A.Client/Transports/JsonRpcTransport.cs:76,94`, `HttpJsonTransport.cs:107,140`, `A2ACardResolver.cs:151` (`ReadAsStreamAsync` + `JsonSerializer.DeserializeAsync(stream, typeInfo)`; error bodies parsed from bytes)
- Modify: `src/A2A.Client/Sse/SseReader.cs` (read with `PipeReader`, split on `\n`, parse field names as UTF-8 spans; `SseEvent.Data` still a string, built once per event)
- Test: existing `LimitsTests.cs` (depth, duplicate keys, body size), `ClientDefaultsTests.cs`, `StreamingResumeTests.cs`; add `SseReaderTests.Crlf_and_lf_and_multiline_data` in `tests/A2A.Tests/SseReaderTests.cs`

- [ ] **Step 1: Write `SseReaderTests`** (CRLF frames, `data:` across two lines joined with `\n`, comment lines ignored, `id:` captured, a 70,000-byte data line).
- [ ] **Step 2: Run.** PASS on the old reader (the test guards the rewrite). Then implement the server body path and the client path.
- [ ] **Step 3: Run all tests.** PASS. `LimitsTests` prove depth 32 and duplicate-key rejection on both TFMs (CI).
- [ ] **Step 4: Benchmarks**: `compare.py` exit 0; baseline downward.
- [ ] **Step 5: Commit** `perf: single pass over request bodies; streamed client responses; span-based SSE reader`.

### Task 24: Bounded channels (`D12`)

**Files:**
- Modify: `src/A2A.Server/A2AServerOptions.cs` (`StreamingOptions.ChannelCapacity { get; set; } = 256`)
- Modify: `src/A2A.Server/TaskEventBus.cs:17` (`Channel.CreateBounded<SequencedEvent>(new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true })`; constructor `TaskEventBus(StreamingOptions options)`)
- Modify: `src/A2A.Server/Execution/Execution.cs` (`Incoming` bounded with `FullMode = Wait`; `Outgoing` bounded with `Wait`)
- Test: `tests/A2A.Tests/Streaming/BoundedChannelTests.cs`

- [ ] **Step 1: Write tests**: `Slow_subscriber_receives_all_events_in_order` (capacity 4, agent emits 200 events, subscriber delays 5 ms per event, assert sequences 1..200 contiguous), `Fast_agent_is_throttled_not_dropped` (capacity 2, consumer paused; agent's 10th `AddArtifactAsync` does not complete until the consumer resumes), `Default_capacity_is_256`.
- [ ] **Step 2: Run.** FAIL (first test may pass on unbounded; the capacity assertions fail).
- [ ] **Step 3: Implement.** Add `PackageRulesTests.No_unbounded_channels_in_src` (grep for `CreateUnbounded`).
- [ ] **Step 4: Run all tests.** PASS.
- [ ] **Step 5: Commit** `perf(server): bounded channels; bus drops to store-backed gap fill, executor is throttled`.

### Task 25: Push configs indexed by task; one worker per task (`D13`)

**Files:**
- Modify: `src/A2A.Server/Storage/PushConfigStore.cs` (`InMemoryPushConfigStore` keyed `ConcurrentDictionary<TaskKey, ConcurrentDictionary<string, PushConfigRecord>>`)
- Modify: `src/A2A.Server/Push/PushNotifications.cs:105-176` (`Enqueue` writes to a per-task `Channel<SequencedEvent>` (bounded, `Wait`, capacity from `StreamingOptions.ChannelCapacity`); one worker task per `TaskKey`, started on first event, exits after idle `PushNotificationOptions.WorkerIdleTimeout` (default 30 s); configs listed once per batch, re-listed when a config is created or deleted through a `Invalidate(TaskKey)` call from `PushConfigService`)
- Test: `tests/A2A.Tests/PushDeliveryTests.cs` (existing; add `Config_added_mid_task_receives_later_events`, `Worker_exits_when_idle`)

- [ ] **Step 1: Write the two tests.**
- [ ] **Step 2: Run.** `Config_added_mid_task_receives_later_events` may pass; `Worker_exits_when_idle` fails (no observable worker count). Expose `internal int ActiveWorkers` for the test.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run all tests.** PASS; ordering and at-least-once tests unchanged.
- [ ] **Step 5: Commit** `perf(push): per-task delivery worker; configs indexed by task key`.

### Task 26: gRPC mapping over UTF-8 (`D14`)

**Files:**
- Modify: `src/A2A.Grpc/ProtoMapper.cs:21-41` (`ToModel<T>`: `Formatter.Format(proto, TextWriter)` into a `StreamWriter(UTF8, leaveOpen)` over a pooled `MemoryStream`, then `JsonSerializer.Deserialize(stream.GetBuffer().AsSpan(0, length), typeInfo)`; `ToProto<TProto>`: `JsonSerializer.SerializeToUtf8Bytes` then `Parser.Parse<TProto>(new StreamReader(new MemoryStream(bytes), UTF8))`. `JsonParser` accepts only `string` or `TextReader`, so the write side still decodes once inside the reader; no UTF-16 `string` object is created on either side)
- Test: existing `tests/A2A.Grpc.Tests` round-trip tests; add `ProtoMapperTests.Round_trip_equals_previous_mapping` comparing JSON of model → proto → model with the old path (kept as a private test helper).

- [ ] **Step 1: Write the test.**
- [ ] **Step 2: Implement.**
- [ ] **Step 3: Run `dotnet test tests/A2A.Grpc.Tests -f net10.0`.** PASS.
- [ ] **Step 4: Commit** `perf(grpc): mapping avoids UTF-16 strings where Google.Protobuf allows`.

## Stage 7: surface

### Task 27: `AddA2A`, `MapA2A` maps the card, `A2AEndpointOptions` (`D1`, `D2`)

**Files:**
- Modify: `src/A2A.Server.AspNetCore/A2AEndpointRouteBuilderExtensions.cs` (`AddA2A(this IServiceCollection services, Action<A2AServerBuilder> configure)` = `AddA2AServer()` + `AddA2AServerAspNetCore()` + `configure`; `MapA2A(this IEndpointRouteBuilder endpoints, string pattern, Action<A2AEndpointOptions>? configure = null)` maps the card at `/.well-known/agent-card.json` and JWKS unless `MapAgentCard == false`, applies `RequireAuthorization(options.AuthorizationPolicy)` when set)
- Create: `src/A2A.Server.AspNetCore/A2AEndpointOptions.cs` (`public sealed class A2AEndpointOptions { public bool MapAgentCard { get; set; } = true; public string? AuthorizationPolicy { get; set; } }`)
- Modify: `src/A2A.Server.AspNetCore/A2AStartupValidator.cs:99` (error text: `"Call app.UseA2A() before app.UseAuthorization(), or set A2AServerOptions.EnableRateLimiting = false."` and for authorization `"Call app.MapA2A(...).RequireAuthorization() or MapA2A(pattern, o => o.AuthorizationPolicy = \"...\"), or set A2AServerOptions.AllowAnonymous = true."`)
- Test: `tests/A2A.Tests/SurfaceTests.cs`

- [ ] **Step 1: Write tests**: `AddA2A_registers_core_and_aspnetcore` (resolve `IA2ARequestHandler`, `A2AEndpointRegistry`, `ITenantResolver`), `MapA2A_maps_card_by_default` (GET `/.well-known/agent-card.json` → 200 with no auth), `MapA2A_card_can_be_disabled`, `MapA2A_with_policy_requires_authorization` (no token → 401), `Startup_error_names_the_fix` (assert both message strings).
- [ ] **Step 2: Run.** FAIL.
- [ ] **Step 3: Implement.** Existing `MapAgentCard()` must not double-map: it checks the registry and is a no-op if the card is already mapped on that route.
- [ ] **Step 4: Run all tests.** PASS.
- [ ] **Step 5: Commit** `feat(aspnetcore): AddA2A one-call registration; MapA2A maps the card; A2AEndpointOptions`.

### Task 28: Card completion at first serve (`D3`)

**Files:**
- Modify: `src/A2A.Server.AspNetCore/A2AHttpOptions.cs` (`public Uri? PublicBaseUrl { get; set; }`)
- Modify: `src/A2A.Server.AspNetCore/A2AStartupValidator.cs:17` (`A2AEndpointRegistry` becomes `public sealed`; records `(string Binding, string Pattern)` per `MapA2A*` call; `MapA2AGrpc` registers `ProtocolBindings.Grpc`)
- Create: `src/A2A.Server.AspNetCore/AgentCardCompleter.cs`
- Modify: `src/A2A.Server.AspNetCore/AgentCardEndpoint.cs:42` (`Build` receives the completed card), `A2ARequestHandler.AgentCard` and `GetExtendedAgentCardAsync` read the completed card through `IAgentCardProvider`
- Create: `src/A2A.Server/IAgentCardProvider.cs` (`public interface IAgentCardProvider { AgentCard Get(); }`, synchronous because `IA2ARequestHandler.AgentCard` is a property; default implementation returns `options.AgentCard`; the ASP.NET Core package replaces it with the completer, which computes once on first call after the server has started)
- Modify: `src/A2A.Protocol/Models/AgentCard.cs:14-24` (`SupportedInterfaces`, `Capabilities`, `DefaultInputModes`, `DefaultOutputModes`, `Skills` lose `required`; defaults stay `new()`; `Name`, `Description`, `Version` stay `required`)
- Test: `tests/A2A.Tests/CardCompletionTests.cs`

**Interfaces:**
- Produces: `public sealed class AgentCardCompleter(IOptions<A2AServerOptions> options, IOptions<A2AHttpOptions> http, A2AEndpointRegistry registry, IServer server) : IAgentCardProvider`. Completion rule: if `SupportedInterfaces` is empty, one `AgentInterface` per registered binding with `Url = (PublicBaseUrl ?? first IServerAddressesFeature address) + pattern`; computed once, cached.

- [ ] **Step 1: Write tests**: `Empty_interfaces_are_filled_from_mapped_bindings` (JSON-RPC and HTTP+JSON at `/a2a`; assert two interfaces with `http://localhost/a2a`), `Explicit_interfaces_are_untouched`, `PublicBaseUrl_wins_over_server_address`, `Completed_card_is_what_gets_signed` (signed card's payload contains the filled interfaces), `Grpc_binding_is_listed_when_mapped` (in `tests/A2A.Grpc.Tests`).
- [ ] **Step 2: Run.** FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run all tests.** PASS. Interop agent in `interop/dotnet/InteropAgent` still sets explicit interfaces; leave it.
- [ ] **Step 5: Commit** `feat(aspnetcore): complete an empty Agent Card from the mapped bindings at first serve`.

### Task 29: gRPC client `CreateAsync(Uri)` (`D4`)

**Files:**
- Modify: `src/A2A.Client.Grpc/GrpcTransport.cs:164` (`A2AGrpcClientFactory.CreateAsync(Uri agentUrl, HttpClient httpClient, A2AClientOptions? options = null, GrpcChannelOptions? channelOptions = null, CancellationToken ct = default)` resolving the card with `A2ACardResolver`, selecting the gRPC interface, building the transport)
- Test: `tests/A2A.Grpc.Tests/GrpcClientFactoryTests.cs`

- [ ] **Step 1: Write test** `CreateAsync_resolves_card_and_picks_grpc_interface` against the gRPC test host.
- [ ] **Step 2: Run.** FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run gRPC tests.** PASS.
- [ ] **Step 5: Commit** `feat(grpc): A2AGrpcClientFactory.CreateAsync from the agent URL`.

### Task 30: Options tidy (`D6`)

**Files:**
- Modify: `src/A2A.Server/A2AServerOptions.cs:23` (`EnableRateLimiting` moves to `A2ARateLimitOptions.Enabled` in `src/A2A.Server.AspNetCore/RateLimiting/A2ARateLimiting.cs`; `A2AStartupValidator` reads it there)
- Modify: `src/A2A.Client/IA2AClient.cs:20` (`A2AClientOptions.Tenant` XML documents precedence: per-request `Tenant` > `A2AClientOptions.Tenant` > `AgentInterface.Tenant` of the selected interface)
- Test: existing `LimitsTests` rate-limit tests (update the switch location), `ClientDefaultsTests.Tenant_precedence_is_request_then_options_then_interface` (new)

- [ ] **Step 1: Write the precedence test.**
- [ ] **Step 2: Move the switch; fix references; update `PublicAPI.Unshipped.txt`.**
- [ ] **Step 3: Run all tests.** PASS.
- [ ] **Step 4: Commit** `refactor: rate-limit switch lives with its options; document tenant precedence`.

### Task 31: Samples, README, hello-world gate

**Files:**
- Modify: `samples/EchoAgent/Program.cs` (use `AddA2A`, `MapA2A("/a2a", o => o.AuthorizationPolicy = ...)`, drop `MapAgentCard`, drop explicit `SupportedInterfaces`), `samples/EchoClient/Program.cs` (reference `A2A.Client.DependencyInjection`)
- Modify: `README.md` (package table adds `A2A.Security.Http`, `A2A.Client.DependencyInjection`; "Server in 20 lines" becomes the Task 27 snippet; a "Design principles" section links the spec; "Extending" section lists the seams: `IA2AOperationFilter`, `IA2ARequestHandler`, stores, `IAgentExecutorFactory`, `IResumableTransport`, extension operations)
- Create: `tests/A2A.Tests/HelloWorldGateTests.cs`

- [ ] **Step 1: Write `HelloWorldGateTests.Sample_a2a_lines_within_budget`**: reads `samples/EchoAgent/Program.cs`, counts lines containing `A2A` (case-sensitive, excluding comments and `using` lines), asserts count `<= 6`; same for `EchoClient` with `<= 8`.
- [ ] **Step 2: Run.** FAIL on the current samples.
- [ ] **Step 3: Rewrite the samples and README.**
- [ ] **Step 4: Run all tests**, `dotnet run --project samples/EchoAgent -- --EchoAgent:ApiKey=x` in the background, `dotnet run --project samples/EchoClient -- http://localhost:5100/ hello x`. Expected: `ping -> pong` and a streamed echo.
- [ ] **Step 5: Run the AOT publish** for both samples locally (Task 4 command). Expected: 0 warnings.
- [ ] **Step 6: Commit** `docs: samples and README on the one-call surface; hello-world line budget test`.

### Task 32: Whole-plan verification

- [ ] **Step 1:** `dotnet build A2A.slnx -c Release` → 0 warnings. `dotnet test tests/A2A.Tests -f net10.0` and `dotnet test tests/A2A.Grpc.Tests -f net10.0` → all green. `wc -l src/A2A.Server/A2ARequestHandler.cs` → under 500.
- [ ] **Step 2:** `grep -rn "A2AJson.Clone\|SerializeToElement\|CreateUnbounded" src/` → only `A2AJson.SerializeToElement` definitions, no call sites on response paths.
- [ ] **Step 3:** Benchmarks: `compare.py` exit 0; commit the final `baseline.json`.
- [ ] **Step 4:** `interop/run.sh` with the Python SDK (CI job) green.
- [ ] **Step 5:** Update `PublicAPI.Unshipped.txt` files and commit `chore: final API snapshot after the principles refactor`.
