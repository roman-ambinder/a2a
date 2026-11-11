using A2A.Server;
using A2A.Server.AspNetCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace A2A.Tests;

public class ConfigurationBindingTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>("A2A:" + v.Key, v.Value)))
            .Build().GetSection("A2A");

    private static ServiceProvider Provider(IConfiguration config, Action<A2AServerBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddA2A(config, a =>
        {
            a.AddAgentExecutor<ScenarioAgent>();
            configure?.Invoke(a);
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Reads_card_limits_streaming_http_and_rate_limit()
    {
        using var provider = Provider(Config(
            ("Card:Name", "Weather"), ("Card:Description", "Forecasts"), ("Card:Version", "2.0.0"),
            ("Card:Provider:Organization", "Example"), ("Card:Capabilities:Streaming", "true"),
            ("Card:DefaultInputModes:0", "text/plain"),
            ("Card:Skills:0:Id", "forecast"), ("Card:Skills:0:Name", "Forecast"), ("Card:Skills:0:Tags:0", "weather"),
            ("Limits:MaxRequestBodyBytes", "1048576"), ("Limits:MaxStreamDuration", "00:10:00"),
            ("Limits:MaxTaskTokens", "5000"), ("Streaming:ReplayBufferSize", "50"),
            ("MaxTaskDuration", "01:00:00"), ("InterruptedTimeout", ""), ("followuppolicy", "allowsteering"),
            ("PublicBaseUrl", "https://agent.example.com"), ("AgentCardMaxAge", "00:01:00"),
            ("RateLimit:TokenLimit", "5"), ("RateLimit:TokensPerPeriod", "10")));

        var o = provider.GetRequiredService<IOptions<A2AServerOptions>>().Value;
        Assert.Equal("Weather", o.AgentCard.Name);
        Assert.Equal("2.0.0", o.AgentCard.Version);
        Assert.Equal("Example", o.AgentCard.Provider!.Organization);
        Assert.True(o.AgentCard.Capabilities.Streaming);
        Assert.Equal(["text/plain"], o.AgentCard.DefaultInputModes);
        var skill = Assert.Single(o.AgentCard.Skills);
        Assert.Equal(("forecast", "Forecast"), (skill.Id, skill.Name));
        Assert.Equal(["weather"], skill.Tags);
        Assert.Equal(1048576, o.Limits.MaxRequestBodyBytes);
        Assert.Equal(TimeSpan.FromMinutes(10), o.Limits.MaxStreamDuration);
        Assert.Equal(5000, o.Limits.MaxTaskTokens);
        Assert.Equal(100, o.Limits.MaxPartsPerMessage); // untouched default
        Assert.Equal(50, o.Streaming.ReplayBufferSize);
        Assert.Equal(TimeSpan.FromHours(1), o.MaxTaskDuration);
        Assert.Null(o.InterruptedTimeout);
        Assert.Equal(FollowUpPolicy.AllowSteering, o.FollowUpPolicy);

        var http = provider.GetRequiredService<IOptions<A2AHttpOptions>>().Value;
        Assert.Equal(new Uri("https://agent.example.com"), http.PublicBaseUrl);
        Assert.Equal(TimeSpan.FromMinutes(1), http.AgentCardMaxAge);

        var rate = provider.GetRequiredService<IOptions<A2ARateLimitOptions>>().Value;
        Assert.Equal((5, 10), (rate.TokenLimit, rate.TokensPerPeriod));
        Assert.True(rate.Enabled);
    }

    [Fact]
    public void Code_after_configuration_wins()
    {
        using var provider = Provider(Config(("Card:Name", "From config"), ("Limits:MaxJsonDepth", "10")), a => a
            .AddAgentCard(new AgentCard { Name = "From code", Description = "d", Version = "1" })
            .Services.Configure<A2AServerOptions>(o => o.Limits.MaxJsonDepth = 20));
        var o = provider.GetRequiredService<IOptions<A2AServerOptions>>().Value;
        Assert.Equal("From code", o.AgentCard.Name);
        Assert.Equal(20, o.Limits.MaxJsonDepth);
    }

    [Theory]
    [InlineData("AllowAnonymous", "true", "A2A:AllowAnonymous")]
    [InlineData("TreatMissingVersionAsCurrent", "true", "A2A:TreatMissingVersionAsCurrent")]
    [InlineData("IncludeExceptionDetails", "true", "A2A:IncludeExceptionDetails")]
    public void Settings_that_weaken_defaults_are_refused(string key, string value, string path)
    {
        using var provider = Provider(Config((key, value)));
        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IOptions<A2AServerOptions>>().Value);
        Assert.Contains(path, ex.Message, StringComparison.Ordinal);
        Assert.Contains("Set it in code", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Turning_rate_limiting_off_is_refused()
    {
        using var provider = Provider(Config(("RateLimit:Enabled", "false")));
        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IOptions<A2ARateLimitOptions>>().Value);
        Assert.Contains("A2A:RateLimit:Enabled", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_key_is_refused_with_its_path()
    {
        using var provider = Provider(Config(("Limits:MaxJsonDept", "10")));
        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IOptions<A2AServerOptions>>().Value);
        Assert.Contains("A2A:Limits:MaxJsonDept", ex.Message, StringComparison.Ordinal);
        Assert.Contains("MaxJsonDepth", ex.Message, StringComparison.Ordinal); // lists the known keys
    }

    [Fact]
    public void Bad_value_names_the_key_and_the_expected_form()
    {
        using var provider = Provider(Config(("Limits:MaxStreamDuration", "ten minutes")));
        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IOptions<A2AServerOptions>>().Value);
        Assert.Contains("A2A:Limits:MaxStreamDuration", ex.Message, StringComparison.Ordinal);
        Assert.Contains("duration", ex.Message, StringComparison.Ordinal);
    }
}
