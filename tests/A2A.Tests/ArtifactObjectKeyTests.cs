using A2A.Server.Storage;

namespace A2A.Tests;

public sealed class ArtifactObjectKeyTests
{
    public static TheoryData<string, string> Ids => new()
    {
        { "t", "a/b" }, { "t", ".." }, { "t", "a%2Fb" }, { "t", "" },
        { "a/b", "task" }, { "..", "task" }, { "a%2Fb", "task" }, { "", "task" },
        { ".", "." }, { "a b", "ü" },
    };

    [Theory]
    [MemberData(nameof(Ids))]
    public void Create_then_TryParse_round_trips_and_no_segment_is_dot_dot(string tenant, string taskId)
    {
        var objectId = ArtifactObjectKey.NewObjectId();

        var key = ArtifactObjectKey.Create(tenant, taskId, objectId);

        Assert.True(ArtifactObjectKey.TryParse(key, out var t, out var id, out var oid));
        Assert.Equal(tenant, t);
        Assert.Equal(taskId, id);
        Assert.Equal(objectId, oid);
        var segments = key.Split('/');
        Assert.Equal(3, segments.Length);
        Assert.DoesNotContain("..", segments);
        Assert.DoesNotContain(".", segments);
    }

    [Fact]
    public void Task_prefix_of_one_task_is_not_a_prefix_of_another_tasks_key()
    {
        var prefix = ArtifactObjectKey.TaskPrefix("t", "a");
        var other = ArtifactObjectKey.Create("t", "ab", ArtifactObjectKey.NewObjectId());
        var own = ArtifactObjectKey.Create("t", "a", ArtifactObjectKey.NewObjectId());

        Assert.EndsWith("/", prefix);
        Assert.False(other.StartsWith(prefix, StringComparison.Ordinal));
        Assert.StartsWith(prefix, own, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_tenant_is_underscore_and_a_literal_underscore_tenant_is_rejected()
    {
        var oid = ArtifactObjectKey.NewObjectId();

        Assert.StartsWith("_/", ArtifactObjectKey.Create("", "x", oid), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => ArtifactObjectKey.Create("_", "x", oid));
        Assert.Throws<ArgumentException>(() => ArtifactObjectKey.TaskPrefix("_", "x"));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("0123456789abcdef0123456789abcdeg")]
    [InlineData("0123456789abcdef0123456789abcdef0")]
    public void IsValidObjectId_rejects_bad_ids(string id)
    {
        Assert.False(ArtifactObjectKey.IsValidObjectId(id));
    }

    [Fact]
    public void NewObjectId_is_32_lowercase_hex_and_random()
    {
        var a = ArtifactObjectKey.NewObjectId();
        var b = ArtifactObjectKey.NewObjectId();

        Assert.True(ArtifactObjectKey.IsValidObjectId(a));
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("a/b/c/d")]
    [InlineData("a/b/not-an-id")]
    [InlineData("../b/0123456789abcdef0123456789abcdef")]
    [InlineData("a/%2e%2e/0123456789abcdef0123456789abcdef")]
    [InlineData("a/../0123456789abcdef0123456789abcdef")]
    public void TryParse_rejects_malformed_keys(string key)
    {
        Assert.False(ArtifactObjectKey.TryParse(key, out _, out _, out _));
    }

    [Fact]
    public void Create_rejects_an_invalid_object_id()
    {
        Assert.Throws<ArgumentException>(() => ArtifactObjectKey.Create("t", "x", "../x"));
    }
}
