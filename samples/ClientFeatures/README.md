# ClientFeatures sample

The client side of A2A in one app. The app hosts a small agent and a demo token endpoint on `127.0.0.1:5347`, then
calls the agent with two named clients from `AddA2AClient`. The agent replies with the caller and how it signed in.

| Step | Shows | Checks |
|---|---|---|
| OAuth2 client credentials | `AddOAuth2ClientCredentials`: the client gets a token and sends it as a bearer token | the call succeeds, and the agent saw a token that the demo endpoint issued |
| API key | `AddApiKey`: the card names the header (`X-Api-Key`), you give the key | the call succeeds as the API key caller |
| Retry | the agent answers 503 once; the client retries (`MaxRetries`, default 2) | the call succeeds after 2 attempts |
| Peer identity | `ResolvedAgentCard.Identity`: origin, key id and assurance | origin is `http://127.0.0.1:5347`, no key id, `OriginOnly` (the card is not signed) |
| Custom operation | `AddExtensionOperation` on the agent, `InvokeExtensionOperationAsync` on the client | the typed result comes back |

The card offers OAuth2 first and the API key second. The API key client has no OAuth2 provider, so it takes the second
requirement.

**The token endpoint is a demo, not an OAuth server.** It knows one client id and secret, issues random tokens and keeps
them in memory. In production the token endpoint is your identity provider. The client id, client secret and API key
in the code are demo values.

No security default is turned off. Credentials need HTTPS, but loopback is exempt. The default endpoint policy accepts
a card on a loopback host that names a loopback endpoint.

mTLS is not part of this sample. See the [client credentials guide](../../docs/site/guides/client-credentials.md#mutual-tls).

```bash
dotnet run --project samples/ClientFeatures
```

Expected output ends with:

```text
--- OAuth2 client credentials
reply: demo-client via oauth2, bearer token #1 from the demo token endpoint
tokens issued: 1

--- API key
reply: api-key-user via api-key

--- Retry after a 503
reply: api-key-user via api-key
attempts: 2

--- Peer identity
origin: http://127.0.0.1:5347
key id: none
assurance: OriginOnly
identity: http://127.0.0.1:5347 (origin only)

--- Custom operation
https://example.com/reports/2026/sales?for=api-key-user
round-trip: https://example.com/reports/2026/sales?for=api-key-user

OK: OAuth2 and API key calls succeeded, a 503 was retried, the peer identity matched and a custom operation round-tripped.
```

Guides: [client credentials](../../docs/site/guides/client-credentials.md),
[client resilience](../../docs/site/guides/client-resilience.md),
[peer identity](../../docs/site/guides/peer-identity.md),
[custom operations](../../docs/site/guides/custom-operations.md).
