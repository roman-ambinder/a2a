# D25: netstandard2.0 for A2A.Protocol and A2A.Client

Closes gap D25 (official SDK issue a2aproject/a2a-dotnet#197). `A2A.Protocol` and `A2A.Client` add `netstandard2.0`, so .NET
Framework 4.7.2+ and older hosts can use the client. Servers stay net8+. net8.0 and net10.0 builds do not change.

## Survey result

A forced netstandard2.0 build of `A2A.Protocol` gave about 100 errors, all mechanical: `ThrowIfNull` (40), `KeyValuePair`
deconstruction, one-shot hash and hex helpers, span overloads. `A2AJsonContext` source generation works with the
`System.Text.Json` package. No large rewrite is needed, and nothing has to weaken.

## Package scope

| Project | netstandard2.0? | Why |
| --- | --- | --- |
| `A2A.Protocol` | Yes | Asked for. |
| `A2A.Client` | Yes | Asked for. |
| `A2A.Security.Http` | **No** | The guard is `SocketsHttpHandler.ConnectCallback`. See below. |
| `A2A.Client.DependencyInjection` | No | Not in D25. Can be a later change. |
| gRPC, v0.3 compat, Extensions.AI, servers | No | Not in D25. |

## Security decisions

The rule: a security feature on netstandard2.0 is the same as on net8+, or it is absent at compile time, or it fails closed.

**SSRF guard: absent.** netstandard2.0 has no connect-time hook. A check before the request has a DNS rebinding gap, so
it would be weaker while looking the same. `SafeHttp`, `GuardedJwksFetcher` and `PrivateAddressGuard` do not exist for
netstandard2.0 consumers, and using them is a compile error. `A2A.Client` references `A2A.Security.Http` only on net8+.

**Card verification: same checks, no `jku` fetch.** On netstandard2.0 `A2ACardResolver` builds `AgentCardVerifier` with
no fetcher. Signatures checked against trusted keys in `CardTrustPolicy` work as on net8+. A signature that needs a `jku`
fetch is `Unverified` with the reason "no JWKS fetcher configured". Once trust is configured, only `Verified` cards resolve, so
this fails closed. With `RequireSignature = false` the card is returned and its status says `Unverified`, as on net8+
when a `jku` fetch fails.

**`A2AMutualTls.CreateHandler`: absent.** It presents the certificate only to the agent host through
`SslOptions.LocalCertificateSelectionCallback`, which only `SocketsHttpHandler` has. `HttpClientHandler.ClientCertificates`
has no per-host rule. `MutualTlsCredentialProvider` stays: it adds nothing to the request.

**`ArtifactVerifier`: absent.** The streaming check needs `IncrementalHash.GetCurrentHash`, which netstandard2.0 does not
have, and the class promises to keep no content. `ArtifactHash.Compute` stays, so a caller can check a whole artifact.

**`CryptographicOperations.FixedTimeEquals`: real constant-time polyfill.** Loop over all bytes with OR-ed XOR, and
`[MethodImpl(NoInlining | NoOptimization)]`. Different lengths return false at once, as on net8+. Used for webhook tokens.

**Redirect rules: same.** The default handler on netstandard2.0 is `HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }`.
`A2ACredentialHandler.EnsureNoAutoRedirect` keeps its `HttpClientHandler` check and refuses unknown behaviour as today.

**RSA-PSS on .NET Framework.** `RSA.Create()` there returns `RSACryptoServiceProvider`, which cannot do PSS. On netstandard2.0,
`Jwk.ToPublicKey` creates `RSACng` by name when the runtime is .NET Framework and falls back to `RSA.Create()` when
that fails. Without PSS support, verification returns false (`JwsAlgorithms.Verify` already catches `CryptographicException`), so
PS256 fails closed. The net48 smoke run covers this.

**ECDSA signature format.** netstandard2.0 has no `DSASignatureFormat`. The default of `ECDsa.SignData`/`VerifyData` is
already IEEE P1363, which JWS needs. `#if` picks the old overload.

## How the code changes

**One polyfill file:** `src/Shared/NetStandardPolyfills.cs`, all `internal`, compiled only for netstandard2.0 into both
projects (`<Compile Include="..\Shared\*.cs" Condition=... />`). C# 14 static extensions (`extension(ArgumentNullException) { ... }`)
keep call sites unchanged, so most code needs no `#if`.

- Guards: `ArgumentNullException.ThrowIfNull`, `ArgumentException.ThrowIfNullOrEmpty`, `ArgumentOutOfRangeException.ThrowIf*`.
- Crypto and encoding: `SHA256.HashData`, `SHA256.HashSizeInBytes`, `HMACSHA256.HashData`, `HMACSHA256.HashSizeInBytes`,
  `Convert.ToHexString`, `Convert.TryFromBase64String`, `CryptographicOperations.FixedTimeEquals`.
- General: `KeyValuePair.Deconstruct`, `Enum.GetValues<T>`, `string.Create`, `char.IsAscii*`, `Random.Shared`, `Dictionary.Remove(key, out)`,
  the `string` overloads with `char`/`StringComparison`, `ArrayBufferWriter<T>`, `DynamicallyAccessedMembersAttribute`.
- HTTP and streams: `HttpContent.ReadAs*Async(ct)`, `Stream.ReadAsync(Memory<byte>, ct)`, `Task.WaitAsync(ct)`, `CancellationTokenSource.CancelAsync`.

**A polyfill must never change meaning without saying so.** So there is no `GetCurrentHash` polyfill (it would reset the hash).
`ArtifactHash.Compute` uses `GetHashAndReset` on all targets, since it owns the hash and has the same result.

**Cancellation.** `Stream.ReadAsync` on .NET Framework may ignore the token, and an SSE stream can then hang. The polyfill waits
on the read or the token, and throws `OperationCanceledException` when the token fires. The caller then disposes the stream.
`ReadAs*Async(ct)` polyfills throw if the token is already cancelled and also honour it while waiting.

**`HttpRequestException.StatusCode`** does not exist on netstandard2.0, but the code never reads it, so nothing is needed.
`RetryPolicy.NeverReachedServer` is always false there (no `HttpRequestError`), so a connect failure is not retried for non-idempotent requests.

**Number format.** On .NET Framework `ToString("R")` is not the shortest round-trip form (it tries 15 digits, then 17). RFC 8785 needs the
shortest, so `JsonCanonicalizer.FormatNumber` tries `G1` to `G17` on netstandard2.0 and takes the first that parses back to the same value.

**Unicode.** On .NET Framework, case mapping and Unicode data for supplementary-plane characters are older. Agent name normalization can differ
from net8 for those characters.

**Compiler types:** PolySharp as a private, build-only reference (`IsExternalInit`, `required`, `Index`/`Range`, nullable attributes,
`CallerArgumentExpression`). It adds no runtime dependency.

**`SocketsHttpHandler` sites** in `A2AClientFactory`, `OAuthTokenExchanger`, `OAuth2ClientCredentialsProvider` and
`A2ACredentialHandler` use `#if NETSTANDARD2_0` for `HttpClientHandler`. Public surface does not change.

**`JwsAlgorithms.All`** is `IReadOnlySet<string>`, which netstandard2.0 lacks. It is absent there; `IsSupported` stays.

**Build settings**

- Packages, on netstandard2.0 only (`ItemGroup` condition): `System.Text.Json` 8.0.x (patched, same line as net8), `Microsoft.Bcl.AsyncInterfaces`,
  `System.Diagnostics.DiagnosticSource`, plus only what the build proves is needed. net8/net10 dependency graphs do not change.
- `IsAotCompatible` only when `$(TargetFramework) != netstandard2.0`. AOT analyzers on net8/net10 do not change.
- `PublicAPI` files track net8/net10. On netstandard2.0 only, `NoWarn` RS0016 and RS0017 (the surface is a subset by design) and
  CS1574/CS1580 (doc `cref`s to `SocketsHttpHandler`). `EnablePackageValidation` stays on and proves that netstandard2.0 is a subset of net8.0.

## Test

`tests/A2A.NetStandard.Smoke`: a console app, `net10.0;net48`, exit code 0 on success. It references `A2A.Client` and `A2A.Protocol`.
On `net10.0` it uses `SetTargetFramework=TargetFramework=netstandard2.0`, so the netstandard2.0 DLLs run on every OS. On `net48` it
gets them through normal asset selection.

It takes an agent URL and checks, against `samples/EchoAgent` as a separate process:

1. Resolve the Agent Card.
2. `SendMessage`, on JSON-RPC and HTTP+JSON if the agent serves both.
3. Streaming, and cancelling a stream ends it promptly.
4. Local sign and verify with ES256, RS256 and PS256 through `AgentCardSigner` and `AgentCardVerifier`.
5. A card that needs `jku` is `Unverified`, and resolving it with trust configured is refused.

CI: run the `net10.0` flavour on ubuntu and windows, and `net48` on windows only, in the existing `build-test` job.

## Not in this change

- A public way to plug an `IJwksFetcher` into `A2ACardResolver` on netstandard2.0.
- `A2A.Client.DependencyInjection` on netstandard2.0.
