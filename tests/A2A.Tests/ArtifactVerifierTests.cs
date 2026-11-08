using System.Text.Json;
using A2A.Client;
using A2A.Integrity;

namespace A2A.Tests;

public sealed class ArtifactVerifierTests
{
    // sha256 of "foo", "foobar" (computed outside .NET)
    private const string Foo = "sha256:2c26b46b68ffc68ff99b453c1d30413413422d706483bfa0f98a5e886266e7ae";
    private const string FooBar = "sha256:c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2";

    private static JsonElement Str(string s) => JsonSerializer.SerializeToElement(s);

    private static Dictionary<string, JsonElement>? Hash(string? value) => value is null ? null : new() { [ArtifactHash.MetadataKey] = Str(value) };

    private static StreamResponse Chunk(string text, string? hash, string id = "a", bool append = false, bool last = false, bool noParts = false) =>
        StreamResponse.Of(new TaskArtifactUpdateEvent
        {
            TaskId = "t", ContextId = "c", Append = append, LastChunk = last,
            Artifact = new Artifact { ArtifactId = id, Parts = noParts ? [] : [Part.FromText(text)], Metadata = Hash(hash) },
        });

    private static ArtifactVerification One(ArtifactVerifier v, StreamResponse e) => Assert.Single(v.Observe(e));

    [Fact]
    public void Cumulative_hashes_verify_chunk_by_chunk()
    {
        using var v = new ArtifactVerifier();
        Assert.Equal(ArtifactVerificationStatus.Verified, One(v, Chunk("foo", Foo)).Status);
        Assert.Equal(ArtifactVerificationStatus.Verified, One(v, Chunk("bar", FooBar, append: true)).Status);
        // A seal-only event repeats the hash.
        var sealOnly = One(v, Chunk("", FooBar, append: true, last: true, noParts: true));
        Assert.Equal(ArtifactVerificationStatus.Verified, sealOnly.Status);
        Assert.Equal("a", sealOnly.ArtifactId);
    }

    [Fact]
    public void A_changed_chunk_is_a_mismatch_with_both_values()
    {
        using var v = new ArtifactVerifier();
        Assert.Equal(ArtifactVerificationStatus.Verified, One(v, Chunk("foo", Foo)).Status);
        var bad = One(v, Chunk("baz", FooBar, append: true));
        Assert.Equal(ArtifactVerificationStatus.Mismatch, bad.Status);
        Assert.Equal(FooBar, bad.Expected);
        Assert.Equal(ArtifactHash.Compute([Part.FromText("foobaz")]), bad.Actual);
    }

    [Fact]
    public void A_lost_or_repeated_chunk_is_a_mismatch()
    {
        // The producer sent foo, bar, baz. The consumer never saw "bar".
        using var lost = new ArtifactVerifier();
        One(lost, Chunk("foo", Foo));
        Assert.Equal(ArtifactVerificationStatus.Mismatch,
            One(lost, Chunk("baz", ArtifactHash.Compute([Part.FromText("foobarbaz")]), append: true)).Status);
        using var repeated = new ArtifactVerifier();
        One(repeated, Chunk("foo", Foo));
        One(repeated, Chunk("bar", FooBar, append: true));
        Assert.Equal(ArtifactVerificationStatus.Mismatch, One(repeated, Chunk("bar", FooBar, append: true)).Status);
    }

    [Fact]
    public void Append_false_starts_over()
    {
        using var v = new ArtifactVerifier();
        One(v, Chunk("foo", Foo));
        Assert.Equal(ArtifactVerificationStatus.Verified, One(v, Chunk("foo", Foo)).Status);
    }

    [Fact]
    public void Without_a_hash_the_result_is_no_hash_and_the_stream_goes_on()
    {
        using var v = new ArtifactVerifier();
        Assert.Equal(ArtifactVerificationStatus.NoHash, One(v, Chunk("foo", null)).Status);
        // A later chunk with a hash still covers everything from the start.
        Assert.Equal(ArtifactVerificationStatus.Verified, One(v, Chunk("bar", FooBar, append: true)).Status);
    }

    [Theory]
    [InlineData("sha512:abcd")]
    [InlineData("SHA256:2c26b46b68ffc68ff99b453c1d30413413422d706483bfa0f98a5e886266e7ae")]
    [InlineData("sha256:2C26B46B68FFC68FF99B453C1D30413413422D706483BFA0F98A5E886266E7AE")]
    [InlineData("sha256:2c26b46b")]
    [InlineData("2c26b46b68ffc68ff99b453c1d30413413422d706483bfa0f98a5e886266e7ae")]
    [InlineData("")]
    public void A_value_that_is_not_sha256_and_64_lowercase_hex_is_unverifiable(string value)
    {
        using var v = new ArtifactVerifier();
        var r = One(v, Chunk("foo", value));
        Assert.Equal(ArtifactVerificationStatus.Unverifiable, r.Status);
        Assert.Equal(value, r.Expected);
    }

