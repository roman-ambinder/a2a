# S37 credential providers: task plan

Spec: `docs/superpowers/specs/2026-11-08-s37-credential-providers-design.md` (binding authority; rules R1 to R6 are cited below).

## Global constraints

- Branch `claude/s37-credential-providers`, one commit or more per task. Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- `dotnet build A2A.slnx -c Release` must finish with 0 warnings (warnings are errors under `src/`). Every new public member needs an XML summary and a line in the project's `PublicAPI.Unshipped.txt` (nullable-annotated, sorted like the rest of the file).
- `A2A.Client` gains no package reference (`PackageRulesTests`). `A2A.Client.DependencyInjection` gains none either. No new `InternalsVisibleTo` between shipped packages.
- Public `CancellationToken` parameters are named `cancellationToken`. Use `ConfigureAwait(false)` in `src/`.
- No reflection-based JSON (AOT): use `JsonDocument`.
- Do not touch server projects or `A2ACardResolver`.
- Secrets never reach exception messages or logs. Options types are classes, not records, so `ToString` prints no secret.
- New code lives in `src/A2A.Client/Credentials/`, namespace `A2A.Client.Credentials`. Tests live in `tests/A2A.Tests/Credentials/`, namespace `A2A.Tests.Credentials`.
- Test command while iterating: `dotnet test tests/A2A.Tests -f net10.0 --filter FullyQualifiedName~Credentials`. Before committing, also build Release.
- Use `tests/A2A.Tests/FakeTimeProvider.cs` for clock-driven tests.

## Task 1: seam, binding, handler, no-DI factory

Create `IA2ACredentialProvider.cs`, `A2ACredentialBinding.cs`, `A2ACredentialHandler.cs`. Change `A2AClientFactory.cs`.

```csharp
public interface IA2ACredentialProvider
{
    bool CanHandle(A2ACredentialContext context);
    ValueTask ApplyAsync(HttpRequestMessage request, A2ACredentialContext context, CancellationToken cancellationToken);
}

public sealed class A2ACredentialContext
{
    public A2ACredentialContext(string schemeName, SecurityScheme scheme, IReadOnlyList<string> scopes, Uri cardOrigin);
    public string SchemeName { get; }
    public SecurityScheme Scheme { get; }
    public IReadOnlyList<string> Scopes { get; }
    public Uri CardOrigin { get; }   // scheme://host[:port], no path
}

public sealed class A2ACredentialBinding
{
    public static A2ACredentialBinding Create(ResolvedAgentCard card, AgentInterface selectedInterface,
        IEnumerable<IA2ACredentialProvider> providers, bool requireHttps = true);
    public Uri CardOrigin { get; }
    public IReadOnlyList<A2ACredentialContext> Schemes { get; }
    // internal: the (context, provider) pairs the handler applies
}

public sealed class A2ACredentialHandler : DelegatingHandler
{
    public A2ACredentialHandler(A2ACredentialBinding binding, HttpMessageHandler innerHandler);  // calls EnsureNoAutoRedirect
    public static void EnsureNoAutoRedirect(HttpMessageHandler handler);  // ArgumentException
}
```

Binding rules:
- Origin comes from `card.Source` (`GetLeftPart(UriPartial.Authority)`). Do not use `Identity`.
- Selection exactly as spec section 3. Provider order is registration order. A requirement naming a scheme missing from `SecuritySchemes` is unsatisfiable.
- No provider for a required scheme: `A2AException(A2AErrorCode.Unauthenticated, ...)`. The message lists declared scheme names and adds: API keys in query strings are refused and OAuth2 token URLs on another origin need `TokenEndpoint`.
- If credentials were selected: selected interface URL must be same-origin as the card, else `A2AException(A2AErrorCode.InvalidAgentResponse, ...)` naming both origins. Compare with `Uri.Compare(a, b, UriComponents.Scheme | UriComponents.Host | UriComponents.StrongPort, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase)`.
- If credentials were selected and `requireHttps`: card origin must be https or loopback, else `A2AException(InvalidRequest)`.
- If credentials were selected and `selectedInterface.ProtocolBinding == ProtocolBindings.Grpc`: throw `NotSupportedException` with guidance to set `GrpcChannelOptions.HttpHandler` over `A2ACredentialHandler`.

