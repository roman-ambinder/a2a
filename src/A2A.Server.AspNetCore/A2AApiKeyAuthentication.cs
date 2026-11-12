using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace A2A.Server.AspNetCore;

/// <summary>The caller an API key belongs to, as an <see cref="IApiKeyStore" /> returns it.</summary>
/// <param name="CallerId">
///     The caller's id. The task owner is <c>apikey:</c> and this id, so API-key callers never share tasks with callers of
///     another scheme, or with each other.
/// </param>
/// <param name="Tenants">The tenants the key may call, or null for every tenant.</param>
public sealed record ApiKeyCaller(string CallerId, IReadOnlyList<string>? Tenants = null);

/// <summary>
///     Finds the caller of an API key by the key's hash (<see cref="A2AApiKey.ComputeHash" />). The default store reads
///     <see cref="A2AApiKeyOptions.Keys" /> (code and the <c>A2A:ApiKeys</c> configuration section) and follows reloads.
///     Register your own to keep keys in a database or a secret store without a configuration provider.
/// </summary>
/// <remarks>
///     It is called once per request that carries a well-formed key, so an implementation that goes over the network
///     should cache: a hash is not a secret, so a cache of hashes is safe to keep. Return null for an unknown or revoked key.
/// </remarks>
public interface IApiKeyStore
{
    /// <summary>The caller of the key whose hash is <paramref name="keyHash" />, or null when there is none.</summary>
    ValueTask<ApiKeyCaller?> FindAsync(string keyHash, CancellationToken cancellationToken);
}

/// <summary>One registered key: its hash and the caller it belongs to.</summary>
public sealed class A2AApiKeyEntry
{
    /// <summary>The caller's id (see <see cref="ApiKeyCaller.CallerId" />). Several keys may share one, for rotation.</summary>
    public string CallerId { get; set; } = string.Empty;

    /// <summary>The key's hash: <c>sha256:</c> and 64 lowercase hex digits (<see cref="A2AApiKey.Hash" />).</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>The tenants the key may call, or null (or empty) for every tenant.</summary>
    public List<string>? Tenants { get; set; }
}

/// <summary>Settings of <see cref="A2AApiKeyAuthenticationExtensions.AddApiKeyAuthentication" />.</summary>
public sealed class A2AApiKeyOptions
{
    /// <summary>The ASP.NET Core authentication scheme name.</summary>
    public const string SchemeName = "A2AApiKey";


    /// <summary>
    ///     The prefix of the task owner id of an API-key caller. Reserved: a caller of another scheme whose owner id starts
    ///     with it is refused as unauthenticated.
    /// </summary>
    public const string OwnerPrefix = "apikey:";


    /// <summary>The header the key travels in. Default <c>X-Api-Key</c>. The Agent Card declares it.</summary>
    public string HeaderName { get; set; } = "X-Api-Key";

    /// <summary>The name of the scheme in the Agent Card's <c>securitySchemes</c>. Default <c>apiKey</c>.</summary>
    public string CardSchemeName { get; set; } = "apiKey";

    /// <summary>The registered keys, read by the default <see cref="IApiKeyStore" />.</summary>
    public List<A2AApiKeyEntry> Keys { get; } = [];

    /// <summary>Registers the key whose hash is <paramref name="hash" /> for <paramref name="callerId" />.</summary>
    /// <param name="callerId">The caller's id.</param>
    /// <param name="hash">The key's hash (<see cref="A2AApiKey.Hash" />), never the key itself.</param>
    /// <param name="tenants">The tenants the key may call; none for every tenant.</param>
    /// <returns>These options, so calls can be chained.</returns>
    public A2AApiKeyOptions Add(string callerId, string hash, params string[] tenants)
    {
        Keys.Add(new A2AApiKeyEntry
        {
            CallerId = callerId, Hash = hash, Tenants = tenants is { Length: > 0 } ? [.. tenants] : null
        });
        return this;
    }
}

