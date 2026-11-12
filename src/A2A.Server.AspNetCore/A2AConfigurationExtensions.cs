using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace A2A.Server.AspNetCore;

/// <summary>
///     Reads the A2A settings from configuration (<c>appsettings.json</c>, environment variables, ...), for example the
///     <c>"A2A"</c> section.
/// </summary>
/// <remarks>
///     <para>
///         Keys (case-insensitive): <c>Card</c> (<c>Name</c>, <c>Description</c>, <c>Version</c>, <c>DocumentationUrl</c>,
///         <c>IconUrl</c>, <c>Provider</c>, <c>Capabilities</c>, <c>DefaultInputModes</c>, <c>DefaultOutputModes</c>,
///         <c>Skills</c>), <c>PublicBaseUrl</c>, <c>AgentCardMaxAge</c>, <c>Limits</c> (every <see cref="A2ALimits" />
///         property), <c>Streaming</c> (every <see cref="StreamingOptions" /> property), <c>Anomalies</c> (every
///         <see cref="A2AAnomalyOptions" /> property; the detector only reports, so turning it on or off here weakens
///         nothing), <c>RateLimit</c>
///         (<c>TokenLimit</c>, <c>TokensPerPeriod</c>, <c>ReplenishmentPeriod</c>), and the server settings
///         <c>LongPollTimeout</c>, <c>CancelGracePeriod</c>, <c>IdempotentCancel</c>, <c>ShutdownDrainTimeout</c>,
///         <c>MaxTaskDuration</c>, <c>InterruptedTimeout</c>, <c>MaxTurnsPerTask</c> and <c>FollowUpPolicy</c>, and
///         <c>ApiKeys</c> (<c>HeaderName</c>, <c>CardSchemeName</c>, <c>Keys</c>: a list of <c>CallerId</c>, <c>Hash</c>,
///         <c>Tenants</c>; read by <see cref="A2AApiKeyAuthenticationExtensions.AddApiKeyAuthentication" /> and reloaded
///         when the configuration changes). Durations
///         use the <see cref="TimeSpan" /> format (<c>"00:00:30"</c>); an empty value sets an optional one to null.
///     </para>
///     <para>
///         Settings that weaken a secure default (<c>AllowAnonymous</c>, <c>TreatMissingVersionAsCurrent</c>,
///         <c>IncludeExceptionDetails</c>, <c>ClientContextIdPolicy</c>, <c>RateLimit:Enabled</c>) are refused here: set
///         them in code, where a reviewer sees them. An unknown key is refused too, so a typo does not go unnoticed. Both
///         throw an <see cref="InvalidOperationException" /> that names the key, when the options are first read (at
///         startup).
///     </para>
///     <para>
///         Configuration is applied before the code that runs after it: <c>AddAgentCard</c> in the builder replaces a card
///         from configuration, and <c>Configure</c> calls override single values.
///     </para>
/// </remarks>
public static class A2AConfigurationExtensions
{
    private static readonly StringComparer Keys = StringComparer.OrdinalIgnoreCase;

    private static readonly Dictionary<string, string> CodeOnly = new(Keys)
    {
        ["AllowAnonymous"] = "it lets anyone call the agent",
        ["TreatMissingVersionAsCurrent"] = "it accepts calls without the A2A-Version header",
        ["IncludeExceptionDetails"] = "it sends exception text to callers",
        ["ClientContextIdPolicy"] = "it lets callers choose context ids"
    };

