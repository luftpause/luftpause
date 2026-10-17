using System.Text.Json.Nodes;

namespace Luftpause.Runtime;

public static class LabelDecision
{
    public static async Task<string> DecideAsync(
        LlmClient llm, string model, string input, IReadOnlyList<string> labels, CancellationToken cancel)
    {
        var checkedLabels = Validate(labels);
        var runId = $"run-{Guid.NewGuid():N}";
        IReadOnlyList<(string Token, double Logprob)> alternatives;
        try
        {
            alternatives = await llm.FirstTokenAlternativesAsync(runId, model, Messages(input, checkedLabels), cancel);
        }
        finally
        {
            await llm.EndRunAsync(runId, CancellationToken.None);
        }
        return Result(LabelScores.From(alternatives, checkedLabels)).ToJsonString();
    }

    public static IReadOnlyList<string> Validate(IReadOnlyList<string> labels)
    {
        if (labels.Count is < 2 or > 20)
            throw new ArgumentException($"labels takes 2 to 20 labels, got {labels.Count}");
        var trimmed = labels.Select(l => l.Trim()).ToList();
        if (trimmed.Any(l => l.Length == 0))
            throw new ArgumentException("labels must not be empty");
        var clash = trimmed.GroupBy(FirstWord, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (clash is not null)
            throw new ArgumentException($"labels {string.Join(", ", clash)} share their first word; their first tokens cannot be told apart");
        return trimmed;
    }

    public static JsonArray Messages(string input, IReadOnlyList<string> labels) =>
    [
        new JsonObject { ["role"] = "system", ["content"] = "Answer with exactly one of the labels, nothing else." },
        new JsonObject { ["role"] = "user", ["content"] = $"{input}\n\nLabels: {string.Join(", ", labels)}\nAnswer:" },
    ];

    private static string FirstWord(string label) => label.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0];

    private static JsonObject Result(IReadOnlyList<LabelScore> scores)
    {
        if (scores[0].Logprob is null)
            throw new InvalidOperationException("no label was among the model's first-token candidates");
        return new JsonObject
        {
            ["label"] = scores[0].Label,
            ["probabilities"] = new JsonObject(scores.Select(s => KeyValuePair.Create(s.Label, (JsonNode?)s.Probability))),
            ["unscored"] = new JsonArray(scores.Where(s => s.Logprob is null).Select(s => (JsonNode?)s.Label).ToArray()),
        };
    }
}