/// <summary>API-key authentication for A2A endpoints.</summary>
public static class A2AApiKeyAuthenticationExtensions
{
    /// <summary>
    ///     Lets callers in with an API key, in one call: an ASP.NET Core authentication scheme that reads the key from
    ///     <see cref="A2AApiKeyOptions.HeaderName" />, a caller id per key (the task owner is <c>apikey:</c> and the id), the
    ///     scheme on the Agent Card (<c>securitySchemes</c> and <c>securityRequirements</c>, so clients such as
    ///     <c>AddApiKey</c> and the <c>a2a</c> tool find the header), an optional tenant list per key (as <see cref="A2AClaimTypes.Tenant" /> claims, so a call to another tenant is
    ///     refused and audited like any other denial), and the policy
    ///     <see cref="A2ACardAuthenticationExtensions.DefaultPolicyName" /> (any declared scheme, API keys among them) on every
    ///     A2A endpoint that has no authorization of its own.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Keys are registered by their hash only (<see cref="A2AApiKey" />): in <paramref name="configure" />, in the
    ///         <c>A2A:ApiKeys</c> configuration section (<c>ConfigureA2A</c>), or by an <see cref="IApiKeyStore" /> of your
    ///         own. A request whose key is not well formed or not known is unauthenticated (401). Keys in the query string
    ///         are never read.
    ///     </para>
    ///     <para>
    ///         Endpoints that already carry authorization (<c>RequireAuthorization(...)</c>, <c>AuthorizationPolicy</c>,
    ///         <c>AllowAnonymous</c>) keep it and do not get the default policy: add the scheme to your policy yourself, for example
    ///         <c>p.AddAuthenticationSchemes(A2AApiKeyOptions.SchemeName, "Bearer")</c>. The card lists the API key as one
    ///         more way in. Cards from a <see cref="Agents.TenantCardProvider" /> are not changed: call
    ///         <see cref="A2ACardSecurity.Apply" /> on them.
    ///     </para>
    /// </remarks>
    public static A2AServerBuilder AddApiKeyAuthentication(this A2AServerBuilder builder,
        Action<A2AApiKeyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        if (configure is not null) services.Configure(configure);
        if (services.Any(d => d.ServiceType == typeof(ApiKeyMarker))) return builder;

        services.AddSingleton<ApiKeyMarker>();
        // Checked once at startup, not on every options read: a broken configuration reload must not fail every request.
        services.AddHostedService<ApiKeyStartupCheck>();
        services.TryAddSingleton<IApiKeyStore, OptionsApiKeyStore>();
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(A2AApiKeyOptions.SchemeName, null);
        services.AddSingleton<ICardSchemeDeclaration, ApiKeyCardScheme>();
        // Also a way into A2ACardSchemes, the default policy, next to schemes declared with AddCardAuthentication.
        A2ACardAuthenticationExtensions.AddDefaultScheme(services, A2AApiKeyOptions.SchemeName);
        return builder;
    }

    private sealed class ApiKeyMarker;

