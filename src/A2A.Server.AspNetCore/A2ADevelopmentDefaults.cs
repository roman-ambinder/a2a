using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace A2A.Server.AspNetCore;

/// <summary>Development settings for local work: one call instead of several relaxed options.</summary>
public static class A2ADevelopmentDefaultsExtensions
{
    internal static string NotDevelopmentMessage(string environment) =>
        $"UseDevelopmentDefaults() is for the Development environment only, and this host runs as '{environment}'. It turns off authentication for A2A calls. Call it only when builder.Environment.IsDevelopment() is true.";

    /// <summary>
    ///     For local development: lets anonymous callers in (<see cref="A2AServerOptions.AllowAnonymous" />), accepts calls
    ///     without the <c>A2A-Version</c> header such as plain <c>curl</c>
    ///     (<see cref="A2AServerOptions.TreatMissingVersionAsCurrent" />), and puts exception text in errors
    ///     (<see cref="A2AServerOptions.IncludeExceptionDetails" />). Rate limiting and every limit stay on.
    /// </summary>
    /// <remarks>
    ///     The app refuses to start when the host environment is not Development, so these settings never reach production
    ///     by mistake: wrap the call in <c>if (builder.Environment.IsDevelopment())</c> when the same code runs elsewhere.
    ///     Code that sets one of these options after this call wins.
    /// </remarks>
    /// <exception cref="InvalidOperationException">At startup, when the host environment is not Development.</exception>
    public static A2AServerBuilder UseDevelopmentDefaults(this A2AServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<IConfigureOptions<A2AServerOptions>, DevelopmentDefaults>();
        return builder;
    }

    private sealed class DevelopmentDefaults(IHostEnvironment? environment = null) : IConfigureOptions<A2AServerOptions>
    {
        public void Configure(A2AServerOptions options)
        {
            if (environment is null || !environment.IsDevelopment())
                throw new InvalidOperationException(
                    NotDevelopmentMessage(environment?.EnvironmentName ?? "(no host environment)"));
            options.AllowAnonymous = true;
            options.TreatMissingVersionAsCurrent = true;
            options.IncludeExceptionDetails = true;
        }
    }
}
