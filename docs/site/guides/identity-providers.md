# Identity providers

This guide is for agents that run in a cloud or behind a company identity provider: Microsoft Entra ID, Okta, Auth0, Keycloak, Kubernetes, AWS. It shows which piece of the SDK to use on each side.

The short version:

| You want to | Use |
|---|---|
| Accept callers with Entra (or any OIDC) tokens on your agent | Your ASP.NET Core JWT bearer handler, plus an `IPrincipalResolver` for Entra |
| Call an agent with a client id and secret | `OAuth2ClientCredentialsProvider` with `ClientSecret` |
| Call an agent with no secret, from Kubernetes or Azure workload identity | `OAuth2ClientCredentialsProvider` with `OAuth2ClientAssertions.FromFile` |
| Call an agent with your own key instead of a secret (`private_key_jwt`) | `OAuth2ClientCredentialsProvider` with `OAuth2ClientAssertions.PrivateKeyJwt` |
| Call an agent with an Azure managed identity or any Azure.Identity credential | `AzureTokenCredentialProvider` from `RomanAmbinder.A2A.Client.Identity.Azure` |
| Call an agent behind AWS IAM (API Gateway, Lambda function URLs) | `AwsSigV4Handler` from `RomanAmbinder.A2A.Client.Identity.Aws` |
| Anything else (a token service of your own) | Your own `IA2ACredentialProvider` |

## The server: accept Entra tokens

The SDK does not log callers in. Your app's authentication scheme does, and the SDK ties each task to the caller it sees (see [Authentication and ownership](auth-and-ownership.md)). For Entra, that scheme is the JWT bearer handler from `Microsoft.AspNetCore.Authentication.JwtBearer`:

