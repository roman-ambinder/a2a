# S37: credential providers selected by the card's security schemes

Date: 2026-11-08. Status: decided (routine decisions made without the user, listed in section 8).
Scope: `src/A2A.Client`, `src/A2A.Client.DependencyInjection`, tests and docs. No server code. No change to `A2ACardResolver` identity logic. Gap: S37 in `docs/2026-11-07-a2a-gap-analysis.md`, including CVE-2026-111763/5 (credentials following a redirect or going to another origin).

## 1. Problem

A card declares how to authenticate (`SecuritySchemes`, `SecurityRequirements`), but the client cannot act on it. `AddBearerToken` attaches whatever the caller fetched, to every request, on any origin. Nothing fetches OAuth2 tokens, nothing reads an API key's declared location, and nothing ties a credential to the origin the card came from.

## 2. Rules (the safety floor of this feature)

R1. A credential is attached only to a request whose origin equals the origin the card was resolved from (scheme, port with default ports normalized, and the dialed host: `Uri.IdnHost`, ordinal ignore case; `Uri.Host` is not used because case folding equates Greek sigma and final sigma). `A2ACredentialBinding.IsSameOrigin` is the one public helper, used by the binding, the handler, the OAuth provider and the DI bearer guard. Origins shown in messages or stored drop user info. A card interface on another origin fails client creation; a request to another origin at send time is refused.
R2. No credential crosses a redirect. A credential-bearing client refuses, at construction, any handler chain that follows redirects. Redirect following is a property of the handler below the client and cannot be seen on an `HttpClient`, so credentials are only accepted with a handler (section 4), never with an opaque `HttpClient`.
R3. The card fetch never carries credentials from this feature. With mTLS the certificate belongs to the connection, so it is offered to the agent host during the card fetch too.
R4. A client secret is a credential too. The card cannot choose where it goes: the token endpoint is the locally configured `TokenEndpoint`, or the card's `tokenUrl` only when it is on the card's origin. Token requests use HTTPS (loopback exempt) and do not follow redirects.
R5. Credentials travel over HTTPS only (loopback exempt), unless `A2ACardResolverOptions.RequireHttps` was turned off by the caller.
R6. Secrets never appear in exception messages, logs or `ToString`.

## 3. The seam

Namespace `A2A.Client.Credentials`, folder `src/A2A.Client/Credentials`.

```csharp
public interface IA2ACredentialProvider
{
    bool CanHandle(A2ACredentialContext context);
    ValueTask ApplyAsync(HttpRequestMessage request, A2ACredentialContext context, CancellationToken cancellationToken);
}

public sealed class A2ACredentialContext  // SchemeName, Scheme, Scopes, CardOrigin
```

`A2ACredentialBinding.Create(ResolvedAgentCard, AgentInterface, providers, requireHttps = true, forGrpcChannel = false)` selects what to apply once, at client creation:

- Walk `card.SecurityRequirements` in card order. A requirement is an AND of schemes; the list is an OR.
- Take the first non-empty requirement where every named scheme exists in `card.SecuritySchemes` and some provider `CanHandle` it (first provider in registration order wins).
- None satisfied, and an empty requirement (`{}`) exists or no requirements are declared: anonymous, no credentials.
- None satisfied otherwise: `A2AException(Unauthenticated)` naming the declared schemes. Failing at creation beats a 401 on the first call.
- Selected interface must pass R1, and R5 when credentials are selected. gRPC is refused unless `forGrpcChannel` is true (section 6). Null entries in the card (a null requirement, null `schemes`, a null scheme) are unsatisfiable, not an error.

`A2ACredentialHandler : DelegatingHandler` applies the binding on every attempt, so retries and stream resumes get a fresh token. It checks R1 before applying and that the provider left `RequestUri` alone after applying. Its constructor calls `EnsureNoAutoRedirect` on the inner chain, walking `DelegatingHandler.InnerHandler` and refusing `SocketsHttpHandler` or `HttpClientHandler` with `AllowAutoRedirect = true`. An unknown terminal handler is accepted (test doubles, custom transports) and documented.

## 4. Using it

Without DI:

```csharp
var client = await A2AClientFactory.CreateAsync(agentUrl,
    [new OAuth2ClientCredentialsProvider(new() { ClientId = id, ClientSecret = secret, TokenEndpoint = tokenUrl })]);
```

New overload `CreateAsync(Uri, IEnumerable<IA2ACredentialProvider>, HttpMessageHandler? handler = null, A2AClientOptions? = null, A2ACardResolverOptions? = null, CancellationToken = default)`. With no `handler`, the SDK uses `SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }`. The card is fetched through a plain client over the handler (R3), then an `HttpClient` over `A2ACredentialHandler` carries the calls. The existing `HttpClient` overloads are unchanged and take no providers.

