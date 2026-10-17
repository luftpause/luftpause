using System.Diagnostics;
using OpenTelemetry.Trace;

namespace Luftpause.Runtime;

public static class Tracing
{
    public const string SourceName = "Luftpause.Runtime";
    public static readonly ActivitySource Source = new(SourceName);

    public static IServiceCollection AddLuftpauseTracing(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOpenTelemetry().WithTracing(tracing =>
        {
            tracing.AddSource(SourceName, "Experimental.ModelContextProtocol").AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
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
