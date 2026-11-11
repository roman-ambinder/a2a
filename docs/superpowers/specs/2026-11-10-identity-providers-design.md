# Identity providers: client assertions, Azure credentials, server recipes

Date: 2026-11-10. Status: decided with the user (options A, B, C and E; AWS SigV4, option D, deferred).
Scope: `src/A2A.Client` (client assertions), a new package `src/A2A.Client.Identity.Azure`, tests, docs and snippets. No server code. Builds on S37 (`2026-11-08-s37-credential-providers-design.md`), whose rules R1 to R6 stay in force.

## 1. Problem

An agent that runs in a cloud has an identity already: an Azure managed identity, a Kubernetes service account, a workload identity federation. The client can only log in with a client id and a shared secret (`OAuth2ClientCredentialsProvider`), or with a provider the host writes. S37 listed `private_key_jwt` as a non-goal. So an agent on Azure or Kubernetes must keep a secret it does not need, or write its own provider.

The server side needs no code. The SDK does not log callers in (auth guide: "Your ASP.NET Core app does that"), and Entra tokens are checked by the host's JWT bearer handler. What is missing there is guidance.

## 2. Options taken

| Option | What | Where |
|---|---|---|
| A | How to write a provider for any other identity provider | Docs |
| B | Client assertions (RFC 7523) in the OAuth2 provider: `private_key_jwt` and federated tokens | `A2A.Client` |
| C | `AzureTokenCredentialProvider` over Azure.Core `TokenCredential` | New package `A2A.Client.Identity.Azure` |
| E | Server recipe for Entra: JWT bearer, owner id from `tid` and `oid` | Docs and snippet |
| D | AWS SigV4 signing | Deferred, then built: `2026-11-11-aws-sigv4-design.md` |

## 3. B: client assertions

### 3.1 API

```csharp
public sealed class OAuth2ClientCredentialsOptions
{
    // existing members...
    public Func<OAuth2ClientAssertionContext, CancellationToken, ValueTask<string>>? ClientAssertion { get; set; }
}

public sealed class OAuth2ClientAssertionContext   // ClientId, TokenEndpoint

public static class OAuth2ClientAssertions
{
    public static Func<OAuth2ClientAssertionContext, CancellationToken, ValueTask<string>> FromFile(string path);
    public static Func<OAuth2ClientAssertionContext, CancellationToken, ValueTask<string>> PrivateKeyJwt(AgentCardSigningKey key, TimeSpan? lifetime = null, TimeProvider? timeProvider = null);
}
```

### 3.2 Rules

- B1. Exactly one of `ClientSecret` and `ClientAssertion` is set. Both, or neither, is an `ArgumentException` at construction.
- B2. With an assertion the token request sends `client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer`, `client_assertion=<jwt>` and `client_id` in the form (RFC 7521 section 4.2, RFC 7523 section 2.2). No `Authorization` header. `ClientAuthentication` is ignored. `client_assertion` and `client_assertion_type` join the reserved `AdditionalParameters` names.
- B3. The assertion is fetched for every token request, never cached by the provider. A federated token file is rotated by its platform, and a `private_key_jwt` needs a fresh `jti`. Token caching is unchanged: an access token is still reused until it is due for refresh.
- B4. The assertion is a credential (R4, R6). It goes only to the token endpoint the provider already allows: `TokenEndpoint`, or the card's `tokenUrl` on the card's origin. It is redacted from error messages like the secret.
- B5. An assertion that is empty, longer than 64 KiB, or holds white space or control characters fails the token request with `Unauthenticated`. A delegate that throws fails it the same way. The message names the exception type only.
- B6. `FromFile` reads the file on each call, trims surrounding white space, and caps it at 64 KiB. It is how Kubernetes projected service account tokens and Azure workload identity (`AZURE_FEDERATED_TOKEN_FILE`) are handed to a pod.
- B7. `PrivateKeyJwt` signs a JWT with the claims `iss` and `sub` (the client id), `aud` (the token endpoint URL), `jti` (128 random bits), `iat`, `nbf` and `exp` (default lifetime 5 minutes, at most 1 hour). Header: `alg`, `typ: JWT`, `kid`. It reuses `AgentCardSigningKey` and `JwsAlgorithms`, so only asymmetric algorithms are possible.

### 3.3 Non-goals for B

- OIDC discovery. A discovery document chosen by the card would choose where the assertion goes, which breaks R4. The host sets `TokenEndpoint`.
- `client_secret_jwt` (HMAC with the shared secret): it keeps the shared secret.

## 4. C: Azure credentials

### 4.1 Package

`A2A.Client.Identity.Azure` (NuGet `RomanAmbinder.A2A.Client.Identity.Azure`), net10.0. Depends on `A2A.Client` and `Azure.Core` only. The host brings `Azure.Identity` and picks the credential (`ManagedIdentityCredential`, `WorkloadIdentityCredential`, `DefaultAzureCredential`, ...). This keeps MSAL and its dependencies out of the package.

### 4.2 API

