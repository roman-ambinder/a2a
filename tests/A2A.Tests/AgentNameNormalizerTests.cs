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
    [InlineData("Agent", "Agent\u200B")] // zero-width space
    [InlineData("Agent", "\u202EAgent")] // right-to-left override
    [InlineData("Agent", "\uFF21gent")] // fullwidth A
    [InlineData("Travel Agent", "  travel \t agent\n")]
    [InlineData("\uFB01le", "file")] // fi ligature
    public void NameKey_same(string a, string b) =>
        Assert.Equal(AgentNameNormalizer.NameKey(a, Max), AgentNameNormalizer.NameKey(b, Max));

    [Theory]
    [InlineData("Agent", "\u0410gent")] // Cyrillic A
    [InlineData("Caf\u00E9", "Cafe")] // e acute
    [InlineData("Agent 1", "Agent 2")]
    public void NameKey_different(string a, string b) =>
        Assert.NotEqual(AgentNameNormalizer.NameKey(a, Max), AgentNameNormalizer.NameKey(b, Max));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\u200B\u200B")] // zero-width spaces
    [InlineData("  \t")]
    public void NameKey_empty(string? name) => Assert.Equal("", AgentNameNormalizer.NameKey(name, Max));

    [Theory]
    [InlineData("Paypal", "P\u0430ypal")] // Cyrillic a
    [InlineData("paypal", "paypa1")]
    [InlineData("PAYPAL", "PAYPAI")]
    [InlineData("pay-pal", "pay pal")]
    [InlineData("pay_pal", "PayPal")]
    [InlineData("Cafe", "Caf\u00E9")] // e acute
    [InlineData("modern", "modem")]
    [InlineData("wide", "vvide")]
    [InlineData("Agent O", "Agent 0")]
    [InlineData("Scope", "\u0405\u0441\u043E\u0440\u0435")] // Cyrillic S c o p e
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
    [InlineData("P\u0430ypal")] // Latin + Cyrillic a
    [InlineData("\u0391gent")] // Greek Alpha + Latin
    [InlineData("P\u0301\u0430\u0301ypal")] // marks at each script boundary must not hide the mix
    public void IsMixedScript_true(string name) => Assert.True(AgentNameNormalizer.IsMixedScript(name, Max));

    [Theory]
    [InlineData("Paypal")]
    [InlineData("\u0410\u0433\u0435\u043D\u0442")] // Cyrillic word
    [InlineData("Travel \u0410\u0433\u0435\u043D\u0442")] // Latin token, Cyrillic token
    [InlineData("AI\u7FFB\u8A33")] // Latin + CJK
    [InlineData("Agent 1")]
    [InlineData("")]
    [InlineData("Cafe\u0301")] // e + combining acute
    [InlineData("Caf\u00E9")] // precomposed e acute
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
