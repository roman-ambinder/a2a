# Client credentials

An agent card says how callers must authenticate. The client can read that and attach the right credential for you. This guide is for code that calls an agent that needs OAuth2, an API key or a client certificate.

You pass credential providers to <xref:A2A.Client.A2AClientFactory> (or add them to a DI client). The client chooses providers from the card, once, when it creates the client. It then applies them to every request, and only on the origin the card came from.

## How the client picks providers

A card lists `securitySchemes` and `securityRequirements`:

```json
{
  "securitySchemes": {
    "oauth": { "oauth2SecurityScheme": { "flows": { "clientCredentials": { "tokenUrl": "https://agent.example.com/token", "scopes": { "agent.invoke": "Call the agent" } } } } },
    "key": { "apiKeySecurityScheme": { "location": "header", "name": "X-Api-Key" } }
  },
  "securityRequirements": [
    { "schemes": { "oauth": { "list": ["agent.invoke"] } } },
    { "schemes": { "key": { "list": [] } } }
  ]
}
```

The list of requirements is an OR. One requirement is an AND of schemes. The client walks the list in card order and takes the first requirement that:

- is not empty,
- names only schemes the card declares, and
- has a provider for every scheme (the first provider in your list that returns true from `CanHandle` wins).

The client reads only the card-level requirements. It does not read the requirements of a skill.

If no requirement can be met:

- The client is anonymous when the card declares no requirements, or declares an empty one (`{}`). An empty requirement is used only when no requirement with credentials can be met.
- Otherwise creation fails with `A2AException` (`Unauthenticated`). The message names the schemes the card declares. This is better than a 401 on the first call.

Order matters twice: the card's order of requirements, and your order of providers.