    private static readonly Dictionary<string, Action<A2ALimits, IConfigurationSection>> LimitKeys = new(Keys)
    {
        [nameof(A2ALimits.MaxRequestBodyBytes)] = (o, s) => o.MaxRequestBodyBytes = Long(s),
        [nameof(A2ALimits.MaxPartsPerMessage)] = (o, s) => o.MaxPartsPerMessage = Int(s),
        [nameof(A2ALimits.MaxActorChainHops)] = (o, s) => o.MaxActorChainHops = Int(s),
        [nameof(A2ALimits.MaxRawPartBytes)] = (o, s) => o.MaxRawPartBytes = Int(s),
        [nameof(A2ALimits.MaxJsonDepth)] = (o, s) => o.MaxJsonDepth = Int(s),
        [nameof(A2ALimits.MaxHistoryLengthReturned)] = (o, s) => o.MaxHistoryLengthReturned = Int(s),
        [nameof(A2ALimits.MaxUserArtifactsPerRequest)] = (o, s) => o.MaxUserArtifactsPerRequest = Int(s),
        [nameof(A2ALimits.LiveInputCapacity)] = (o, s) => o.LiveInputCapacity = Int(s),
        [nameof(A2ALimits.MaxTimelineLengthReturned)] = (o, s) => o.MaxTimelineLengthReturned = Int(s),
        [nameof(A2ALimits.MaxListPageSize)] = (o, s) => o.MaxListPageSize = Int(s),
        [nameof(A2ALimits.MaxConcurrentStreamsPerTask)] = (o, s) => o.MaxConcurrentStreamsPerTask = Int(s),
        [nameof(A2ALimits.MaxConcurrentStreamsPerPrincipal)] = (o, s) => o.MaxConcurrentStreamsPerPrincipal = Int(s),
        [nameof(A2ALimits.MaxEventBytes)] = (o, s) => o.MaxEventBytes = Int(s),
        [nameof(A2ALimits.MaxStreamDuration)] = (o, s) => o.MaxStreamDuration = Duration(s),
        [nameof(A2ALimits.StreamReauthInterval)] = (o, s) => o.StreamReauthInterval = Duration(s),
        [nameof(A2ALimits.StreamIdleTimeout)] = (o, s) => o.StreamIdleTimeout = Duration(s),
        [nameof(A2ALimits.MaxPushConfigsPerTask)] = (o, s) => o.MaxPushConfigsPerTask = Int(s),
        [nameof(A2ALimits.MaxTaskBytes)] = (o, s) => o.MaxTaskBytes = Long(s),
        [nameof(A2ALimits.MaxArtifactsPerTask)] = (o, s) => o.MaxArtifactsPerTask = Int(s),
        [nameof(A2ALimits.MaxConcurrentExecutions)] = (o, s) => o.MaxConcurrentExecutions = Int(s),
        [nameof(A2ALimits.MaxConcurrentExecutionsPerTenant)] = (o, s) => o.MaxConcurrentExecutionsPerTenant = Int(s),
        [nameof(A2ALimits.MaxQueuedExecutions)] = (o, s) => o.MaxQueuedExecutions = Int(s),
        [nameof(A2ALimits.MaxExecutionQueueWait)] = (o, s) => o.MaxExecutionQueueWait = Duration(s),
        [nameof(A2ALimits.MaxTaskTokens)] = (o, s) => o.MaxTaskTokens = Empty(s) ? null : Long(s),
        [nameof(A2ALimits.MaxTaskCostUnits)] = (o, s) => o.MaxTaskCostUnits = Empty(s) ? null : Decimal(s)
    };

    private static readonly Dictionary<string, Action<StreamingOptions, IConfigurationSection>> StreamingKeys =
        new(Keys)
        {
            [nameof(StreamingOptions.ReplayBufferSize)] = (o, s) => o.ReplayBufferSize = Int(s),
            [nameof(StreamingOptions.TerminalRetention)] = (o, s) => o.TerminalRetention = Duration(s),
            [nameof(StreamingOptions.KeepAliveInterval)] = (o, s) => o.KeepAliveInterval = Duration(s),
            [nameof(StreamingOptions.ChannelCapacity)] = (o, s) => o.ChannelCapacity = Int(s)
        };

    private static readonly Dictionary<string, Action<A2AAnomalyOptions, IConfigurationSection>> AnomalyKeys =
        new(Keys)
        {
            [nameof(A2AAnomalyOptions.Enabled)] = (o, s) => o.Enabled = Bool(s),
            [nameof(A2AAnomalyOptions.TaskCreationsPerMinute)] = (o, s) => o.TaskCreationsPerMinute = Int(s),
            [nameof(A2AAnomalyOptions.CancellationsPerMinute)] = (o, s) => o.CancellationsPerMinute = Int(s),
            [nameof(A2AAnomalyOptions.MaxTrackedCallers)] = (o, s) => o.MaxTrackedCallers = Int(s)
        };