    [Fact]
    public void A_value_that_is_not_a_string_is_unverifiable()
    {
        using var v = new ArtifactVerifier();
        var evt = StreamResponse.Of(new TaskArtifactUpdateEvent
        {
            TaskId = "t", ContextId = "c",
            Artifact = new Artifact { ArtifactId = "a", Parts = [Part.FromText("foo")], Metadata = new() { ["sha256"] = JsonSerializer.SerializeToElement(42) } },
        });
        var r = One(v, evt);
        Assert.Equal(ArtifactVerificationStatus.Unverifiable, r.Status);
        Assert.Null(r.Expected);
    }

    [Fact]
    public void Content_that_cannot_be_hashed_is_unverifiable_and_never_throws()
    {
        using var v = new ArtifactVerifier();
        using var doc = JsonDocument.Parse("""{"n":1e999}""");
        var evt = StreamResponse.Of(new TaskArtifactUpdateEvent
        {
            TaskId = "t", ContextId = "c",
            Artifact = new Artifact { ArtifactId = "a", Parts = [Part.FromData(doc.RootElement.Clone())], Metadata = Hash(Foo) },
        });
        var r = One(v, evt);
        Assert.Equal(ArtifactVerificationStatus.Unverifiable, r.Status);
        Assert.Null(r.Actual);
        Assert.Equal(ArtifactVerificationStatus.Unverifiable, ArtifactVerifier.Verify(new Artifact { ArtifactId = "a", Parts = [Part.FromData(doc.RootElement.Clone())], Metadata = Hash(Foo) }).Status);
    }

    [Fact]
    public void Artifacts_are_tracked_by_id()
    {
        using var v = new ArtifactVerifier();
        One(v, Chunk("foo", Foo, id: "a"));
        Assert.Equal(ArtifactVerificationStatus.Verified, One(v, Chunk("foo", Foo, id: "b")).Status);
        Assert.Equal(ArtifactVerificationStatus.Verified, One(v, Chunk("bar", FooBar, id: "a", append: true)).Status);
    }

    [Fact]
    public void Other_events_return_nothing()
    {
        using var v = new ArtifactVerifier();
        var status = StreamResponse.Of(new TaskStatusUpdateEvent { TaskId = "t", ContextId = "c", Status = new AgentTaskStatus { State = TaskState.Working } });
        Assert.Empty(v.Observe(status));
        Assert.Empty(v.Observe(StreamResponse.Of(Message.FromAgent("hi", "c", "t"))));
        Assert.Empty(v.Observe(StreamResponse.Of(new AgentTask { Id = "t", ContextId = "c", Status = new AgentTaskStatus { State = TaskState.Working } })));
    }

    [Fact]
    public void A_snapshot_resets_state_and_returns_one_result_per_artifact()
    {
        using var v = new ArtifactVerifier();
        One(v, Chunk("foo", Foo, id: "a"));
        var snapshot = StreamResponse.Of(new AgentTask
        {
            Id = "t", ContextId = "c", Status = new AgentTaskStatus { State = TaskState.Working },
            Artifacts =
            [
                new Artifact { ArtifactId = "a", Parts = [Part.FromText("foo"), Part.FromText("bar")], Metadata = Hash(FooBar) },
                new Artifact { ArtifactId = "b", Parts = [Part.FromText("foo")] },
                new Artifact { ArtifactId = "c", Parts = [Part.FromText("foo")], Metadata = Hash(FooBar) },
            ],
        });
        var results = v.Observe(snapshot);
        Assert.Equal(["a", "b", "c"], results.Select(r => r.ArtifactId));
        Assert.Equal([ArtifactVerificationStatus.Verified, ArtifactVerificationStatus.NoHash, ArtifactVerificationStatus.Mismatch], results.Select(r => r.Status));

        // Open artifacts continue from the snapshot: the next chunk is checked against the whole content.
        var more = ArtifactHash.Compute([Part.FromText("foo"), Part.FromText("bar"), Part.FromText("!")]);
        Assert.Equal(ArtifactVerificationStatus.Verified, One(v, Chunk("!", more, id: "a", append: true)).Status);
    }

    [Fact]
    public void A_snapshot_forgets_what_came_before()
    {
        using var v = new ArtifactVerifier();
        One(v, Chunk("foo", Foo));
        v.Observe(StreamResponse.Of(new AgentTask { Id = "t", ContextId = "c", Status = new AgentTaskStatus { State = TaskState.Working } }));
        // "a" is unknown again: an append starts the hash at this chunk.
        Assert.Equal(ArtifactVerificationStatus.Mismatch, One(v, Chunk("bar", FooBar, append: true)).Status);
    }

    [Fact]
    public void Verify_checks_a_whole_artifact()
    {
        Assert.Equal(ArtifactVerificationStatus.Verified,
            ArtifactVerifier.Verify(new Artifact { ArtifactId = "a", Parts = [Part.FromText("foo"), Part.FromText("bar")], Metadata = Hash(FooBar) }).Status);
        var bad = ArtifactVerifier.Verify(new Artifact { ArtifactId = "a", Parts = [Part.FromText("foo")], Metadata = Hash(FooBar) });
        Assert.Equal(ArtifactVerificationStatus.Mismatch, bad.Status);
        Assert.Equal(Foo, bad.Actual);
        Assert.Equal(ArtifactVerificationStatus.NoHash, ArtifactVerifier.Verify(new Artifact { ArtifactId = "a", Parts = [Part.FromText("foo")] }).Status);
    }
}
