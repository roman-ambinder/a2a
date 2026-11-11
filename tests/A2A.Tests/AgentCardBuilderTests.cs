namespace A2A.Tests;

public class AgentCardBuilderTests
{
    [Fact]
    public void Create_sets_name_description_and_default_version()
    {
        var card = AgentCard.Create("Weather", "Forecasts");
        Assert.Equal(("Weather", "Forecasts", "1.0.0"), (card.Name, card.Description, card.Version));
        Assert.Empty(card.SupportedInterfaces);
        Assert.Empty(card.Skills);
    }

    [Theory]
    [InlineData("", "d", "1")]
    [InlineData("n", " ", "1")]
    [InlineData("n", "d", "")]
    public void Create_refuses_empty_values(string name, string description, string version) =>
        Assert.Throws<ArgumentException>(() => AgentCard.Create(name, description, version));

    [Fact]
    public void AddSkill_chains_and_refuses_duplicate_ids()
    {
        var card = AgentCard.Create("n", "d")
            .AddSkill("a", "A", "Does a", tags: ["x"], examples: ["try a"])
            .AddSkill(new AgentSkill { Id = "b", Name = "B", Description = "Does b" });
        Assert.Equal(["a", "b"], card.Skills.Select(s => s.Id));
        Assert.Equal(["x"], card.Skills[0].Tags);
        Assert.Equal(["try a"], card.Skills[0].Examples!);
        Assert.Empty(card.Skills[1].Tags);
        Assert.Throws<ArgumentException>(() => card.AddSkill("a", "Again", "Duplicate"));
    }

    [Fact]
    public void Card_round_trips_through_json()
    {
        var card = AgentCard.Create("n", "d", "2.0").AddSkill("a", "A", "Does a");
        var copy = A2A.Json.A2AJson.Deserialize<AgentCard>(A2A.Json.A2AJson.Serialize(card));
        Assert.Equal("2.0", copy.Version);
        Assert.Equal("a", Assert.Single(copy.Skills).Id);
    }
}
