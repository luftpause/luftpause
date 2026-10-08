using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Luftpause.Runtime;

public sealed record LlmReply(string? Content, IReadOnlyList<JsonObject> ToolCalls, string? FinishReason);

public sealed class LlmClient(HttpClient http, EnginePriority priority)
{
    private const int MaxTokens = 256;

    public async Task<LlmReply> ChatAsync(
        string runId, RunSpec spec, JsonArray messages, JsonArray tools, CancellationToken cancel)
    {
        var body = new JsonObject
        {
            ["model"] = spec.Model,
            ["messages"] = messages.DeepClone(),
            ["max_tokens"] = MaxTokens,
        };
        if (priority.ValueFor(spec.ServiceClass) is { } value)
            body["priority"] = value;
        if (tools.Count > 0)
        {
            body["tools"] = tools.DeepClone();
            body["parallel_tool_calls"] = false;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions") { Content = JsonContent.Create(body) };
        request.Headers.Add("x-luftpause-run-id", runId);
        request.Headers.Add("x-luftpause-class", spec.ServiceClass);
        using var response = await http.SendAsync(request, cancel);
        var text = await response.Content.ReadAsStringAsync(cancel);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"chat turn failed: {(int)response.StatusCode} {text[..Math.Min(text.Length, 300)]}");

        var choice = JsonNode.Parse(text)?["choices"]?[0];
        var message = choice?["message"];
        var calls = message?["tool_calls"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
        return new LlmReply(message?["content"]?.GetValue<string>(), calls, choice?["finish_reason"]?.GetValue<string>());
    }

    public async Task<string> DecideAsync(string model, string input, CancellationToken cancel)
    {
        using var response = await http.PostAsJsonAsync("v1/decide", new { model, input }, cancel);
        var text = await response.Content.ReadAsStringAsync(cancel);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"decide failed: {(int)response.StatusCode} {text[..Math.Min(text.Length, 300)]}");
        return text;
    }
}

public sealed record EnginePriority(bool Send)
{
    public const int Interactive = 100;
    public const int Batch = 0;

    public int? ValueFor(string serviceClass) =>
        !Send ? null : string.Equals(serviceClass, "batch", StringComparison.OrdinalIgnoreCase) ? Batch : Interactive;
}
