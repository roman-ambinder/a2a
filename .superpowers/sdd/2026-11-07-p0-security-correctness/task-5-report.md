# Task 5 report: push config store re-keyed, secrets protected and redacted

## Implemented
- `PushConfigStore` / `PushConfigRecord` / `InMemoryPushConfigStore` (Storage/PushConfigStore.cs), keyed by TaskKey + config id.
- `IPushSecretProtector` + `EphemeralPushSecretProtector` (AES-256-GCM, per-process key, 12-byte nonce, `e1:` + base64url(nonce||ct||tag)); default via TryAddSingleton in AddA2AServer (controller ruling 1; no pass-through).
- A2A.Server.AspNetCore: `DataProtectionPushSecretProtector` (purpose `A2A.PushSecrets.v1`), `AddA2ADataProtectionPushSecrets()` next to ConfigureA2AHttp.
- `IPushNotificationSender.Enqueue(TaskKey, StreamResponse)`; sender lists by TaskKey, unprotects at delivery only (a config that fails to unprotect is logged and skipped).
- Handler: protect-on-save helper used by Create and inline SendMessage config (both existing-task and new-task paths); `Redact` on Create/Get/List responses; ids via `TaskIds.New()`; private const `MaxPushConfigsPerTask = 10` -> `A2AException(InvalidRequest, "Too many push notification configs for this task (max 10).")` (overwriting an existing id is allowed).
- `PageCursor` moved to Storage/PageCursor.cs; Stores.cs deleted. README mention updated.

## TDD
RED: `dotnet test A2A.slnx -f net10.0 --filter "FullyQualifiedName~PushConfigTests"` -> build errors CS0246 (PushConfigStore, InMemoryPushConfigStore, PushConfigRecord, EphemeralPushSecretProtector, IPushSecretProtector, DataProtectionPushSecretProtector) and CS1061 (AddA2ADataProtectionPushSecrets missing). Expected: types did not exist.
GREEN: same command -> `Passed! Failed: 0, Passed: 14, Total: 14`. Full: `dotnet test A2A.slnx -f net10.0` -> `Failed: 0, Passed: 160`. Build `-warnaserror`: 0 Error(s), 0 Warning(s).

Tests (tests/A2A.Tests/PushConfigTests.cs): redaction (both bindings), stored secret not plaintext, inline config stored protected, foreign owner -> TaskNotFound, store owner/tenant keying, survives terminal state, max 10, ephemeral protector round trip/tamper/foreign key, DataProtection protector round trip and used by store path.
`Push_config_crud` unchanged and passing (never asserted secrets); delivery test still sees plaintext token and credentials.

## Concerns
- Max-count check is not atomic with save (concurrent creates could exceed 10 slightly). Task 7 / durable stores can tighten.
- Ephemeral default means configs held in a durable store become undeliverable after restart; documented on the type.

## Fix round 1
- EphemeralPushSecretProtector: no shared AesGcm; key kept as byte[], new `AesGcm(key, TagSize)` per Protect/Unprotect (using). No longer IDisposable. Added test: 64 parallel Protects, parallel Unprotects, all 64 nonces distinct.
- Inline push config on an existing task now goes through `EnsurePushCapacityAsync` (shared with Create; replacing an existing id allowed), same InvalidRequest text. Added test (both bindings): 10 configs via Create, then a follow-up SendMessage with inline config to the INPUT_REQUIRED task -> InvalidRequest, List still 10.
- Command: `dotnet test A2A.slnx -f net10.0 --filter "FullyQualifiedName~PushConfigTests"` -> Passed 17/17. Full suite `dotnet test A2A.slnx -f net10.0` -> see commit run: Failed 0. Build -warnaserror: 0 errors, 0 warnings.