```csharp
public sealed class AzureTokenCredentialOptions
{
    public IList<string> Scopes { get; }              // required, for example "api://my-agent/.default"
    public string? TenantId { get; set; }
    public string? SchemeName { get; set; }
    public Uri? AgentOrigin { get; set; }
    public TimeSpan RefreshBeforeExpiry { get; set; } // default 5 minutes, capped at half the lifetime
    public TimeSpan TokenRequestTimeout { get; set; } // default 30 seconds
    public TimeProvider TimeProvider { get; set; }
}

public sealed class AzureTokenCredentialProvider : IA2ACredentialProvider
{
    public AzureTokenCredentialProvider(TokenCredential credential, AzureTokenCredentialOptions options);
}
```

### 4.3 Rules (the decisions the user asked for)

- C1. Scopes. The host sets them, always. Card scopes such as `agent.invoke` are not Entra scopes, and a scope picks the token's audience. An empty `Scopes` is an `ArgumentException`. Card scopes are never sent to Entra.
- C2. Card schemes. The provider handles `oauth2SecurityScheme` (any flow), `openIdConnectSecurityScheme`, and `httpAuthSecurityScheme` with scheme `Bearer`. It sends `Authorization: Bearer`. `SchemeName` limits it to one scheme.
- C3. One agent per provider. A token for one audience must not reach another agent, which could replay it. The provider binds to `AgentOrigin` when set, else to the first card origin it accepts, and refuses every other origin (as the OAuth2 provider does without `TokenEndpoint`).
- C4. Caching. The provider caches the token itself, because not every `TokenCredential` does. Refresh at `ExpiresOn - RefreshBeforeExpiry` (capped at half the remaining lifetime), or at `RefreshOn` when Azure.Core sets it and it is earlier. Concurrent callers share one fetch, which runs on its own timeout so one caller's cancellation does not fail the others. When a refresh fails and the old token is still valid, the old token is used.
- C5. Failures. An exception from the credential becomes `A2AException(Unauthenticated)` with the exception type and the Azure message cleaned by `TextSanitizer`. Azure.Identity messages do not hold tokens. A token with white space or control characters is refused.
- C6. Targets: net10.0 only. `Azure.Core` supports older frameworks, but the client's netstandard2.0 build has a reduced surface and this package does not need it. If wrong: add the target later, it is additive.

## 5. A and E: docs

New guide `docs/site/guides/identity-providers.md`:

- Server: protect the endpoints with Entra (JWT bearer with `Authority` and `Audience`), and an `IPrincipalResolver` that uses `tid` and `oid`. The default resolver reads `NameIdentifier`, then `sub`. In Entra tokens `sub` is unique per application, so it changes between apps; `oid` is the stable id of the user or service principal inside a tenant. The tenant of A2A still never comes from a claim (multi-tenancy guide).
- Client: which option fits which platform (secret, `FromFile`, `PrivateKeyJwt`, the Azure package, a custom provider).
- Other identity providers (Okta, Auth0, Keycloak, Google): standard OAuth2, so `OAuth2ClientCredentialsProvider`. Anything else: a custom provider.
- AWS IAM: not built in. What a custom SigV4 provider must do, and why it is not built in yet.

## 6. Tests that prove it

B: form fields with an assertion and no `Authorization` header; secret and assertion both or neither refused; reserved parameter names refused; a new assertion per token request and none for a cached token; assertion redacted from an error body; empty, oversized, white space and throwing assertions fail with `Unauthenticated`; `FromFile` re-reads a rotated file and trims; `PrivateKeyJwt` header and claims, verified with the public key, a fresh `jti` each time, lifetime bounds.

C: a fake `TokenCredential` records requests: configured scopes and tenant are sent, card scopes are not; scheme matching; origin binding and `AgentOrigin`; caching and refresh time; `RefreshOn`; concurrent callers share one fetch; caller cancellation does not fail the shared fetch; refresh failure falls back to a valid token; credential failure becomes `Unauthenticated`; bad token refused; end to end through `A2AClientFactory.CreateAsync`.

## 7. Deferred: D, AWS SigV4

Built later: see `2026-11-11-aws-sigv4-design.md`, which decides the questions below. They were:

1. When to sign. No A2A security scheme describes SigV4, so card matching cannot pick it. Either the host opts in per agent, or a custom scheme name is agreed.
2. The body. SigV4 hashes the body. Streaming requests and retries need a body that can be read twice, or `UNSIGNED-PAYLOAD` where the service allows it.
3. The rule that a provider must not change `RequestUri` holds: SigV4 adds headers only.

## 8. Calls I made

1. `ClientAssertion` is a delegate, not a new `OAuth2ClientAuthentication` value. Setting it is the switch. If wrong: an enum value can be added later and mean the same.
2. `PrivateKeyJwt` reuses `AgentCardSigningKey` instead of a new key type. The name reads oddly here, but the type is exactly a key, a `kid` and an algorithm.
3. The Azure package takes a `TokenCredential` and does not reference `Azure.Identity`.
4. No DI helper in the Azure package: `AddCredentialProvider(sp => new AzureTokenCredentialProvider(...))` already works, and a helper would make the package depend on `A2A.Client.DependencyInjection`.
5. Default `RefreshBeforeExpiry` is 5 minutes for Azure (Entra tokens live about an hour) and stays 60 seconds for OAuth2.
