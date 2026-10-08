namespace Luftpause.LoadGen;

public static class Report
{
    public static string Summarise(IReadOnlyList<IReadOnlyList<TurnSample>> runs, TimeSpan wall)
    {
        var samples = runs.SelectMany(r => r).ToList();
        var ok = samples.Where(s => s.Status == 200).ToList();
        return string.Join(Environment.NewLine,
        [
            $"runs {runs.Count}  turns {samples.Count}  ok {ok.Count}  failed {samples.Count - ok.Count}  wall {wall.TotalSeconds:F1}s",
            $"runs split across run ids {Count(runs, s => s.RunId)}  runs that changed replica {Count(runs, s => s.Replica)}",
            Line("ttft first ", ok.Where(s => !s.Resumed).Select(s => s.TtftMs)),
            Line("ttft resume", ok.Where(s => s.Resumed).Select(s => s.TtftMs)),
            Line("  kv hit   ", ok.Where(s => s.Resumed && s.CachedTokens > 0).Select(s => s.TtftMs)),
            Line("  kv miss  ", ok.Where(s => s.Resumed && s.CachedTokens == 0).Select(s => s.TtftMs)),
            Line("turn total ", ok.Select(s => s.TotalMs)),
            Prefill(ok.Where(s => s.Resumed).ToList()),
        ]);
    }

    private static string Prefill(IReadOnlyList<TurnSample> resumed)
    {
        var prompt = resumed.Sum(s => (long)s.PromptTokens);
        var cached = resumed.Sum(s => (long)s.CachedTokens);
        return prompt == 0
            ? "prefill    n/a"
            : $"prefill on resume  prompt {prompt}  cached {cached} ({100.0 * cached / prompt:F1}%)  computed {prompt - cached}";
    }

    private static int Count(IEnumerable<IReadOnlyList<TurnSample>> runs, Func<TurnSample, string?> key) =>
        runs.Count(run => run.Select(key).Distinct().Count() > 1);

    private static string Line(string label, IEnumerable<double> values)
    {
        var sorted = values.Where(v => !double.IsNaN(v)).Order().ToArray();
        return sorted.Length == 0
            ? $"{label}  n=0"
            : $"{label}  n={sorted.Length}  p50 {Percentile(sorted, 0.50):F1} ms  p99 {Percentile(sorted, 0.99):F1} ms  max {sorted[^1]:F1} ms";
    }

    private static double Percentile(double[] sorted, double p) =>
        sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
}
