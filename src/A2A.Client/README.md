# RomanAmbinder.A2A.Client

A2A (Agent2Agent) client for .NET: JSON-RPC and HTTP+JSON transports, SSE streaming, Agent Card discovery with HTTP
caching and signature checks. DI registration (`AddA2AClient`) is in `RomanAmbinder.A2A.Client.DependencyInjection`.

```csharp
var client = await A2AClientFactory.CreateAsync(new Uri("https://agent.example.com/"), httpClient);
var reply = await client.SendMessageAsync("hello");
```

Peer identity: `ResolvedAgentCard.Identity` is a `PeerIdentity` (origin and key id), and `AgentRegistry` rejects
duplicate agent names and warns on lookalikes;
see [Security](https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/spec-decisions.md).

Credentials: pass providers (`OAuth2ClientCredentialsProvider`, `ApiKeyCredentialProvider`,
`MutualTlsCredentialProvider`) instead of an `HttpClient`. They are chosen by the card's required schemes and sent only
to the card's origin, never across a redirect. To log in without a secret, set
`OAuth2ClientCredentialsOptions.ClientAssertion` (`OAuth2ClientAssertions.FromFile` for a Kubernetes or workload identity
token file, `OAuth2ClientAssertions.PrivateKeyJwt` for your own key, `OAuth2ClientAssertions.CertificateJwt` for a
certificate registered at the identity provider). Azure credentials:
`RomanAmbinder.A2A.Client.Identity.Azure`.

```csharp
var client = await A2AClientFactory.CreateAsync(new Uri("https://agent.example.com/"),
    [new ApiKeyCredentialProvider(apiKey)]);
```

Tracing: spans on the `A2A.Client` `ActivitySource`, with W3C `traceparent` sent on every call;
see [Observability](https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/observability.md).

Part of the [A2A .NET SDK](https://github.com/roman-ambinder/a2a),
implementing [A2A protocol v1.0](https://a2a-protocol.org/latest/specification/). Targets .NET 10 and netstandard2.0.

## On netstandard2.0

Works on .NET Framework 4.7.2 and later. Not available there:

- `jku` key fetch. A signature that needs one is `Unverified`. Verification against trusted keys still works, and a
  configured trust policy refuses unverified cards.
- `A2AMutualTls.CreateHandler`.
- `ArtifactVerifier`. Use `ArtifactHash.Compute` on the whole artifact.
- `A2AArtifactDownloader` and `A2AFileUploader`.
- `JwsAlgorithms.All`. `IsSupported` still works.
- `A2A.Security.Http` (SSRF guard).

A connect failure is not retried automatically for non-idempotent requests.
To stop calls to a peer that keeps failing, set `A2AClientOptions.CircuitBreaker` (off by default).
See the [getting started guide](https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/getting-started.md), the
[client credentials guide](https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/client-credentials.md) and the
[repository README](https://github.com/roman-ambinder/a2a#readme) for full docs.
