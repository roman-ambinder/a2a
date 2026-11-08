# RomanAmbinder.A2A.Client.DependencyInjection

Dependency-injection registration for the A2A (Agent2Agent) .NET client: named clients with `IHttpClientFactory`, card resolution and bearer-token handlers.

```csharp
services.AddA2AClient("weather", o => o.AgentUrl = new Uri("https://agent.example.com/"))
        .AddBearerToken((sp, ct) => tokens.GetAsync(ct));

var client = await provider.GetRequiredService<IA2AClientFactory>().GetClientAsync("weather");
```

Credentials: `AddOAuth2ClientCredentials`, `AddApiKey`, `AddMutualTls` and `AddCredentialProvider` pick the provider by the card's required schemes. Credentials go only to the card's origin, never across a redirect, and every logged header value is redacted. With mTLS the certificate is also offered to the agent host during the card fetch. `AddBearerToken` refuses to send the token off the `AgentUrl` origin.

```csharp
services.AddA2AClient("weather", o => o.AgentUrl = new Uri("https://agent.example.com/"))
        .AddOAuth2ClientCredentials(o => { o.ClientId = "id"; o.ClientSecret = secret; o.TokenEndpoint = new Uri("https://idp.example.com/token"); o.Scopes = ["agent.invoke"]; });
```

The named client has no cookies, no redirects and no `HttpClient` timeout (limits are per call), and buffers response bodies up
to 16 MiB; set `o.MaxResponseContentBufferSize` to change that.

Use `RomanAmbinder.A2A.Client` alone when you do not use a DI container; this package adds the `Microsoft.Extensions.*` dependencies.

Part of the [A2A .NET SDK](https://github.com/roman-ambinder/a2a), implementing [A2A protocol v1.0](https://a2a-protocol.org/latest/specification/). Targets .NET 8 and .NET 10.
See the [repository README](https://github.com/roman-ambinder/a2a#readme) for full docs.