With DI, on the builder `AddA2AClient` returns: `AddCredentialProvider(factory)`, `AddOAuth2ClientCredentials(configure)`, `AddApiKey(key)`, `AddMutualTls(certificate)`. `DefaultA2AClientFactory` resolves the card with the named client, builds the binding, wraps `IHttpMessageHandlerFactory.CreateHandler(name)` (so user handlers stay in the chain and R2 sees the real primary), and copies the named client's settings onto the authenticated `HttpClient`.

`AddBearerToken` predates this seam. It stays a pipeline handler, but when added to an `AddA2AClient` builder it now refuses to send the token off the `AgentUrl` origin (R1). Its public constructor is unchanged.

## 5. Built-in providers

`OAuth2ClientCredentialsProvider` handles `OAuth2Scheme` with a client credentials flow, and HTTP Bearer or OpenID Connect schemes when `TokenEndpoint` is configured. Client authentication is `client_secret_basic` by default, `client_secret_post` by option. Scopes: `Options.Scopes` when set, else the card requirement's scopes (the card chooses, so set `Scopes` when one client id serves several APIs). Without `TokenEndpoint`, the provider binds to the first card origin it accepts (`CanHandle` or `ApplyAsync`) and refuses every other origin, so one client secret never goes to token endpoints of different agents.

Token cache per (token endpoint, card origin, scopes), bounded to 256 slots. A token is reused until `RefreshBeforeExpiry` (default 60 s, capped at half its lifetime) before it expires. Concurrent callers share one fetch. The fetch runs on its own timeout (`TokenRequestTimeout`, default 30 s), so one caller's cancellation does not fail the others. If a refresh fails while the old token is still valid, the old token is used and the next call retries. No `expires_in`: `DefaultTokenLifetime` (5 min). Only `token_type` Bearer (or absent) is accepted. The token response is capped at 64 KiB.

`ApiKeyCredentialProvider` handles `ApiKeyScheme` at `header` or `cookie`, using the card's declared name. `query` is refused: keys in URLs end up in logs and proxies, and changing the URI would also trip the redirect check. Protocol-sensitive header names (`Host`, `Content-*`, `Transfer-Encoding`, `Connection`, `Upgrade`, `Cookie`, `TE`, `Trailer`, `Proxy-Authorization`, `A2A-*`) are refused, since the card picks the name.

`MutualTlsCredentialProvider` handles `MutualTlsScheme`; it attaches nothing per request, because a client certificate belongs to the TLS connection. `A2AMutualTls.CreateHandler(certificate, agentUrl)` returns a non-redirecting handler that offers the certificate only to the agent's host. The pair satisfies an mTLS requirement.

## 6. Non-goals

- gRPC through the client factory. `GrpcTransport` owns its channel and never uses the `HttpClient`, so a binding would silently not apply. Selecting a gRPC interface with providers fails with guidance. The supported route: `A2ACredentialBinding.Create(resolved, grpcInterface, providers, forGrpcChannel: true)`, then `GrpcChannelOptions.HttpHandler = new A2ACredentialHandler(binding, handler)` over a non-redirecting handler. Origin and HTTPS checks still apply.
- Skill-level `SecurityRequirements`; card-level only.
- Reactive refresh on 401, authorization code and device flows, `private_key_jwt`, OIDC discovery.
- Cross-origin endpoints by allowlist. R1 has no opt-out in this change.

## 7. Tests that prove it

Caching, refresh before expiry, concurrent callers sharing one fetch, caller cancellation not failing the shared fetch, refresh failure falling back to a valid token, no credential on redirect over real sockets (two Kestrel listeners), cross-origin refusal, card fetch uncredentialed, auto-redirect handler refused, requirement selection (order, AND, `{}`, no match), API key placement and refusals, mTLS host restriction, DI end to end, a custom provider swapped in through DI (D23).

## 8. Calls I made

1. Credentials are accepted with a handler, not an `HttpClient`, because only a handler's redirect setting can be checked. If wrong: an extra overload.
2. No cross-origin allowlist; gRPC on another port or a separate API host fails at creation with a clear error. If wrong: add an explicit allowlist later.
3. `AddBearerToken` gains an origin check. If wrong: a caller relying on cross-origin bearer tokens sees a refusal.
4. Card requirement `{}` is used only when no credentialed requirement can be met. If wrong: a credential is sent where the card also allowed anonymous; it still goes only to the card origin.
5. API keys in query strings are refused. If wrong: a custom provider cannot do it either; needs a change to the redirect check.
6. Card-chosen scopes are requested unless `Scopes` is set. If wrong: set `Scopes`.