    private static readonly Dictionary<string, Action<A2AServerOptions, IConfigurationSection>> ServerKeys = new(Keys)
    {
        ["Card"] = (o, s) => ApplyCard(o.AgentCard, s),
        ["Limits"] = (o, s) => Apply(s, o.Limits, LimitKeys),
        ["Streaming"] = (o, s) => Apply(s, o.Streaming, StreamingKeys),
        ["Anomalies"] = (o, s) => Apply(s, o.Anomalies, AnomalyKeys),
        [nameof(A2AServerOptions.LongPollTimeout)] = (o, s) => o.LongPollTimeout = Duration(s),
        [nameof(A2AServerOptions.CancelGracePeriod)] = (o, s) => o.CancelGracePeriod = Duration(s),
        [nameof(A2AServerOptions.IdempotentCancel)] = (o, s) => o.IdempotentCancel = Bool(s),
        [nameof(A2AServerOptions.ShutdownDrainTimeout)] = (o, s) => o.ShutdownDrainTimeout = Duration(s),
        [nameof(A2AServerOptions.MaxTaskDuration)] = (o, s) => o.MaxTaskDuration = Empty(s) ? null : Duration(s),
        [nameof(A2AServerOptions.InterruptedTimeout)] = (o, s) =>
            o.InterruptedTimeout = Empty(s) ? null : Duration(s),
        [nameof(A2AServerOptions.MaxTurnsPerTask)] = (o, s) => o.MaxTurnsPerTask = Empty(s) ? null : Int(s),
        [nameof(A2AServerOptions.FollowUpPolicy)] = (o, s) => o.FollowUpPolicy = Enum<FollowUpPolicy>(s),
        // Read by the HTTP and rate limit options below; listed so they are not reported as unknown.
        [nameof(A2AHttpOptions.PublicBaseUrl)] = (_, _) => { },
        [nameof(A2AHttpOptions.AgentCardMaxAge)] = (_, _) => { },
        ["RateLimit"] = (_, _) => { },
        ["ApiKeys"] = (_, _) => { }
    };

    private static readonly Dictionary<string, Action<A2AApiKeyOptions, IConfigurationSection>> ApiKeyKeys =
        new(Keys)
        {
            [nameof(A2AApiKeyOptions.HeaderName)] = (o, s) => o.HeaderName = s.Value ?? string.Empty,
            [nameof(A2AApiKeyOptions.CardSchemeName)] = (o, s) => o.CardSchemeName = s.Value ?? string.Empty,
            [nameof(A2AApiKeyOptions.Keys)] = (o, s) => o.Keys.AddRange(s.GetChildren().Select(ApiKeyEntry))
        };

    private static readonly Dictionary<string, Action<A2AApiKeyEntry, IConfigurationSection>> ApiKeyEntryKeys =
        new(Keys)
        {
            [nameof(A2AApiKeyEntry.CallerId)] = (e, s) => e.CallerId = s.Value ?? string.Empty,
            [nameof(A2AApiKeyEntry.Hash)] = (e, s) => e.Hash = s.Value ?? string.Empty,
            [nameof(A2AApiKeyEntry.Tenants)] = (e, s) => e.Tenants = Strings(s)
        };

    private static readonly Dictionary<string, Action<A2ARateLimitOptions, IConfigurationSection>> RateLimitKeys =
        new(Keys)
        {
            [nameof(A2ARateLimitOptions.TokenLimit)] = (o, s) => o.TokenLimit = Int(s),
            [nameof(A2ARateLimitOptions.TokensPerPeriod)] = (o, s) => o.TokensPerPeriod = Int(s),
            [nameof(A2ARateLimitOptions.ReplenishmentPeriod)] = (o, s) => o.ReplenishmentPeriod = Duration(s)
        };

    private static readonly Dictionary<string, Action<AgentProvider, IConfigurationSection>> ProviderKeys = new(Keys)
    {
        [nameof(AgentProvider.Organization)] = (p, s) => p.Organization = s.Value ?? string.Empty,
        [nameof(AgentProvider.Url)] = (p, s) => p.Url = s.Value ?? string.Empty
    };

