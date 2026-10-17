namespace Luftpause.Runtime;

public sealed record LabelScore(string Label, double? Logprob, double Probability);

public static class LabelScores
{
    public static IReadOnlyList<LabelScore> From(
        IReadOnlyList<(string Token, double Logprob)> topLogprobs, IReadOnlyList<string> labels)
    {
        var logprobs = labels.Select(label => Best(topLogprobs, label)).ToList();
        var scored = logprobs.OfType<double>().ToList();
        var max = scored.Count > 0 ? scored.Max() : 0;
        var total = scored.Sum(l => Math.Exp(l - max));
        return labels
            .Zip(logprobs, (label, logprob) => new LabelScore(label, logprob, logprob is { } l ? Math.Exp(l - max) / total : 0))
            .OrderByDescending(s => s.Probability)
            .ToList();
    }

    private static double? Best(IReadOnlyList<(string Token, double Logprob)> topLogprobs, string label)
    {
        var text = label.ToLowerInvariant();
        var matches = topLogprobs
            .Where(t => t.Token.Trim().ToLowerInvariant() is { Length: > 0 } token && text.StartsWith(token, StringComparison.Ordinal))
            .Select(t => t.Logprob)
            .ToList();
        return matches.Count > 0 ? matches.Max() : null;
    }
}
