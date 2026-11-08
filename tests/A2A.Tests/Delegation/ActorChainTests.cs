using System.Text.Json;
using A2A.Delegation;
using A2A.Json;

namespace A2A.Tests;

public class ActorChainTests
{
    private static JsonElement J(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static Message WithChain(string json) => new()
    {
        MessageId = "m1",
        Role = Role.User,
        Parts = [Part.FromText("hi")],
        Metadata = new() { [ActorChain.MetadataKey] = J(json) },
    };

    private const string IssueShape = """
        {
          "origin": { "sub": "user:alice@example.com", "iss": "https://idp.example.com" },
          "actors": [
            { "sub": "agent:coordinator", "scopes": ["repo:read", "ci:trigger"] },
            { "sub": "agent:builder" }
          ]
        }
        """;

    [Fact]
    public void Round_trips_the_issue_2028_shape()
    {
        var chain = WithChain(IssueShape).GetActorChain();

        Assert.NotNull(chain);
        Assert.Equal("user:alice@example.com", chain.Origin!.Sub);
        Assert.Equal(2, chain.Actors.Count);
        Assert.Equal(["repo:read", "ci:trigger"], chain.Actors[0].Scopes);
    }

    [Fact]
    public void AppendHop_keeps_earlier_hops_and_does_not_change_the_source()
    {
        var source = ActorChain.Start(new ActorChainEntry { Sub = "user:alice" })
            .AppendHop(new ActorChainEntry { Sub = "agent:a" });
        Assert.Single(source.Actors);

        var result = source.AppendHop(new ActorChainEntry { Sub = "agent:b" });

        Assert.Equal(2, result.Actors.Count);
        Assert.Single(source.Actors);
        Assert.Same(source.Actors[0], result.Actors[0]);
        Assert.Same(source.Origin, result.Origin);
    }

    [Fact]
    public void Unknown_hop_fields_survive_append_and_set()
    {
        var parsed = WithChain("""
            { "actors": [ { "sub": "agent:a", "proof_ref": "sha256:abc", "session": "s1" } ] }
            """).GetActorChain()!;

        var next = parsed.AppendHop(new ActorChainEntry { Sub = "agent:b" });
        var message = new Message { MessageId = "m2", Role = Role.User, Parts = [Part.FromText("x")] }.SetActorChain(next);

        using var doc = JsonDocument.Parse(A2AJson.Serialize(message));
        var hop = doc.RootElement.GetProperty("metadata").GetProperty("actorChain").GetProperty("actors")[0];
        Assert.Equal("sha256:abc", hop.GetProperty("proof_ref").GetString());
        Assert.Equal("s1", hop.GetProperty("session").GetString());
        Assert.Equal("agent:a", hop.GetProperty("sub").GetString());
    }

    [Fact]
    public void Wire_names_are_exact()
    {
        var chain = ActorChain.Start(new ActorChainEntry { Sub = "u", Iss = "i", Scopes = ["s"] })
            .AppendHop(new ActorChainEntry { Sub = "a" });
        var json = A2AJson.SerializeToElement(chain);

        Assert.True(json.TryGetProperty("origin", out var origin));
        Assert.Equal("u", origin.GetProperty("sub").GetString());
        Assert.Equal("i", origin.GetProperty("iss").GetString());
        Assert.Equal("s", origin.GetProperty("scopes")[0].GetString());
        Assert.Equal(1, json.GetProperty("actors").GetArrayLength());
    }

    [Fact]
    public void SetActorChain_creates_metadata_and_returns_the_same_message()
    {
        var message = new Message { MessageId = "m", Role = Role.User, Parts = [Part.FromText("x")] };
        Assert.Null(message.Metadata);

        var returned = message.SetActorChain(ActorChain.Start(new ActorChainEntry { Sub = "u" }));

        Assert.Same(message, returned);
        Assert.True(message.Metadata!.ContainsKey("actorChain"));
        Assert.Equal("u", message.GetActorChain()!.Origin!.Sub);
    }

    [Fact]
    public void GetActorChain_reads_task_metadata()
    {
        var task = new AgentTask
        {
            Id = "t",
            ContextId = "c",
            Status = new AgentTaskStatus { State = TaskState.Working },
            Metadata = new() { ["actorChain"] = J(IssueShape) },
        };

        Assert.Equal(2, task.GetActorChain()!.Actors.Count);
    }

    [Theory]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("{\"actors\":\"x\"}")]
    [InlineData("{\"actors\":[1]}")]
    [InlineData("{\"actors\":[{\"iss\":\"x\"}]}")]
    [InlineData("{\"actors\":[{\"sub\":1}]}")]
    [InlineData("{\"origin\":\"x\",\"actors\":[]}")]
    [InlineData("{\"origin\":{\"iss\":\"x\"},\"actors\":[]}")]
    public void GetActorChain_returns_null_for_other_shapes(string json)
    {
        Assert.Null(WithChain(json).GetActorChain());
        Assert.False(ActorChain.TryReadActorChain(new() { ["actorChain"] = J(json) }, out _));
    }

    [Fact]
    public void GetActorChain_returns_null_without_metadata()
    {
        var message = new Message { MessageId = "m", Role = Role.User, Parts = [Part.FromText("x")] };
        Assert.Null(message.GetActorChain());
        Assert.False(ActorChain.TryReadActorChain(null, out var chain));
        Assert.Null(chain);
    }

    [Fact]
    public void Chain_without_origin_and_with_no_hops_is_well_formed()
    {
        Assert.True(ActorChain.TryReadActorChain(new() { ["actorChain"] = J("{\"actors\":[]}") }, out var chain));
        Assert.Null(chain!.Origin);
        Assert.Empty(chain.Actors);
    }

    [Fact]
    public void CreateExtension_is_optional_and_names_the_uri()
    {
        var ext = ActorChain.CreateExtension();

        Assert.Equal(ActorChain.ExtensionUri, ext.Uri);
        Assert.False(ext.Required);
        Assert.False(string.IsNullOrWhiteSpace(ext.Description));
    }
}