Handler `SendAsync`: no schemes means pass through. Else require same origin as `CardOrigin`, else throw `A2AException(InvalidAgentResponse)`. Apply each provider. If `request.RequestUri` changed, throw `InvalidOperationException`. Send.

Factory overload (no ambiguity with the `HttpClient` overloads, which stay as they are):

```csharp
public static async Task<A2AClient> CreateAsync(Uri agentUrl, IEnumerable<IA2ACredentialProvider> credentialProviders,
    HttpMessageHandler? handler = null, A2AClientOptions? options = null, A2ACardResolverOptions? resolverOptions = null,
    CancellationToken cancellationToken = default)
```

Materialize providers, throw `ArgumentException` when empty. Default handler `new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false }`. Call `EnsureNoAutoRedirect` before any network. Fetch the card with `new HttpClient(primary, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan }` through `A2ACardResolver.ResolveAsync`, so R3 holds. Select the interface with `SelectInterface` on a copy of options; when none fits, throw the same error `Create` throws (share it). Build the binding with `requireHttps: resolverOptions?.RequireHttps ?? true`. Build `new HttpClient(new A2ACredentialHandler(binding, primary)) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 16 * 1024 * 1024 }` and return `Create(resolved.Card, thatClient, options)`.

Tests (`CredentialBindingTests.cs`, `CredentialHandlerTests.cs`), with a recording test-double provider and a fake `HttpMessageHandler`:
1. First satisfied requirement in card order wins.
2. AND requirement needs a provider for every scheme; partial match is skipped.
3. First provider in registration order wins for a scheme.
4. `[{}, {oauth}]` with a provider uses oauth; with no matching provider is anonymous.
5. No requirements declared: anonymous, nothing applied.
6. No match with a non-empty requirement: `Unauthenticated`, message names the schemes.
7. Requirement naming an undeclared scheme is skipped.
8. Scopes in the context come from the requirement.
9. Interface on another origin (host, port, scheme each): `InvalidAgentResponse`. Same origin with explicit default port is accepted.
10. Plain http on a non-loopback origin refused; loopback accepted; accepted with `requireHttps: false`.
11. gRPC interface refused when credentials selected; allowed when anonymous.
12. Handler applies on every send (retries get `ApplyAsync` again).
13. Handler refuses another origin at send time and does not call the provider or the inner handler.
14. Handler refuses a provider that changes `RequestUri`.
15. `EnsureNoAutoRedirect` refuses default `SocketsHttpHandler`, `HttpClientHandler`, and either nested under a `DelegatingHandler`; accepts `AllowAutoRedirect = false` and an unknown handler.
16. No-DI `CreateAsync`: the card request carries no credential, the A2A request does; a redirecting handler is refused before any request; empty providers throw.

## Task 2: OAuth2 client credentials

Create `OAuth2ClientCredentialsProvider.cs`.

```csharp
public enum OAuth2ClientAuthentication { ClientSecretBasic, ClientSecretPost }

public sealed class OAuth2ClientCredentialsOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public Uri? TokenEndpoint { get; set; }
    public IList<string>? Scopes { get; set; }
    public OAuth2ClientAuthentication ClientAuthentication { get; set; } = OAuth2ClientAuthentication.ClientSecretBasic;
    public IDictionary<string, string> AdditionalParameters { get; } = new Dictionary<string, string>(StringComparer.Ordinal);  // e.g. audience, resource
    public string? SchemeName { get; set; }             // only this card scheme, when set
    public TimeSpan RefreshBeforeExpiry { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan DefaultTokenLifetime { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan TokenRequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public bool RequireHttps { get; set; } = true;      // loopback always allowed
    public HttpMessageHandler? TokenHandler { get; set; } // default: non-redirecting SocketsHttpHandler; caller keeps ownership
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}

public sealed class OAuth2ClientCredentialsProvider : IA2ACredentialProvider, IDisposable
{
    public OAuth2ClientCredentialsProvider(OAuth2ClientCredentialsOptions options);
}
```

