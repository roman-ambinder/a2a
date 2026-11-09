using A2A.Client;

namespace A2A.Grpc.Tests;

/// <summary>Artifact seal and integrity hash over gRPC (S27). The JSON-RPC and HTTP+JSON twin is in the main test project.</summary>
public sealed class GrpcArtifactIntegrityTests : IAsyncLifetime
{
    private GrpcHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await GrpcHost.StartAsync();

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

    [Fact]
    public async Task Seal_only_event_round_trips_and_hashed_chunks_verify()
    {
        AssertChunksVerify(await StreamAsync(_host.Client(), "chunks"));
    }

    [Fact]
    public async Task A_tampered_chunk_reports_mismatch()
    {
        var client = _host.Client();
        var events = await StreamAsync(client, "tampered");
        using var verifier = new ArtifactVerifier();
        var result = Assert.Single(events.SelectMany(verifier.Observe));
        Assert.Equal(ArtifactVerificationStatus.Mismatch, result.Status);
        Assert.Equal("bad", result.ArtifactId);
        var task = await client.GetTaskAsync(events.OfType<TaskEvent>().First().Task.Id);
        Assert.Equal(ArtifactVerificationStatus.Mismatch, ArtifactVerifier.Verify(Assert.Single(task.Artifacts!)).Status);
    }

    [Fact]
    public async Task Get_task_verifies_and_shows_the_seal_key()
    {
        var client = _host.Client();
        var sent = (await client.SendMessageAsync("chunks")) is TaskResponse { Task: var t } ? t : throw new Xunit.Sdk.XunitException("Expected a task response.");
        AssertStored(sent);
        AssertStored(await client.GetTaskAsync(sent.Id));
    }
}
