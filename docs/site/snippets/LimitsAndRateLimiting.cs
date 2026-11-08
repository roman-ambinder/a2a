using A2A.Server;
using A2A.Server.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.DocSnippets.LimitsAndRateLimiting;

internal static class Samples
{
    public static void Limits()
    {
        var services = new ServiceCollection();
        #region Limits
        services.AddA2AServer(o =>
        {
            o.Limits.MaxRequestBodyBytes = 2 * 1024 * 1024;
            o.Limits.MaxPartsPerMessage = 20;
            o.Limits.MaxConcurrentStreamsPerPrincipal = 20;
            o.Limits.MaxStreamDuration = TimeSpan.FromMinutes(30);
        });
        #endregion
    }

    public static void RateLimit()
    {
        var services = new ServiceCollection();
        #region RateLimit
        services.AddA2ARateLimiting(o =>
        {
            o.TokenLimit = 10;
            o.TokensPerPeriod = 30;
            o.ReplenishmentPeriod = TimeSpan.FromMinutes(1);
        });
        #endregion
    }

    public static void RateLimitOff()
    {
        var services = new ServiceCollection();
        #region RateLimitOff
        services.AddA2ARateLimiting(o => o.Enabled = false);
        #endregion
    }

    public static void Proxy()
    {
        var builder = WebApplication.CreateBuilder();
        #region Proxy
        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
            o.KnownProxies.Add(System.Net.IPAddress.Parse("10.0.0.5"));
        });

        var app = builder.Build();
        app.UseForwardedHeaders();   // before UseA2A, so the remote address is the client's
        app.UseAuthentication();
        app.UseA2A();
        app.UseAuthorization();
        #endregion
    }
}