Constructor: snapshot options. `ArgumentException` for empty id or secret, a non-absolute or `RequireHttps`-violating `TokenEndpoint`, a `TokenEndpoint` with user info or fragment, `AdditionalParameters` keys `grant_type`, `scope`, `client_id`, `client_secret`. `ArgumentOutOfRangeException` for negative `RefreshBeforeExpiry`, non-positive `DefaultTokenLifetime` or `TokenRequestTimeout`. `EnsureNoAutoRedirect` on `TokenHandler`. Own `HttpClient`, `MaxResponseContentBufferSize = 64 * 1024`, `Timeout = InfiniteTimeSpan`, not disposing a caller's handler.

`CanHandle`: honours `SchemeName`. True for `OAuth2Scheme` with `Flows.ClientCredentials` when `TokenEndpoint` is set, or when the flow's `TokenUrl` is absolute, same origin as `context.CardOrigin`, and passes `RequireHttps`. True for `HttpAuthScheme` with scheme `Bearer` (case-insensitive) and `OpenIdConnectScheme` only when `TokenEndpoint` is set. False otherwise.

`ApplyAsync`: resolve the endpoint (configured, else card), scopes (options, else context; distinct, ordinal sorted, space joined), get the token, set `request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token)`.

Cache, mandatory shape (a fetch must never run under the lock, and callers must not cancel the shared fetch):

```csharp
private readonly ConcurrentDictionary<(string Endpoint, string Origin, string Scope), Slot> _slots = new();

private sealed class Slot
{
    public readonly object Gate = new();
    public CachedToken? Token;
    public Task<CachedToken>? InFlight;
}
private sealed record CachedToken(string Value, DateTimeOffset RefreshAt, DateTimeOffset ExpiresAt);

private async ValueTask<string> GetTokenAsync(key, CancellationToken cancellationToken)
{
    var slot = GetSlot(key);
    Task<CachedToken> fetch;
    CachedToken? current;
    lock (slot.Gate)
    {
        current = slot.Token;
        if (current is not null && Now < current.RefreshAt) return current.Value;
        fetch = slot.InFlight ??= Task.Run(() => FetchAndStoreAsync(slot, key));
    }
    try { return (await fetch.WaitAsync(cancellationToken).ConfigureAwait(false)).Value; }
    catch (A2AException) when (current is not null && Now < current.ExpiresAt) { return current.Value; }
}
```

`FetchAndStoreAsync` uses a `CancellationTokenSource(TokenRequestTimeout, TimeProvider)`, stores the result and clears `InFlight` under `slot.Gate` (`InFlight` is cleared on failure too). `GetSlot`: when the map has 256 or more entries, first remove slots with no `InFlight`.

Token request: `POST`, `application/x-www-form-urlencoded`, `grant_type=client_credentials`, `scope` when non-empty, additional parameters. Basic: `Authorization: Basic base64(utf8(WebUtility.UrlEncode(id) + ":" + WebUtility.UrlEncode(secret)))`. Post: `client_id` and `client_secret` in the body. `Accept: application/json`.

Response: any non-2xx, including 3xx, is `A2AException(Unauthenticated)` with status and sanitized `error` / `error_description` (best-effort parse, `TextSanitizer.Sanitize` to 200 chars). Malformed JSON, missing or control-character `access_token`, non-Bearer `token_type`, or negative `expires_in` is `A2AException(Unauthenticated)`. `expires_in` may be a JSON number or numeric string. Every other failure is wrapped as `A2AException(Unauthenticated, ...)` with the inner exception. No message includes the secret, the token or the request body.

Timing: `issuedAt` is taken before the request. `lifetime` is `expires_in` or `DefaultTokenLifetime`. `ExpiresAt = issuedAt + lifetime`. `RefreshAt = ExpiresAt - min(RefreshBeforeExpiry, lifetime / 2)`.

