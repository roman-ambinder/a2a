# ClientFeatures sample

The client side of A2A in one app. The app hosts a small agent and a demo token endpoint on `127.0.0.1:5347`, then
calls the agent with two named clients from `AddA2AClient`. The agent replies with the caller and how it signed in.

| Step | Shows | Checks |
|---|---|---|
| OAuth2 client credentials | `AddOAuth2ClientCredentials`: the client gets a token and sends it as a bearer token | the call succeeds, and the agent saw a token that the demo endpoint issued |
| OAuth2 with private_key_jwt | `ClientAssertion = OAuth2ClientAssertions.PrivateKeyJwt(key)`: no secret, the client signs a JWT with its own key | the demo endpoint checked the signature, `aud` and `jti`, and issued a second token |
| Azure credential | `AzureTokenCredentialProvider` over a `TokenCredential` (a demo one stands in for Entra) | the call succeeds, and the credential was asked for the configured scope, not the card's |
| API key | `AddApiKey`: the card names the header (`X-Api-Key`), you give the key | the call succeeds as the API key caller |
| Retry | the agent answers 503 once; the client retries (`MaxRetries`, default 2) | the call succeeds after 2 attempts |
| Peer identity | `ResolvedAgentCard.Identity`: origin, key id and assurance | origin is `http://127.0.0.1:5347`, no key id, `OriginOnly` (the card is not signed) |
| Custom operation | `AddExtensionOperation` on the agent, `InvokeExtensionOperationAsync` on the client | the typed result comes back |

The card offers OAuth2 first and the API key second. The API key client has no OAuth2 provider, so it takes the second
requirement.

**The token endpoint is a demo, not an OAuth server.** It knows one client id, its secret and its public key, issues
random tokens and keeps them in memory. In production the token endpoint is your identity provider. The client id,
client secret, key and API key in the code are demo values. The demo Azure credential asks this endpoint instead of
Entra; in production use a credential from Azure.Identity, such as `ManagedIdentityCredential`.

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

--- OAuth2 with private_key_jwt
reply: demo-client via private_key_jwt, bearer token #2 from the demo token endpoint
tokens issued: 2

--- Azure credential
reply: demo-client via azure-credential, bearer token #3 from the demo token endpoint
scope asked for: api://client-features-agent/.default

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

OK: OAuth2 (secret and private_key_jwt), Azure credential and API key calls succeeded, a 503 was retried, the peer identity matched and a custom operation round-tripped.
```

Guides: [client credentials](../../docs/site/guides/client-credentials.md),
[identity providers](../../docs/site/guides/identity-providers.md),
[client resilience](../../docs/site/guides/client-resilience.md),
[peer identity](../../docs/site/guides/peer-identity.md),
[custom operations](../../docs/site/guides/custom-operations.md).
