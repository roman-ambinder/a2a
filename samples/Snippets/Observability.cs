using A2A.Diagnostics;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Snippets.Observability;

public static class ServerSetup
{
    public static void AddServerTelemetry(IServiceCollection services)
    {
        #region ServerOpenTelemetry
        services.AddOpenTelemetry()
            .WithTracing(t => t
                .AddSource(A2ATelemetry.ServerSourceName)
                .AddAspNetCoreInstrumentation()
                .AddOtlpExporter())
            .WithMetrics(m => m
                .AddMeter(A2ATelemetry.ServerMeterName, "A2A")
                .AddOtlpExporter());
        #endregion
    }

    public static void AddClientTelemetry(IServiceCollection services)
    {
        #region ClientOpenTelemetry
        services.AddOpenTelemetry()
            .WithTracing(t => t
                .AddSource(A2ATelemetry.ClientSourceName)
                .AddOtlpExporter());
        #endregion
    }
}
