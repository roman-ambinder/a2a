using System.Security.Cryptography;
using A2A.Signing;
using BenchmarkDotNet.Attributes;

namespace A2A.Benchmarks;

/// <summary>Verifies an ES256-signed Agent Card against a pinned key.</summary>
[MemoryDiagnoser]
public class CardVerifyBenchmark
{
    private const string Origin = "https://agent.example";
    private static readonly Uri CardUri = new(Origin + "/.well-known/agent-card.json");
    private AgentCardVerifier _verifier = null!;
    private AgentCard _card = null!;
    private AgentCardSigningKey _key = null!;

    [GlobalSetup]
    public void Setup()
    {
        _key = new AgentCardSigningKey(ECDsa.Create(ECCurve.NamedCurves.nistP256), "bench-key", "ES256");
        _card = AgentCardSigner.Sign(new AgentCard
        {
            Name = "Signer", Description = "d", Version = "1.0.0",
            SupportedInterfaces = [new AgentInterface { Url = Origin + "/a2a", ProtocolBinding = ProtocolBindings.JsonRpc }],
            Capabilities = new AgentCapabilities { Streaming = true },
            DefaultInputModes = ["text/plain"], DefaultOutputModes = ["text/plain"],
            Skills = [new AgentSkill { Id = "s", Name = "S", Description = "x", Tags = ["t"] }],
        }, _key);
        var policy = new CardTrustPolicy();
        policy.TrustedKeys[CardTrustPolicy.OriginOf(CardUri)] = new JwkSet { Keys = [_key.ToPublicJwk()] };
        _verifier = new AgentCardVerifier(policy);
    }

    [GlobalCleanup]
    public void Cleanup() => _key.Key.Dispose();

    [Benchmark]
    public async Task<AgentCardVerificationResult> Verify()
    {
        var result = await _verifier.VerifyAsync(_card, CardUri);
        if (result.Status != AgentCardVerificationStatus.Verified) throw new InvalidOperationException(result.Reason);
        return result;
    }
}
