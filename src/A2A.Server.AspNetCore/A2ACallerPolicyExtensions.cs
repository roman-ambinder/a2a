using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace A2A.Server.AspNetCore;

/// <summary>
///     Who may call the A2A endpoints, set on the A2A builder. Next to these, <c>AddApiKeyAuthentication</c>,
///     <c>AddBearerAuthentication</c> and <c>AddCardAuthentication</c> require a caller authenticated by a scheme the card
///     declares. Each one applies to every A2A endpoint that has no authorization of its own, so <c>MapA2A</c> needs no
///     <c>RequireAuthorization()</c>. Without any of them the app refuses to start, unless anonymous callers are allowed.
/// </summary>
public static class A2ACallerPolicyExtensions
{
    /// <summary>
    ///     Requires the ASP.NET Core authorization policy <paramref name="policyName" /> on every A2A endpoint that has no
    ///     authorization of its own (not on the Agent Card). Register the policy as usual, with
    ///     <c>services.AddAuthorization(o =&gt; o.AddPolicy(...))</c>; the app refuses to start when it is missing. Calling it
    ///     with several names requires all of them. With a declared card scheme as well, both are required.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="policyName" /> is null, empty or white space.</exception>
    public static A2AServerBuilder RequirePolicy(this A2AServerBuilder builder, string policyName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        var services = builder.Services;
        if (!services.Any(d => d.ImplementationInstance is RequiredPolicy p && p.Name == policyName))
            services.AddSingleton(new RequiredPolicy(policyName));
        services.AddAuthorization();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, RequiredPolicyCheck>());
        return builder;
    }

    /// <summary>
    ///     For local development: in the Development environment, lets anonymous callers in
    ///     (<see cref="A2AServerOptions.AllowAnonymous" />), accepts calls without the <c>A2A-Version</c> header such as plain
    ///     <c>curl</c> (<see cref="A2AServerOptions.TreatMissingVersionAsCurrent" />), and puts exception text in errors
    ///     (<see cref="A2AServerOptions.IncludeExceptionDetails" />). Rate limiting and every limit stay on. In any other
    ///     environment it does nothing, so no environment check is needed around it.
    /// </summary>
    /// <remarks>
    ///     Outside Development the endpoints need their usual authorization: with none, the app refuses to start and says
    ///     that this call had no effect. Code that sets one of these options after this call wins.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is null.</exception>
    public static A2AServerBuilder ConfigureForDevelopment(this A2AServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureOptions<A2AServerOptions>, DevelopmentDefaults>());
        builder.Services.TryAddSingleton<DevelopmentDefaultsMarker>();
        return builder;
    }

    /// <summary>A policy that <see cref="RequirePolicy" /> puts on A2A endpoints without authorization.</summary>
    internal sealed record RequiredPolicy(string Name);

    /// <summary>Records that <see cref="ConfigureForDevelopment" /> was called, for the startup error text.</summary>
    internal sealed class DevelopmentDefaultsMarker;

    private sealed class DevelopmentDefaults(IHostEnvironment? environment = null) : IConfigureOptions<A2AServerOptions>
    {
        public void Configure(A2AServerOptions options)
        {
            if (environment is null || !environment.IsDevelopment()) return;
            options.AllowAnonymous = true;
            options.TreatMissingVersionAsCurrent = true;
            options.IncludeExceptionDetails = true;
        }
    }

    /// <summary>Fails the app at startup when a policy named by <see cref="RequirePolicy" /> is not registered.</summary>
    private sealed class RequiredPolicyCheck(IEnumerable<RequiredPolicy> policies, IServiceProvider services)
        : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var provider = services.GetRequiredService<IAuthorizationPolicyProvider>();
            foreach (var name in policies.Select(p => p.Name).Distinct(StringComparer.Ordinal))
                if (await provider.GetPolicyAsync(name).ConfigureAwait(false) is null)
                    throw new InvalidOperationException(
                        $"RequirePolicy(\"{name}\") names an authorization policy that is not registered. Register it with builder.Services.AddAuthorization(o => o.AddPolicy(\"{name}\", p => ...)).");
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