    /// <summary>Fails the app at startup when the key options are invalid, and builds the store so its errors show too.</summary>
    private sealed class ApiKeyStartupCheck(IOptions<A2AApiKeyOptions> options, IServiceProvider services)
        : Microsoft.Extensions.Hosting.IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            var result = ApiKeyOptionsValidator.Validate(Options.DefaultName, options.Value);
            if (result.Failed)
                throw new OptionsValidationException(Options.DefaultName, typeof(A2AApiKeyOptions), result.Failures);
            _ = services.GetRequiredService<IApiKeyStore>();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private static class ApiKeyOptionsValidator
    {
        public static ValidateOptionsResult Validate(string? name, A2AApiKeyOptions options)
        {
            var problems = new List<string>();
            if (string.IsNullOrWhiteSpace(options.HeaderName)) problems.Add("HeaderName is empty.");
            if (string.IsNullOrWhiteSpace(options.CardSchemeName)) problems.Add("CardSchemeName is empty.");
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < options.Keys.Count; i++)
            {
                var entry = options.Keys[i];
                var where = $"Keys[{i}] ('{entry.CallerId}')";
                if (string.IsNullOrWhiteSpace(entry.CallerId) || entry.CallerId.Any(char.IsControl))
                    problems.Add($"Keys[{i}] has no caller id.");
                if (!A2AApiKey.IsWellFormedHash(entry.Hash))
                    problems.Add(A2AApiKey.IsWellFormed(entry.Hash)
                        ? $"{where} holds a key, not a hash. Store only the hash: 'a2a keys new' prints both."
                        : $"{where} has no valid hash: it must be 'sha256:' and 64 lowercase hex digits.");
                else if (seen.TryGetValue(entry.Hash, out var other))
                    problems.Add($"{where} has the same hash as the key of '{other}'.");
                else
                    seen[entry.Hash] = entry.CallerId;
                if (entry.Tenants?.Any(t => t is null) == true) problems.Add($"{where} has an empty tenant.");
            }

            return problems.Count == 0
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(problems.Select(p => "A2A API keys: " + p));
        }
    }

    /// <summary>The default store: the keys in <see cref="A2AApiKeyOptions" />, rebuilt when the options change.</summary>
    private sealed class OptionsApiKeyStore : IApiKeyStore, IDisposable
    {
        private readonly IDisposable? _subscription;
        private Dictionary<string, ApiKeyCaller> _byHash;

        public OptionsApiKeyStore(IOptionsMonitor<A2AApiKeyOptions> options, ILogger<OptionsApiKeyStore> logger)
        {
            _byHash = Build(options.CurrentValue);
            _subscription = options.OnChange(o =>
            {
                try
                {
                    Volatile.Write(ref _byHash, Build(o));
                }
                catch (OptionsValidationException e)
                {
                    // Keep the keys that work; a broken reload must not lock every caller out.
                    AspNetCoreLog.ApiKeysNotReloaded(logger, e, string.Join(" ", e.Failures));
                }
            });
        }

        public void Dispose()
        {
            _subscription?.Dispose();
        }

        public ValueTask<ApiKeyCaller?> FindAsync(string keyHash, CancellationToken cancellationToken)
        {
            return new ValueTask<ApiKeyCaller?>(Volatile.Read(ref _byHash).GetValueOrDefault(keyHash));
        }

        private static Dictionary<string, ApiKeyCaller> Build(A2AApiKeyOptions options)
        {
            var result = ApiKeyOptionsValidator.Validate(null, options);
            if (result.Failed) throw new OptionsValidationException(Options.DefaultName, typeof(A2AApiKeyOptions),
                result.Failures);
            return options.Keys.ToDictionary(k => k.Hash,
                k => new ApiKeyCaller(k.CallerId, k.Tenants is { Count: > 0 } t ? t.ToArray() : null),
                StringComparer.Ordinal);
        }
    }

    private sealed class ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IApiKeyStore store,
        IOptionsMonitor<A2AApiKeyOptions> keys)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(keys.CurrentValue.HeaderName, out var values) || values.Count == 0)
                return AuthenticateResult.NoResult();
            if (values.Count != 1) return AuthenticateResult.Fail("Send one API key.");
            var key = values[0];
            if (!A2AApiKey.IsWellFormed(key)) return AuthenticateResult.Fail("The API key is not well formed.");
            var caller = await store.FindAsync(A2AApiKey.ComputeHash(key!), Context.RequestAborted)
                .ConfigureAwait(false);
            if (caller is null || string.IsNullOrWhiteSpace(caller.CallerId))
                return AuthenticateResult.Fail("The API key is not known.");

            List<Claim> claims =
            [
                new(ClaimTypes.NameIdentifier, A2AApiKeyOptions.OwnerPrefix + caller.CallerId),
                new(ClaimTypes.Name, caller.CallerId),
                new(A2AClaimTypes.ApiKeyCaller, caller.CallerId)
            ];
            foreach (var tenant in caller.Tenants ?? []) claims.Add(new Claim(A2AClaimTypes.Tenant, tenant));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
        }
    }

    /// <summary>Declares the API-key scheme on the host's Agent Card, unless the card already has a scheme of that name.</summary>
    /// <summary>The API-key scheme, as the options name it.</summary>
    private sealed class ApiKeyCardScheme(IOptions<A2AApiKeyOptions> keys) : ICardSchemeDeclaration
    {
        public string CardName => keys.Value.CardSchemeName;
        public SecurityScheme Scheme => SecurityScheme.ApiKey(keys.Value.HeaderName);
        public IReadOnlyList<string> Scopes => [];
    }
}