    private static readonly Dictionary<string, Action<AgentCapabilities, IConfigurationSection>> CapabilityKeys =
        new(Keys)
        {
            [nameof(AgentCapabilities.Streaming)] = (c, s) => c.Streaming = Bool(s),
            [nameof(AgentCapabilities.PushNotifications)] = (c, s) => c.PushNotifications = Bool(s)
        };

    private static readonly Dictionary<string, Action<AgentSkill, IConfigurationSection>> SkillKeys = new(Keys)
    {
        [nameof(AgentSkill.Id)] = (k, s) => k.Id = s.Value ?? string.Empty,
        [nameof(AgentSkill.Name)] = (k, s) => k.Name = s.Value ?? string.Empty,
        [nameof(AgentSkill.Description)] = (k, s) => k.Description = s.Value ?? string.Empty,
        [nameof(AgentSkill.Tags)] = (k, s) => k.Tags = Strings(s),
        [nameof(AgentSkill.Examples)] = (k, s) => k.Examples = Strings(s),
        [nameof(AgentSkill.InputModes)] = (k, s) => k.InputModes = Strings(s),
        [nameof(AgentSkill.OutputModes)] = (k, s) => k.OutputModes = Strings(s)
    };

    private static readonly Dictionary<string, Action<AgentCard, IConfigurationSection>> CardKeys = new(Keys)
    {
        [nameof(AgentCard.Name)] = (c, s) => c.Name = s.Value ?? string.Empty,
        [nameof(AgentCard.Description)] = (c, s) => c.Description = s.Value ?? string.Empty,
        [nameof(AgentCard.Version)] = (c, s) => c.Version = s.Value ?? string.Empty,
        [nameof(AgentCard.DocumentationUrl)] = (c, s) => c.DocumentationUrl = Empty(s) ? null : s.Value,
        [nameof(AgentCard.IconUrl)] = (c, s) => c.IconUrl = Empty(s) ? null : s.Value,
        [nameof(AgentCard.DefaultInputModes)] = (c, s) => c.DefaultInputModes = Strings(s),
        [nameof(AgentCard.DefaultOutputModes)] = (c, s) => c.DefaultOutputModes = Strings(s),
        [nameof(AgentCard.Provider)] = (c, s) => Apply(s, c.Provider ??= new AgentProvider(), ProviderKeys),
        [nameof(AgentCard.Capabilities)] = (c, s) => Apply(s, c.Capabilities, CapabilityKeys),
        [nameof(AgentCard.Skills)] = (c, s) => c.Skills = s.GetChildren().Select(Skill).ToList()
    };

    /// <summary>
    ///     Registers the A2A server and its ASP.NET Core endpoints like <see cref="A2AServiceCollectionExtensions.AddA2A(IServiceCollection, Action{A2AServerBuilder}?)" />,
    ///     with the settings in <paramref name="configuration" /> (for example <c>builder.Configuration.GetSection("A2A")</c>)
    ///     applied first. See <see cref="A2AConfigurationExtensions" /> for the keys. Returns the builder.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="services" /> or <paramref name="configuration" /> is null.</exception>
    public static A2AServerBuilder AddA2A(this IServiceCollection services, IConfiguration configuration,
        Action<A2AServerBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.ConfigureA2A(configuration);
        return services.AddA2A(configure);
    }

