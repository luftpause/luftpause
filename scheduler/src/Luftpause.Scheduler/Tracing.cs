using System.Diagnostics;
using OpenTelemetry.Trace;

namespace Luftpause.Scheduler;

public static class Tracing
{
    public const string SourceName = "Luftpause.Scheduler";
    public static readonly ActivitySource Source = new(SourceName);

    public static IServiceCollection AddSchedulerTracing(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOpenTelemetry().WithTracing(tracing =>
        {
            tracing.AddSource(SourceName);
            if (!string.IsNullOrEmpty(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
                tracing.AddOtlpExporter();
        });
        return services;
    }

    public static void SetTags(this Activity activity, IEnumerable<KeyValuePair<string, object?>> tags)
    {
        foreach (var (key, value) in tags)
            activity.SetTag(key, value);
    }
}
