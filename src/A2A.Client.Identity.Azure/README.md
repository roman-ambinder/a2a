# RomanAmbinder.A2A.Client.Identity.Azure

Azure identity for the A2A (Agent2Agent) client. Call agents with a managed identity, workload identity or any
Azure.Core `TokenCredential`, instead of a client secret.

```csharp
// dotnet add package RomanAmbinder.A2A.Client.Identity.Azure
// dotnet add package Azure.Identity
var options = new AzureTokenCredentialOptions();
options.Scopes.Add("api://my-agent/.default");

var entra = new AzureTokenCredentialProvider(new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned), options);
var client = await A2AClientFactory.CreateAsync(new Uri("https://agent.example.com/"), [entra]);
```

With `AddA2AClient`: `.AddAzureTokenCredential(o => o.Scopes.Add("api://my-agent/.default"))` uses the `TokenCredential`
registered in the service collection; an overload takes a factory for the credential. `AgentOrigin` defaults to the
client's `AgentUrl`.

- Handles the card's `oauth2SecurityScheme`, `openIdConnectSecurityScheme` and HTTP `Bearer` schemes, and sends
  `Authorization: Bearer`.
- `Scopes` is required. You choose the token's audience; card scopes are never sent to Entra.
- One provider per agent: it binds to `AgentOrigin`, or to the first card origin it accepts, and refuses others.
- The token is cached and refreshed 5 minutes before it expires (`RefreshBeforeExpiry`). Callers at the same time share
  one request. A failed refresh keeps using the old token while it is valid.
- Credential failures are `A2AException` with `Unauthenticated`.
- Depends on `Azure.Core` only. Bring `Azure.Identity` (or any other `TokenCredential`) yourself. Targets .NET 10.

To log in with no secret and no Azure SDK, `OAuth2ClientCredentialsProvider` with
`OAuth2ClientAssertions.FromFile(AZURE_FEDERATED_TOKEN_FILE)` also works for Azure workload identity.

Guide: [Identity providers](https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/identity-providers.md).
Part of the [A2A .NET SDK](https://github.com/roman-ambinder/a2a).
