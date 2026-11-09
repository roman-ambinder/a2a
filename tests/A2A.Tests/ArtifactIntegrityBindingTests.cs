using A2A.Client;

namespace A2A.Tests;

/// <summary>Artifact seal and integrity hash over JSON-RPC and HTTP+JSON (S27). The gRPC twin is in the gRPC test project.</summary>
public sealed class ArtifactIntegrityBindingTests : IAsyncLifetime
{
    private TestAgentHost _host = null!;

    public static TheoryData<string> Bindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson };

    public async ValueTask InitializeAsync() => _host = await TestAgentHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    // sha256 of "hello ", "hello w\u00f6rld" (computed outside .NET)
    private const string FirstChunk = "sha256:5e3235a8346e5a4585f8c58562f5052b8fe26a3bb122e1e96c76784964dfc461";
    private const string Whole = "sha256:821cd58a9fb899141dd98c29b6cabb6ccdded70ad0197b8cc7657b76f70e64ff";

    private static async Task<List<StreamResponse>> StreamAsync(A2AClient client, string text)
    {
        var events = new List<StreamResponse>();
        await foreach (var e in client.SendStreamingMessageAsync(Message.FromUser(text))) events.Add(e);
        return events;
    }

    private static void AssertChunksVerify(List<StreamResponse> events)
    {
        using var verifier = new ArtifactVerifier();
        var results = events.SelectMany(verifier.Observe).ToList();
        Assert.Equal(3, results.Count);
        Assert.All(results, r => { Assert.Equal(ArtifactVerificationStatus.Verified, r.Status); Assert.Equal("chunked", r.ArtifactId); });
        var chunks = events.OfType<ArtifactUpdateEvent>().Select(e => e.ArtifactUpdate).ToList();
        Assert.Equal(FirstChunk, chunks[0].Artifact.Metadata!["sha256"].GetString());
        Assert.Equal(Whole, chunks[1].Artifact.Metadata!["sha256"].GetString());
        // The seal-only event: empty parts, the last chunk, the same hash as the final artifact.
        Assert.Empty(chunks[2].Artifact.Parts);
        Assert.True(chunks[2].Append);
        Assert.True(chunks[2].LastChunk);
        Assert.Equal(Whole, chunks[2].Artifact.Metadata!["sha256"].GetString());
        // The seal key is the server's own, not on the wire.
        Assert.All(chunks, c => Assert.False(c.Artifact.Metadata!.ContainsKey(A2AProtocol.ArtifactSealedKey)));
    }

    private static void AssertStored(AgentTask task)
    {
        var artifact = Assert.Single(task.Artifacts!);
        Assert.Equal(["hello ", "w\u00f6rld"], artifact.Parts.OfType<TextPart>().Select(p => p.Text));
        Assert.True(artifact.Metadata![A2AProtocol.ArtifactSealedKey].GetBoolean());
        Assert.Equal(Whole, artifact.Metadata["sha256"].GetString());
        Assert.Equal(ArtifactVerificationStatus.Verified, ArtifactVerifier.Verify(artifact).Status);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Seal_only_event_round_trips_and_hashed_chunks_verify(string binding)
    {
        var events = await StreamAsync(_host.Client(binding), "chunks");
        AssertChunksVerify(events);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task A_tampered_chunk_reports_mismatch(string binding)
    {
        var events = await StreamAsync(_host.Client(binding), "tampered");
        using var verifier = new ArtifactVerifier();
        var result = Assert.Single(events.SelectMany(verifier.Observe));
        Assert.Equal(ArtifactVerificationStatus.Mismatch, result.Status);
        Assert.Equal("bad", result.ArtifactId);
        Assert.Equal("sha256:2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", result.Expected);
        // The same mismatch shows in the stored task.
        var task = await _host.Client(binding).GetTaskAsync(events.OfType<TaskEvent>().First().Task.Id);
        Assert.Equal(ArtifactVerificationStatus.Mismatch, ArtifactVerifier.Verify(Assert.Single(task.Artifacts!)).Status);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Get_task_verifies_and_shows_the_seal_key(string binding)
    {
        var client = _host.Client(binding);
        var sent = (await client.SendMessageAsync("chunks")).RequireTask();
        AssertStored(sent);
        AssertStored(await client.GetTaskAsync(sent.Id));
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task A_snapshot_shows_each_artifact_with_its_seal_and_verifies(string binding)
    {
        var client = _host.Client(binding);
        var sent = (await client.SendMessageAsync("chunks")).RequireTask();
        // A finished task: ObserveTaskAsync yields its snapshot as a TaskEvent.
        var events = new List<StreamResponse>();
        await foreach (var e in client.ObserveTaskAsync(sent.Id)) events.Add(e);
        using var verifier = new ArtifactVerifier();
        var result = Assert.Single(events.SelectMany(verifier.Observe));
        Assert.Equal(ArtifactVerificationStatus.Verified, result.Status);
    }
}
