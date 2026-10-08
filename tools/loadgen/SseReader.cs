using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Luftpause.LoadGen;

public static class SseReader
{
    public static async Task<TurnReply> ReadAsync(Stream stream, Stopwatch clock, CancellationToken cancel)
    {
        using var reader = new StreamReader(stream);
        var ttft = double.NaN;
        string? finishReason = null;
        var cachedTokens = 0;
        var promptTokens = 0;
        var toolCalls = new ToolCallAssembler();

        while (await reader.ReadLineAsync(cancel) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;
            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]")
                break;

            var chunk = JsonNode.Parse(payload);
            cachedTokens = chunk?["usage"]?["prompt_tokens_details"]?["cached_tokens"]?.GetValue<int>() ?? cachedTokens;
            promptTokens = chunk?["usage"]?["prompt_tokens"]?.GetValue<int>() ?? promptTokens;
            var choice = chunk?["choices"]?.AsArray().FirstOrDefault();
            var delta = choice?["delta"];
            var calls = delta?["tool_calls"]?.AsArray();
            var hasToken = !string.IsNullOrEmpty(delta?["content"]?.GetValue<string>()) || calls is { Count: > 0 };

            if (hasToken && double.IsNaN(ttft))
                ttft = clock.Elapsed.TotalMilliseconds;
            foreach (var call in calls ?? [])
                toolCalls.Add(call);
            finishReason = choice?["finish_reason"]?.GetValue<string>() ?? finishReason;
        }
        return new TurnReply(200, ttft, finishReason, [.. toolCalls.Build().Take(1).Select(c => c?.DeepClone())], cachedTokens, promptTokens);
    }
}