[!code-csharp[](../../../samples/Snippets/IdentityProviders.cs#EntraServer)]

- `Authority` is your tenant. `Audience` is the application id URI of the agent's app registration.
- `MapInboundClaims = false` keeps the claim names of the token. The resolver below reads `oid` and `tid` by those names, and the policy reads `roles`. Without it (the handler's default is `true`), the handler renames them to long URIs: `oid` becomes `http://schemas.microsoft.com/identity/claims/objectidentifier`, `tid` becomes `http://schemas.microsoft.com/identity/claims/tenantid`, `roles` becomes `ClaimTypes.Role`, and `sub` becomes `ClaimTypes.NameIdentifier`.
- The policy asks for an app role. Define it on the agent's app registration and assign it to the callers you allow. Callers that use client credentials get it in the `roles` claim.

### The owner id

Each task belongs to an owner id. The default resolver reads `NameIdentifier`, then `sub`. In Entra tokens, `sub` is different for each application, so the same caller would get another owner id in another app. `oid` is the id of the user or service principal, and it is the same in every app of the tenant. A person has a different `oid` in each tenant, so use `tid` and `oid` together. See `sub`, `oid` and `tid` in Microsoft's [ID token claims reference](https://learn.microsoft.com/en-us/entra/identity-platform/id-token-claims-reference).

[!code-csharp[](../../../samples/Snippets/IdentityProviders.cs#EntraResolver)]

Register it with `AddSingleton<IPrincipalResolver, EntraPrincipalResolver>()`, as in the server example. A token with no `oid` or `tid` is refused as unauthenticated.

The A2A tenant still never comes from a claim. It comes from the route. A multi-tenant Entra app that maps Entra tenants to A2A tenants must check that the `tid` of the token matches the route tenant, for example in an `A2AAuthorizationHandler`. See [Multi-tenancy](multi-tenancy.md).

Okta, Auth0, Keycloak and other OIDC providers work the same way with their own `Authority` and `Audience`. Check which claim of your provider stays the same for one caller across your apps. The default resolver uses `NameIdentifier`, then `sub`.

## The client: call an agent

The card says how to authenticate. Credential providers read that and attach the credential, only to the card's origin and never across a redirect. See [Client credentials](client-credentials.md) for how a provider is chosen.

### With no secret: a token file

Kubernetes can mount a service account token into a pod, and Azure workload identity uses such a token to log in to Entra. The platform writes the file and rotates it. `FromFile` reads it again for every token request:

[!code-csharp[](../../../samples/Snippets/IdentityProviders.cs#FederatedTokenFile)]

- The provider sends the file's content as a client assertion (RFC 7523): `client_assertion_type` and `client_assertion` in the token request, and no secret.
- The token from the identity provider is cached as usual. The file is read only when a new token is fetched.
- The app registration (or the identity provider) must trust the token's issuer. For Azure, that is a federated credential on the app registration.

### With your own key: private_key_jwt

`PrivateKeyJwt` signs a short JWT for every token request with a key you hold. The identity provider checks it with the public key you registered.

[!code-csharp[](../../../samples/Snippets/IdentityProviders.cs#PrivateKeyJwt)]

- The JWT has `iss` and `sub` set to the client id, `aud` set to the token endpoint, a random `jti`, and `iat`, `nbf` and `exp`. It lives 5 minutes by default, at most 1 hour.
- Only asymmetric algorithms are possible (`ES256` to `ES512`, `RS256` to `RS512`, `PS256` to `PS512`).
- The header names the key with `kid` only. Publish the public key with `key.ToPublicJwk()`. An identity provider that wants a certificate thumbprint in the header (`x5t`), as Entra does for certificate credentials, needs your own delegate. For Entra, prefer a token file or the Azure package.

`ClientSecret` and `ClientAssertion` cannot both be set. Your own delegate works too: it gets the client id and the token endpoint, and returns the assertion. It is a credential, so it is sent only to the token endpoint the provider allows (`TokenEndpoint`, or the card's `tokenUrl` on the card's origin) and is removed from error messages. An empty assertion, one longer than 64 KiB, one with white space, or a delegate that throws fails the call with `Unauthenticated`.

### With an Azure credential

`RomanAmbinder.A2A.Client.Identity.Azure` turns any Azure.Core `TokenCredential` into a credential provider. Install `Azure.Identity` too, and pick the credential that fits where the agent runs:

[!code-csharp[](../../../samples/Snippets/IdentityProviders.cs#AzureManagedIdentity)]

With dependency injection:

[!code-csharp[](../../../samples/Snippets/IdentityProviders.cs#AzureDi)]

- **Scopes are yours.** Set the agent's application id URI and `/.default`. The scope picks who the token is for, so the card does not choose it, and card scopes such as `agent.invoke` are never sent to Entra. `Scopes` is required.
- **Card schemes.** The provider handles `oauth2SecurityScheme`, `openIdConnectSecurityScheme` and `httpAuthSecurityScheme` with scheme `Bearer`. It sends `Authorization: Bearer`. `SchemeName` limits it to one scheme.
- **One agent per provider.** A token for one agent must not reach another, which could replay it. The provider binds to `AgentOrigin` when you set it, or else to the first card origin it accepts, and refuses every other origin. Make one provider per agent.
- **Caching.** The provider caches the token itself, because not every credential does. It fetches a new one 5 minutes before expiry (`RefreshBeforeExpiry`, capped at half the lifetime), or earlier when the credential says so. Callers at the same time share one request, which has its own timeout (`TokenRequestTimeout`, 30 seconds). If a refresh fails while the old token is still valid, the old token is used.
- **Failures** are `A2AException` with `Unauthenticated`. The message names the credential's exception type and its message. A token with white space, a token type other than Bearer, or an expired token is refused.
- The package targets .NET 10. It depends on `Azure.Core` only, not on `Azure.Identity`.

### Any other identity provider

Okta, Auth0, Keycloak, Google and most others are standard OAuth2 identity providers. Use `OAuth2ClientCredentialsProvider` with their token endpoint and a secret, a token file or `private_key_jwt`.

For anything else, write a provider. Keep a token for the agent it was issued for:

[!code-csharp[](../../../samples/Snippets/IdentityProviders.cs#CustomIdp)]

### AWS IAM (SigV4)

An agent behind an AWS service that checks IAM, such as Amazon API Gateway with IAM authorization or a Lambda function URL with `AWS_IAM` auth, accepts only requests signed with AWS Signature Version 4. `RomanAmbinder.A2A.Client.Identity.Aws` signs them.

The agent itself never sees the signature: the AWS front door checks it. So its card usually declares no security scheme, and the card cannot say "sign this". You decide, per agent, with `AwsSigV4Handler`:

[!code-csharp[](../../../samples/Snippets/IdentityProviders.cs#AwsSigV4)]

With dependency injection:

[!code-csharp[](../../../samples/Snippets/IdentityProviders.cs#AwsSigV4Di)]

- **Region and Service** are required. `Service` is the signing name of the AWS service in front of the agent: `execute-api` for API Gateway, `lambda` for a Lambda function URL.
- **Only the agent is signed.** The handler signs requests to `AgentOrigin`, the card fetch included, and lets every other request through unsigned. A signature names its host, so it is no use to another host. `AgentOrigin` is required and must be HTTPS (loopback is exempt, `RequireHttps` turns this off).
- **Credentials.** Leave `Credentials` null to use the AWS SDK's default credential search (`DefaultAWSCredentialsIdentityResolver`), which the SDK describes as environment variables, the "default" profile and EC2 instance metadata. It is resolved on the first request. Or pass any `AWSCredentials`. Credentials are read for every request; the AWS SDK caches and refreshes them.
- **The body is hashed.** The A2A client's request bodies are in memory, so a retry or a resume is hashed again with no copy. A body that is a stream (a file upload) is buffered first, up to `MaxSignedBodyBytes` (10 MiB). A larger one fails before it is sent. `UnsignedPayload = true` skips the hash, for services that accept that.
- **Headers only.** Signing adds `Authorization`, `x-amz-date`, `x-amz-content-sha256` and, for temporary credentials, `x-amz-security-token`. A retry replaces them. The URI is not changed.
- **No redirects.** The inner handler must not follow redirects; the constructor checks a `SocketsHttpHandler` or `HttpClientHandler`. `AddA2AClient`'s primary handler does not follow them.
- **Failures.** Credentials that cannot be loaded fail the call with `Unauthenticated`. The message names the exception type only: AWS SDK messages can name profiles and files.

If the card does declare a scheme for SigV4, under a name you agreed on with the agent, use `AwsSigV4CredentialProvider` instead. It works like the other providers and binds to one agent:

[!code-csharp[](../../../samples/Snippets/IdentityProviders.cs#AwsSigV4Scheme)]

## What can go wrong

- **Every call is 401 on the server although the token is valid.** The token has no `oid` or `tid` (the resolver returns null), or claim names were mapped to long URIs. Set `MapInboundClaims = false`.
- **Every call is 403 although the caller has the app role.** The policy asks for `roles`, but the handler renamed it to `ClaimTypes.Role` (`MapInboundClaims` is `true` by default). Set `MapInboundClaims = false`, or require `ClaimTypes.Role` in the policy.
- **The same caller sees different tasks in two apps.** You use the default resolver with Entra. It reads `NameIdentifier` (the handler maps `sub` to it by default) or `sub`, and `sub` is different in each app. Use the Entra resolver above.
- **`ArgumentException`: "Set ClientSecret or ClientAssertion, not both".** Remove one.
- **`Unauthenticated` from the token endpoint with `invalid_client`.** The identity provider does not trust the assertion: wrong issuer or subject in the federated credential, wrong public key, or the clock is off.
- **`ArgumentException`: "Scopes is required".** The Azure provider needs a scope, for example `api://my-agent/.default`.
- **"This provider is bound to another agent".** You used one Azure or AWS provider for two agents. Make one per agent.
- **403 from API Gateway or a function URL saying the signature does not match.** `Region` or `Service` is wrong, or something changed a signed header after the handler ran. Put `AwsSigV4Handler` last, right above the primary handler.
- **"The request body is larger than MaxSignedBodyBytes".** A streamed body over the limit. Raise `MaxSignedBodyBytes`, or set `UnsignedPayload` if the service accepts it.

## See also

- [Authentication and ownership](auth-and-ownership.md)
- [Client credentials](client-credentials.md)
- [Multi-tenancy](multi-tenancy.md)
- [Peer identity and registry](peer-identity.md)