    /// <summary>
    ///     Applies the settings in <paramref name="configuration" /> to <see cref="A2AServerOptions" />,
    ///     <see cref="A2AHttpOptions" /> and <see cref="A2ARateLimitOptions" />. <c>AddA2A(configuration, ...)</c> calls it;
    ///     without that overload, call it before <c>AddA2AServer</c> or <c>AddA2A</c>, so their code overrides it. See
    ///     <see cref="A2AConfigurationExtensions" /> for the keys.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IServiceCollection ConfigureA2A(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.Configure<A2AServerOptions>(o => Apply(configuration, o, ServerKeys));
        services.Configure<A2AHttpOptions>(o =>
        {
            if (configuration.GetSection(nameof(A2AHttpOptions.PublicBaseUrl)) is { Value: not null } url)
                o.PublicBaseUrl = Empty(url) ? null : AbsoluteUri(url);
            if (configuration.GetSection(nameof(A2AHttpOptions.AgentCardMaxAge)) is { Value: not null } age)
                o.AgentCardMaxAge = Duration(age);
        });
        services.Configure<A2AApiKeyOptions>(o =>
        {
            var section = configuration.GetSection("ApiKeys");
            if (section.Exists()) Apply(section, o, ApiKeyKeys);
        });
        // Keys are rotated and revoked without a restart: a configuration reload rebuilds the key options.
        services.AddSingleton<IOptionsChangeTokenSource<A2AApiKeyOptions>>(
            new ConfigurationChangeTokenSource<A2AApiKeyOptions>(Options.DefaultName, configuration));
        services.Configure<A2ARateLimitOptions>(o =>
        {
            var section = configuration.GetSection("RateLimit");
            if (section.Exists())
                Apply(section, o, RateLimitKeys,
                    new Dictionary<string, string>(Keys) { ["Enabled"] = "it turns rate limiting off" });
        });
        return services;
    }

    private static void Apply<T>(IConfiguration section, T target,
        Dictionary<string, Action<T, IConfigurationSection>> keys, Dictionary<string, string>? codeOnly = null)
    {
        foreach (var child in section.GetChildren())
        {
            if (keys.TryGetValue(child.Key, out var apply))
            {
                apply(target, child);
                continue;
            }

            if ((codeOnly ?? CodeOnly).TryGetValue(child.Key, out var why))
                throw new InvalidOperationException(
                    $"Configuration key '{child.Path}' is not read from configuration because {why}. Set it in code.");
            throw new InvalidOperationException(
                $"Unknown A2A configuration key '{child.Path}'. Known keys here: {string.Join(", ", keys.Keys)}.");
        }
    }

    private static void ApplyCard(AgentCard card, IConfigurationSection section)
    {
        Apply(section, card, CardKeys);
    }

    private static A2AApiKeyEntry ApiKeyEntry(IConfigurationSection section)
    {
        if (section.GetSection("Key").Exists())
            throw new InvalidOperationException(
                $"Configuration key '{section.Path}:Key' holds an API key. Store only its hash, as '{section.Path}:Hash': 'a2a keys new' prints both, and the key goes to the caller.");
        var entry = new A2AApiKeyEntry();
        Apply(section, entry, ApiKeyEntryKeys);
        return entry;
    }

    private static AgentSkill Skill(IConfigurationSection section)
    {
        var skill = new AgentSkill();
        Apply(section, skill, SkillKeys);
        return skill;
    }

    private static bool Empty(IConfigurationSection s)
    {
        return string.IsNullOrWhiteSpace(s.Value);
    }

    private static List<string> Strings(IConfigurationSection s)
    {
        return s.GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).ToList();
    }

    private static int Int(IConfigurationSection s)
    {
        return Parse(s, v => int.Parse(v, NumberStyles.Integer, CultureInfo.InvariantCulture), "a whole number");
    }

    private static long Long(IConfigurationSection s)
    {
        return Parse(s, v => long.Parse(v, NumberStyles.Integer, CultureInfo.InvariantCulture), "a whole number");
    }

    private static decimal Decimal(IConfigurationSection s)
    {
        return Parse(s, v => decimal.Parse(v, NumberStyles.Number, CultureInfo.InvariantCulture), "a number");
    }

    private static bool Bool(IConfigurationSection s)
    {
        return Parse(s, bool.Parse, "true or false");
    }

    private static TimeSpan Duration(IConfigurationSection s)
    {
        return Parse(s, v => TimeSpan.Parse(v, CultureInfo.InvariantCulture), "a duration such as 00:00:30");
    }

    private static T Enum<T>(IConfigurationSection s) where T : struct, Enum
    {
        return Parse(s, v => System.Enum.TryParse<T>(v, true, out var e) && System.Enum.IsDefined(e)
            ? e
            : throw new FormatException(), $"one of {string.Join(", ", System.Enum.GetNames<T>())}");
    }

    private static Uri AbsoluteUri(IConfigurationSection s)
    {
        return Parse(s, v => new Uri(v, UriKind.Absolute), "an absolute URL");
    }

    private static T Parse<T>(IConfigurationSection s, Func<string, T> parse, string expected)
    {
        try
        {
            return parse((s.Value ?? string.Empty).Trim());
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"Configuration key '{s.Path}' has the value '{s.Value}', which is not {expected}.", e);
        }
    }
}
