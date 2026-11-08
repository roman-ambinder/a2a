using System.Diagnostics;
using System.Globalization;
using A2A.Client.Peers;
using Xunit;

namespace A2A.Tests;

public class AgentNameNormalizerTests
{
    private const int Max = 256;

    [Theory]
    [InlineData("Agent", "AGENT")]
    [InlineData("Agent", "Agent​")] // zero-width space
    [InlineData("Agent", "‮Agent")] // right-to-left override
    [InlineData("Agent", "Ａgent")] // fullwidth A
    [InlineData("Travel Agent", "  travel \t agent\n")]
    [InlineData("ﬁle", "file")] // fi ligature
    public void NameKey_same(string a, string b) =>
        Assert.Equal(AgentNameNormalizer.NameKey(a, Max), AgentNameNormalizer.NameKey(b, Max));

    [Theory]
    [InlineData("Agent", "Аgent")] // Cyrillic A
    [InlineData("Café", "Cafe")] // e acute
    [InlineData("Agent 1", "Agent 2")]
    public void NameKey_different(string a, string b) =>
        Assert.NotEqual(AgentNameNormalizer.NameKey(a, Max), AgentNameNormalizer.NameKey(b, Max));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("​​")] // zero-width spaces
    [InlineData("  \t")]
    public void NameKey_empty(string? name) => Assert.Equal("", AgentNameNormalizer.NameKey(name, Max));

    [Theory]
    [InlineData("Paypal", "Pаypal")] // Cyrillic a
    [InlineData("paypal", "paypa1")]
    [InlineData("PAYPAL", "PAYPAI")]
    [InlineData("pay-pal", "pay pal")]
    [InlineData("pay_pal", "PayPal")]
    [InlineData("Cafe", "Café")] // e acute
    [InlineData("modern", "modem")]
    [InlineData("wide", "vvide")]
    [InlineData("Agent O", "Agent 0")]
    [InlineData("Scope", "Ѕсоре")] // Cyrillic S c o p e
    public void Skeleton_same(string a, string b)
    {
        var skeleton = AgentNameNormalizer.Skeleton(a, Max);
        Assert.NotEqual("", skeleton);
        Assert.Equal(skeleton, AgentNameNormalizer.Skeleton(b, Max));
    }

    [Theory]
    [InlineData("Agent 1", "Agent 2")]
    [InlineData("Payments", "Pay")]
    [InlineData("Scheduler", "Planner")]
    public void Skeleton_different(string a, string b) =>
        Assert.NotEqual(AgentNameNormalizer.Skeleton(a, Max), AgentNameNormalizer.Skeleton(b, Max));

    [Theory]
    [InlineData("---")]
    [InlineData("\U0001F916")] // robot emoji
    public void Skeleton_empty(string name) => Assert.Equal("", AgentNameNormalizer.Skeleton(name, Max));

    [Theory]
    [InlineData("Pаypal")] // Latin + Cyrillic a
    [InlineData("Αgent")] // Greek Alpha + Latin
    public void IsMixedScript_true(string name) => Assert.True(AgentNameNormalizer.IsMixedScript(name, Max));

    [Theory]
    [InlineData("Paypal")]
    [InlineData("Агент")] // Cyrillic word
    [InlineData("Travel Агент")] // Latin token, Cyrillic token
    [InlineData("AI翻訳")] // Latin + CJK
    [InlineData("Agent 1")]
    [InlineData("")]
    public void IsMixedScript_false(string name) => Assert.False(AgentNameNormalizer.IsMixedScript(name, Max));

    [Theory]
    [InlineData("a\uD800b")]
    [InlineData("\uDC00")]
    public void Lone_surrogate_does_not_throw(string name)
    {
        _ = AgentNameNormalizer.NameKey(name, Max);
        _ = AgentNameNormalizer.Skeleton(name, Max);
        _ = AgentNameNormalizer.IsMixedScript(name, Max);
    }

    [Fact]
    public void Long_name_is_capped()
    {
        var sw = Stopwatch.StartNew();
        var key = AgentNameNormalizer.NameKey(new string('a', 1_000_000), Max);
        Assert.True(key.Length <= 256);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Turkish_culture_does_not_change_keys()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            Assert.Equal("title", AgentNameNormalizer.NameKey("TITLE", Max));
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
        }
    }
}
