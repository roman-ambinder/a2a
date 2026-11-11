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

/// <summary>
///     Every settable limit and streaming option has a configuration key, and each key sets its own property. A new option
///     without a key fails here, so the key tables cannot fall behind the options.
/// </summary>
public class ConfigurationKeyCoverageTests
{
    private static IConfiguration Config(IEnumerable<KeyValuePair<string, string?>> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    /// <summary>A value of the property's type that differs from its default, as configuration text and as the value.</summary>
    private static (string Text, object Value) Sample(Type type, int seed) =>
        (Nullable.GetUnderlyingType(type) ?? type) switch
        {
            var t when t == typeof(int) => ((7000 + seed).ToString(System.Globalization.CultureInfo.InvariantCulture), 7000 + seed),
            var t when t == typeof(long) => ((9_000_000L + seed).ToString(System.Globalization.CultureInfo.InvariantCulture), 9_000_000L + seed),
            var t when t == typeof(decimal) => ($"{seed}.5", decimal.Parse($"{seed}.5", System.Globalization.CultureInfo.InvariantCulture)),
            var t when t == typeof(TimeSpan) => ($"00:{seed % 60:00}:{(seed * 7) % 60:00}", new TimeSpan(0, seed % 60, (seed * 7) % 60)),
            var t => throw new InvalidOperationException($"No sample for {t}: add one, and a key in A2AConfigurationExtensions.")
        };

    private static void EveryPropertyIsRead<T>(string section, Func<A2AServerOptions, T> target)
    {
        var properties = typeof(T).GetProperties().Where(p => p.CanWrite && p.GetSetMethod() is not null).ToList();
        Assert.NotEmpty(properties);
        var expected = properties.Select((p, i) => (Property: p, Sample: Sample(p.PropertyType, i + 1))).ToList();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddA2A(Config(expected.Select(e =>
                new KeyValuePair<string, string?>($"{section}:{e.Property.Name}", e.Sample.Text))),
            a => a.AddAgentExecutor<ScenarioAgent>());
        using var provider = services.BuildServiceProvider();
        var options = target(provider.GetRequiredService<IOptions<A2AServerOptions>>().Value);
        foreach (var (property, sample) in expected)
            Assert.True(Equals(sample.Value, property.GetValue(options)), $"{section}:{property.Name} was not applied.");
    }

    [Fact]
    public void Every_limit_has_a_key() => EveryPropertyIsRead("Limits", o => o.Limits);

    [Fact]
    public void Every_streaming_option_has_a_key() => EveryPropertyIsRead("Streaming", o => o.Streaming);

    [Fact]
    public void Server_card_api_key_and_rate_limit_keys_are_read()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddA2A(Config(new Dictionary<string, string?>
        {
            ["LongPollTimeout"] = "00:00:12", ["CancelGracePeriod"] = "00:00:03", ["IdempotentCancel"] = "true",
            ["ShutdownDrainTimeout"] = "00:00:09", ["MaxTurnsPerTask"] = "4", ["MaxTaskDuration"] = "",
            ["Card:Name"] = "n", ["Card:Description"] = "d", ["Card:Version"] = "2",
            ["Card:DocumentationUrl"] = "https://docs.example.com", ["Card:IconUrl"] = "https://example.com/i.png",
            ["Card:Provider:Organization"] = "Org", ["Card:Provider:Url"] = "https://example.com",
            ["Card:Capabilities:PushNotifications"] = "true", ["Card:DefaultOutputModes:0"] = "text/plain",
            ["Card:Skills:0:Id"] = "s", ["Card:Skills:0:Name"] = "S", ["Card:Skills:0:Description"] = "Does s",
            ["Card:Skills:0:Examples:0"] = "try s", ["Card:Skills:0:InputModes:0"] = "text/plain",
            ["Card:Skills:0:OutputModes:0"] = "application/json",
            ["ApiKeys:HeaderName"] = "X-Key", ["ApiKeys:CardSchemeName"] = "key",
            ["ApiKeys:Keys:0:CallerId"] = "c", ["ApiKeys:Keys:0:Hash"] = A2AApiKey.Create().Hash,
            ["ApiKeys:Keys:0:Tenants:0"] = "acme",
            ["RateLimit:ReplenishmentPeriod"] = "00:00:30"
        }), a => a.AddAgentExecutor<ScenarioAgent>());
        using var provider = services.BuildServiceProvider();

        var o = provider.GetRequiredService<IOptions<A2AServerOptions>>().Value;
        Assert.Equal(TimeSpan.FromSeconds(12), o.LongPollTimeout);
        Assert.Equal(TimeSpan.FromSeconds(3), o.CancelGracePeriod);
        Assert.True(o.IdempotentCancel);
        Assert.Equal(TimeSpan.FromSeconds(9), o.ShutdownDrainTimeout);
        Assert.Equal(4, o.MaxTurnsPerTask);
        Assert.Null(o.MaxTaskDuration);
        Assert.Equal("https://docs.example.com", o.AgentCard.DocumentationUrl);
        Assert.Equal("https://example.com/i.png", o.AgentCard.IconUrl);
        Assert.Equal("https://example.com", o.AgentCard.Provider!.Url);
        Assert.True(o.AgentCard.Capabilities.PushNotifications);
        Assert.Equal(["text/plain"], o.AgentCard.DefaultOutputModes);
        var skill = Assert.Single(o.AgentCard.Skills);
        Assert.Equal(("Does s", "try s", "text/plain", "application/json"),
            (skill.Description, skill.Examples!.Single(), skill.InputModes!.Single(), skill.OutputModes!.Single()));

        var keys = provider.GetRequiredService<IOptions<A2AApiKeyOptions>>().Value;
        Assert.Equal(("X-Key", "key"), (keys.HeaderName, keys.CardSchemeName));
        Assert.Equal(["acme"], Assert.Single(keys.Keys).Tenants!);
        Assert.Equal(TimeSpan.FromSeconds(30),
            provider.GetRequiredService<IOptions<A2ARateLimitOptions>>().Value.ReplenishmentPeriod);
    }

    [Theory]
    [InlineData("Limits:MaxPartsPerMessage", "many", "whole number")]
    [InlineData("IdempotentCancel", "maybe", "true or false")]
    [InlineData("FollowUpPolicy", "Sometimes", "InterruptedOnly")]
    [InlineData("Limits:MaxTaskCostUnits", "lots", "a number")]
    [InlineData("PublicBaseUrl", "not a url", "absolute URL")]
    public void Bad_values_name_the_expected_form(string key, string value, string expected)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddA2A(Config(new Dictionary<string, string?> { [key] = value }), a => a.AddAgentExecutor<ScenarioAgent>());
        using var provider = services.BuildServiceProvider();
        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            _ = provider.GetRequiredService<IOptions<A2AServerOptions>>().Value;
            _ = provider.GetRequiredService<IOptions<A2AHttpOptions>>().Value;
        });
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_caller_id_fails_at_startup()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddA2A(a => a.AddAgentExecutor<ScenarioAgent>()
            .AddApiKeyAuthentication(o => o.Add(" ", A2AApiKey.Create().Hash)));
        var type = services.Select(d => d.ImplementationType).Single(t => t?.Name == "ApiKeyStartupCheck")!;
        using var provider = services.BuildServiceProvider();
        var check = (Microsoft.Extensions.Hosting.IHostedService)ActivatorUtilities.CreateInstance(provider, type);
        var ex = Assert.Throws<OptionsValidationException>(() => check.StartAsync(CancellationToken.None).GetAwaiter().GetResult());
        Assert.Contains("no caller id", ex.Message, StringComparison.Ordinal);
    }
}