Tests (`OAuth2ClientCredentialsProviderTests.cs`), fake token handler counting calls:
1. Two applies, one fetch, same token (caching).
2. After advancing past `RefreshAt`, a new fetch and the new token (refresh before expiry, and not before it).
3. 50 concurrent callers while the fetch is gated: exactly one fetch, all get the token.
4. A caller cancelled mid-fetch gets `OperationCanceledException`; others waiting succeed, one fetch.
5. Failed refresh with a still-valid token returns the old token; the next call retries; a failed fetch with no valid token throws `Unauthenticated`.
6. Short lifetime: refresh skew capped at half; `expires_in` missing uses `DefaultTokenLifetime`; string `expires_in` accepted.
7. Separate cache entries per scope set and per card origin; scope order does not matter.
8. Basic and Post wire formats, including URL-encoding of id and secret.
9. Configured `TokenEndpoint` beats the card's `tokenUrl`; the secret reaches only the configured endpoint.
10. `CanHandle` matrix: cross-origin card `tokenUrl` without `TokenEndpoint` is false; same-origin is true; Bearer and OIDC only with `TokenEndpoint`; `SchemeName` filter; authorization-code-only flow false.
11. Redirect from the token endpoint: one request, `Unauthenticated`, no re-post. A redirecting `TokenHandler` is refused in the constructor.
12. Error messages contain neither secret nor token, including when the server echoes them in a non-`error` field.
13. Non-Bearer `token_type`, malformed JSON, oversize body, timeout all fail `Unauthenticated`.
14. Constructor validation.

## Task 3: API key and mTLS

Create `ApiKeyCredentialProvider.cs`, `MutualTlsCredentialProvider.cs` (contains `A2AMutualTls`).

```csharp
public sealed class ApiKeyCredentialProvider : IA2ACredentialProvider
{
    public ApiKeyCredentialProvider(string apiKey, string? schemeName = null);
    public ApiKeyCredentialProvider(Func<CancellationToken, ValueTask<string>> apiKey, string? schemeName = null);
}

public sealed class MutualTlsCredentialProvider(string? schemeName = null) : IA2ACredentialProvider;

public static class A2AMutualTls
{
    public static SocketsHttpHandler CreateHandler(X509Certificate2 certificate, Uri agentUrl);
}
```

API key `CanHandle`: `ApiKeyScheme`, location `header` or `cookie` (case-insensitive), name a valid RFC 7230 token, name not protocol-sensitive (spec section 5 list, case-insensitive, `Content-` and `A2A-` as prefixes). Applying validates the key at use: empty or containing control characters is `A2AException(Unauthenticated)` with no key in the message; a cookie value also refuses `;`, `,` and whitespace. Header: `request.Headers.Remove(name)` then add. Cookie: `TryAddWithoutValidation("Cookie", $"{name}={key}")`.

mTLS handler: `AllowAutoRedirect = false`, `UseCookies = false`, `SslOptions.LocalCertificateSelectionCallback` returns the certificate only when `targetHost` equals `agentUrl.IdnHost` (ordinal ignore case), else null. Refuse a certificate with no private key.

Tests (`ApiKeyCredentialProviderTests.cs`, `MutualTlsTests.cs`): header placement under the card's declared name; cookie appended to an existing cookie; delegate source called per apply; query refused by `CanHandle`; each sensitive header name refused; invalid token name refused; `SchemeName` filter; bad key values refused with no key in the message. mTLS: callback returns the cert for the agent host and null for another, handler does not redirect, no-private-key refused, provider `CanHandle` only `MutualTlsScheme`, apply changes nothing, and a binding with an mTLS requirement selects it. Generate the certificate with `CertificateRequest.CreateSelfSigned`.

## Task 4: DI integration and the bearer origin check

Change `A2AClientServiceCollectionExtensions.cs` (new extensions may go in `A2AClientCredentialExtensions.cs` in the same project).

```csharp
public static IHttpClientBuilder AddCredentialProvider(this IHttpClientBuilder builder, Func<IServiceProvider, IA2ACredentialProvider> factory);
public static IHttpClientBuilder AddOAuth2ClientCredentials(this IHttpClientBuilder builder, Action<OAuth2ClientCredentialsOptions> configure);
public static IHttpClientBuilder AddOAuth2ClientCredentials(this IHttpClientBuilder builder, Action<IServiceProvider, OAuth2ClientCredentialsOptions> configure);
public static IHttpClientBuilder AddApiKey(this IHttpClientBuilder builder, string apiKey, string? schemeName = null);
public static IHttpClientBuilder AddApiKey(this IHttpClientBuilder builder, Func<IServiceProvider, CancellationToken, ValueTask<string>> apiKey, string? schemeName = null);
public static IHttpClientBuilder AddMutualTls(this IHttpClientBuilder builder, Func<IServiceProvider, X509Certificate2> certificate, string? schemeName = null);
```

