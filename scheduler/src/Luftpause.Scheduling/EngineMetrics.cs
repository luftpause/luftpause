using System.Globalization;

namespace Luftpause.Scheduling;

public sealed record EngineLoadSample(int Running, int Waiting)
{
    public static EngineLoadSample Sum(IEnumerable<EngineLoadSample> samples) =>
        samples.Aggregate(new EngineLoadSample(0, 0), (total, s) => new EngineLoadSample(total.Running + s.Running, total.Waiting + s.Waiting));
}

public static class EngineMetrics
{
    private static readonly string[] Running = ["sglang:num_running_reqs", "vllm:num_requests_running"];
    private static readonly string[] Waiting = ["sglang:num_queue_reqs", "vllm:num_requests_waiting"];

    public static EngineLoadSample? Parse(string text)
    {
        var samples = text.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#') && !IsPriorityBreakdown(line))
            .Select(Sample)
            .Where(s => s is not null)
            .Select(s => s!.Value)
            .ToList();

        var running = samples.Where(s => Running.Contains(s.Name)).ToList();
        var waiting = samples.Where(s => Waiting.Contains(s.Name)).ToList();
        return running.Count == 0 || waiting.Count == 0
            ? null
            : new EngineLoadSample((int)running.Sum(s => s.Value), (int)waiting.Sum(s => s.Value));
    }

    private static bool IsPriorityBreakdown(string line)
    {
        var label = line.IndexOf("priority=\"", StringComparison.Ordinal);
        return label > 0 && line[label - 1] is '{' or ',' && line[label + "priority=\"".Length] != '"';
    }

    private static (string Name, double Value)? Sample(string line)
    {
        var nameEnd = line.IndexOfAny(['{', ' ']);
        var valueStart = line.LastIndexOf(' ');
        if (nameEnd <= 0 || valueStart <= 0)
            return null;
        return double.TryParse(line[(valueStart + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? (line[..nameEnd], value)
            : null;
    }
}