[!code-csharp[](../snippets/ClientCredentials.cs#Order)]

`CreateAsync` with providers throws `ArgumentException` when the list is empty.

## Providers

| Card scheme | Provider | What it does |
|---|---|---|
| `oauth2SecurityScheme` with a `clientCredentials` flow | <xref:A2A.Client.Credentials.OAuth2ClientCredentialsProvider> | Gets a token and sends `Authorization: Bearer`. |
| `httpAuthSecurityScheme` with scheme `Bearer`, or `openIdConnectSecurityScheme` | `OAuth2ClientCredentialsProvider`, only when `TokenEndpoint` is set | Same. |
| `apiKeySecurityScheme` in a header or cookie | <xref:A2A.Client.Credentials.ApiKeyCredentialProvider> | Sends the key under the name the card declares. |
| `mtlsSecurityScheme` | <xref:A2A.Client.Credentials.MutualTlsCredentialProvider> with <xref:A2A.Client.Credentials.A2AMutualTls> | Adds nothing per request. The certificate is in the connection. |
| Anything else | Your own <xref:A2A.Client.Credentials.IA2ACredentialProvider> | You decide. |

Other OAuth2 flows, API keys in the query string and HTTP Basic are not covered by the built-in providers.

### A custom provider

A provider has two methods. `CanHandle` says whether it can satisfy a scheme. `ApplyAsync` adds the credential to a request. The client calls `ApplyAsync` on every attempt, so retries and stream resumes get a fresh credential. A provider must not change `RequestUri`. If it does, the request fails with `InvalidOperationException`.

[!code-csharp[](../snippets/ClientCredentials.cs#Custom)]

## The origin rule

A credential goes only to the origin the card came from. This is the origin of the card URL, as in `ResolvedAgentCard.Source`.

- Two URLs have the same origin when scheme, port and host are equal. Default ports are normalized. The host is compared as the name that is dialed (`Uri.IdnHost`, ignoring ASCII case). User info, path and query are not part of it. `A2ACredentialBinding.IsSameOrigin` does this check.
- If the interface the client selects is on another origin, client creation fails with `InvalidAgentResponse`. This is checked when the client selects credentials.
- A request to another origin at send time is refused with `InvalidAgentResponse` before anything is sent.
- Credentials are sent over HTTPS only. Loopback is exempt. A non-HTTPS card origin fails with `InvalidRequest`, unless `A2ACardResolverOptions.RequireHttps` is false.
- The card fetch carries none of these credentials. (With mutual TLS the certificate belongs to the connection, so it is offered on the card fetch too.)
- There is no setting to allow another origin. If an agent serves its API on another host, the client refuses it.

## Redirects

The client never follows a redirect with credentials. A redirect could send them to another host.

- The default handler of `CreateAsync` is a `SocketsHttpHandler` with `AllowAutoRedirect = false` and `UseCookies = false`.
- If you pass your own handler, the client checks it. It walks the chain through `DelegatingHandler.InnerHandler`. A `SocketsHttpHandler` or `HttpClientHandler` with `AllowAutoRedirect = true` is refused with `ArgumentException`.
- A handler of any other type cannot be checked. A custom terminal handler must not follow redirects. That is on you.
- <xref:A2A.Client.Credentials.A2ACredentialHandler.EnsureNoAutoRedirect*> runs the same check if you need it.

This is why you cannot pass a plain `HttpClient` together with providers. The redirect setting belongs to the handler, and an `HttpClient` does not expose it. The overloads that take an `HttpClient` take no providers. Pass a handler instead.

## OAuth2 client credentials

[!code-csharp[](../snippets/ClientCredentials.cs#OAuth2)]

`ClientId` and `ClientSecret` are required.

**Token endpoint.** The client secret goes to the token endpoint, so the card cannot choose it freely.

- If you set `TokenEndpoint`, that URL is used.
- If you do not, the provider uses the `tokenUrl` of the card's client credentials flow, but only when that URL is on the card's origin.
- Most identity providers are on another origin. Set `TokenEndpoint` in that case. Without it, the client fails with the "no credential provider" error.

The endpoint must be HTTPS (loopback is exempt, `RequireHttps` can turn this off), with no user info and no fragment. Token requests do not follow redirects. A `TokenHandler` you pass is checked like any other handler.

**One provider per agent.** Without `TokenEndpoint`, the provider binds to the first card origin it accepts. It refuses every other origin, so one secret never goes to the token endpoints of different agents. Make one provider per agent, or set `TokenEndpoint`. With `TokenEndpoint` set, one provider can serve several agents, and tokens are cached per card origin.

**Scopes.** If you set `Scopes`, those are requested. If you do not, the scopes of the card's requirement are requested. The card chooses them then, so set `Scopes` when one client id serves several APIs.

**Tokens.**

- Client authentication is HTTP Basic by default. Set `ClientAuthentication` to `ClientSecretPost` to send the id and secret in the form body.
- `AdditionalParameters` adds form fields such as `audience`. `grant_type`, `scope`, `client_id` and `client_secret` are refused.
- A token is cached per token endpoint, card origin and scope set (at most 256 entries). It is reused until `RefreshBeforeExpiry` (60 seconds, capped at half the token lifetime) before it expires.
- Callers at the same time share one token request. The request has its own timeout (`TokenRequestTimeout`, 30 seconds), so one caller's cancellation does not fail the others.
- If a refresh fails while the old token is still valid, the old token is used and the next call tries again.
- Without `expires_in`, the lifetime is `DefaultTokenLifetime` (5 minutes). Only a `token_type` of Bearer (or none) is accepted. The response is capped at 64 KiB.
- The client does not react to a 401 by fetching a new token. Refresh is by expiry time.

Token failures are `A2AException` with `Unauthenticated`. The messages never contain the secret.

## API keys

[!code-csharp[](../snippets/ClientCredentials.cs#ApiKey)]

The card declares where the key goes and what it is called. The provider handles `header` and `cookie`. It refuses `query`: keys in URLs end up in logs and proxies, and a changed URI would trip the redirect rules. A card that only offers a query key makes creation fail.

The provider also refuses header names that could change how the request is framed or routed: `Host`, `Transfer-Encoding`, `Connection`, `Upgrade`, `Cookie`, `TE`, `Trailer`, `Proxy-Authorization`, `Allow`, `Expires`, `Last-Modified`, and any name that starts with `Content-` or `A2A-`. The name must also be a valid HTTP token.

A cookie key replaces a cookie of the same name in the `Cookie` header. An empty key, or one with control characters (for a cookie also `;`, `,` or white space), fails the request with `Unauthenticated`.

Pass a scheme name as the second argument to limit the provider to one scheme of the card.

## Mutual TLS

[!code-csharp[](../snippets/ClientCredentials.cs#MutualTls)]

Use the provider and the handler as a pair. `MutualTlsCredentialProvider` tells the client that the requirement is met. `A2AMutualTls.CreateHandler` builds the connection that proves it.

- The certificate must have a private key, and the agent URL must be absolute. Otherwise the call throws `ArgumentException`.
- The handler offers the certificate only to the host of the agent URL. It does not follow redirects and does not keep cookies.
- You own the certificate. The handler does not dispose it.
- The card is fetched through the same handler, so the certificate is also offered on the card fetch.

## gRPC

The client factory does not apply credentials to a gRPC interface. The gRPC transport owns its channel and never uses the `HttpClient`, so a credential would silently not be sent. When the card requires credentials and the client selects a gRPC interface, creation fails with `NotSupportedException`.

Wire it yourself. Create the binding with `forGrpcChannel: true`, and give the channel a handler built from it:

[!code-csharp[](../snippets/ClientCredentials.cs#Grpc)]

The origin and HTTPS checks still apply. The handler under the channel must not follow redirects. See [bindings](bindings.md).

## Dependency injection

`AddA2AClient` returns an `IHttpClientBuilder`. The credential methods are in <xref:A2A.Client.A2AClientCredentialExtensions> and work only on that builder. On any other builder they throw `InvalidOperationException`.

[!code-csharp[](../snippets/ClientCredentials.cs#DiOAuth)]

[!code-csharp[](../snippets/ClientCredentials.cs#DiOthers)]

- The providers are created when the client is first created, through `IA2AClientFactory.GetClientAsync`. The client is cached per name. The same rules apply as above.
- The factory builds the credentials client over the handler chain of the named client. Handlers you add stay in the chain. If the primary handler follows redirects and credentials are configured, creation fails before any request.
- `AddA2AClient` sets a primary handler with no cookies and no redirects. `AddMutualTls` replaces any primary handler set before it. It throws `InvalidOperationException` when the client has no `AgentUrl`.
- `AddA2AClient` redacts the value of every logged header. The card picks the header name, so an API key or token cannot show up in a log line from the HTTP client.
- `AddBearerToken` is older than the providers. It adds a token to every request. On an `AddA2AClient` builder it throws `A2AException` instead of sending the token to an origin other than the `AgentUrl` origin. It checks `AgentUrl`, not the card. Both it and a provider set `Authorization`, so use one of them.

## What can go wrong

- **Creation fails with "No credential provider satisfies the Agent Card's security requirements".** The card asks for something you have no provider for. Common causes: an API key in the query string, an OAuth2 `tokenUrl` on another origin without `TokenEndpoint`, a scheme with no provider, or a provider bound to another card origin.
- **Creation fails with "The selected interface is on origin ...".** The card points the interface to another host than the card itself. There is no opt-out.
- **`ArgumentException` about a handler that follows redirects.** Set `AllowAutoRedirect = false` on the handler. This also applies to the `TokenHandler` of OAuth2.
- **`NotSupportedException` for gRPC.** Wire the channel yourself, as shown above.
- **`Unauthenticated` from the token endpoint.** Wrong secret, scope or endpoint. The message holds the HTTP status and the OAuth `error` fields, with the secret removed.
- **Mutual TLS fails at the connection.** You used the provider without `A2AMutualTls.CreateHandler`, or the handler was built for another host.
- **A custom provider throws `InvalidOperationException`.** It changed `RequestUri`.

## See also

- [Authentication and ownership](auth-and-ownership.md#the-client-side) for the server side and delegation
- [Agent card signing](agent-card-signing.md)
- [Peer identity and registry](peer-identity.md)
- [Bindings](bindings.md)