- These need the A2A client name: strip the `A2A:` prefix from `builder.Name` (share the constant with `HttpClientName`). A builder without the prefix throws `InvalidOperationException` telling the caller to use the builder `AddA2AClient` returned.
- Store factories in an `internal List<Func<IServiceProvider, IA2ACredentialProvider>>` on `A2ANamedClientOptions`, through `builder.Services.Configure<A2ANamedClientOptions>(clientName, ...)`.
- `AddMutualTls`: `ConfigurePrimaryHttpMessageHandler(sp => A2AMutualTls.CreateHandler(certificate(sp), agentUrlFromOptions))` plus `MutualTlsCredentialProvider`. No `AgentUrl` throws `InvalidOperationException`.
- `DefaultA2AClientFactory` adds `IServiceProvider` and `IHttpMessageHandlerFactory` constructor parameters. With no factories, behaviour is unchanged. With factories: resolve through the named client with `resolver.ResolveAsync` (no credentials, R3); call `A2AClientFactory.SelectInterface` on `o.Client`; when none fits, fall through to `A2AClientFactory.Create` so it throws its usual error; build the binding with `requireHttps: o.Resolver.RequireHttps`; make `new HttpClient(new A2ACredentialHandler(binding, handlerFactory.CreateHandler(HttpClientName(name))), disposeHandler: false)`, copying `Timeout`, `MaxResponseContentBufferSize`, `BaseAddress`, `DefaultRequestVersion`, `DefaultVersionPolicy` and `DefaultRequestHeaders` from the named client; return `Create(resolved.Card, authed, o.Client)`.
- `AddBearerToken`: on an A2A builder, the handler refuses (`A2AException(InvalidAgentResponse)`, before calling the token provider) a request off the `AgentUrl` origin. Add an internal constructor with the allowed origin, read from options lazily at handler creation. Public constructor unchanged and unguarded; update its XML docs to say so. A non-A2A builder keeps today's behaviour.

Tests (`CredentialDiTests.cs`, `CredentialRedirectTests.cs`):
1. DI end to end with OAuth: card fetch uncredentialed, call carries the bearer, second call reuses the token.
2. A custom `IA2ACredentialProvider` swapped in through `AddCredentialProvider` (D23).
3. `AddApiKey` both overloads.
4. Replacing the primary with a redirecting handler plus credentials fails at `GetClientAsync` with `ArgumentException`.
5. A `ConfigureHttpClient` default header and a user `AddHttpMessageHandler` still apply on the authenticated client.
6. A builder not from `AddA2AClient` throws.
7. `AddBearerToken`: sends on the agent origin, refuses off-origin without calling the token provider.
8. No providers: behaviour unchanged.
9. Real sockets (`CredentialRedirectTests`): two Kestrel listeners on `127.0.0.1:0`. Origin A serves a card (same-origin JSON-RPC interface, an API key scheme; second test OAuth with a same-origin token URL) and answers `/a2a` with 307 to origin B. Both DI and no-DI paths. Assert: `SendMessageAsync` throws `A2AException`; A saw the credential on `/a2a` and none on the card request; B received zero requests.
10. Real sockets: a card whose interface is on origin B fails client creation; B received zero requests.

## Task 5: docs

- `README.md` Client section: short authentication paragraph with the no-DI one-liner, DI form, the origin and redirect rules, gRPC note. Add to the package table row for `A2A.Client.DependencyInjection`.
- `src/A2A.Client/README.md`, `src/A2A.Client.DependencyInjection/README.md`: short snippet each.
- `docs/2026-11-07-a2a-gap-analysis.md`: mark S37 as implemented the way the file marks finished items; if it has no such convention, add nothing but a short "Status" note in that table row.
- `src/A2A.Client/IA2ATransportFactory.cs` XML docs: one sentence saying credential-bearing clients hand the factory an `HttpClient` that carries credentials, so a factory that ignores it sends none.
