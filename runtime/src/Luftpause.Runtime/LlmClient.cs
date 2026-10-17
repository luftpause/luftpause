using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Luftpause.Runtime;

public sealed record LlmReply(string? Content, IReadOnlyList<JsonObject> ToolCalls, string? FinishReason, TokenUsage Usage);

public sealed class LlmClient(HttpClient http)
{
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(1);

    public async Task<LlmReply> ChatAsync(
        string runId, RunSpec spec, JsonArray messages, JsonArray tools, CancellationToken cancel)
    {
        var body = new JsonObject
        {
            ["model"] = spec.Model,
            ["messages"] = messages.DeepClone(),
        };
        if (spec.MaxTokens is { } maxTokens)
            body["max_tokens"] = maxTokens;
        if (tools.Count > 0)
        {
            body["tools"] = tools.DeepClone();
            if (!spec.ParallelToolCalls)
                body["parallel_tool_calls"] = false;
        }

        using var activity = Tracing.Source.StartActivity($"chat {spec.Model}", ActivityKind.Client, default(ActivityContext), GenAiTags.ChatRequest(spec));
        var root = await PostChatAsync(runId, spec.ServiceClass, spec.Caller, body, cancel);
        var choice = root?["choices"]?[0];
        var message = choice?["message"];
        var calls = message?["tool_calls"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
        var reply = new LlmReply(message?["content"]?.GetValue<string>(), calls, choice?["finish_reason"]?.GetValue<string>(), Usage(root?["usage"]));
        activity?.SetTags(GenAiTags.ChatResponse(reply));
        return reply;
    }

    public async Task<IReadOnlyList<(string Token, double Logprob)>> FirstTokenAlternativesAsync(
        string runId, string model, JsonArray messages, CancellationToken cancel)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages.DeepClone(),
            ["max_tokens"] = 1,
            ["logprobs"] = true,
            ["top_logprobs"] = 20,
        };
        var root = await PostChatAsync(runId, "interactive", null, body, cancel);
        var alternatives = root?["choices"]?[0]?["logprobs"]?["content"]?[0]?["top_logprobs"]?.AsArray()
            ?? throw new InvalidOperationException("chat turn reply has no top_logprobs for its first token");
        return alternatives
            .Select(a => (a!["token"]!.GetValue<string>(), a["logprob"]!.GetValue<double>()))
            .ToList();
    }

    private async Task<JsonNode?> PostChatAsync(
        string runId, string serviceClass, string? caller, JsonObject body, CancellationToken cancel)
    {
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions") { Content = JsonContent.Create(body) };
            request.Headers.Add("x-luftpause-run-id", runId);
            request.Headers.Add("x-luftpause-class", serviceClass);
            if (caller is not null)
                request.Headers.Add("x-luftpause-caller", caller);
            return request;
        }, cancel);
        var text = await response.Content.ReadAsStringAsync(cancel);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"chat turn failed: {(int)response.StatusCode} {text[..Math.Min(text.Length, 300)]}");
        return JsonNode.Parse(text);
    }

    private static TokenUsage Usage(JsonNode? usage) =>
        usage is null
            ? throw new InvalidOperationException("chat turn reply has no usage")
            : new TokenUsage(
                Count(usage, "prompt_tokens"),
                usage["prompt_tokens_details"]?["cached_tokens"]?.GetValue<int>(),
                Count(usage, "completion_tokens"));

    private static int Count(JsonNode usage, string field) =>
        usage[field]?.GetValue<int>() ?? throw new InvalidOperationException($"chat turn usage has no {field}");

    public async Task EndRunAsync(string runId, CancellationToken cancel)
    {
        using var response = await http.DeleteAsync($"v1/runs/{Uri.EscapeDataString(runId)}", cancel);
    }

    public async Task<string> DecideAsync(string model, string input, CancellationToken cancel)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "v1/decide") { Content = JsonContent.Create(new { model, input }) },
            cancel);
        var text = await response.Content.ReadAsStringAsync(cancel);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"decide failed: {(int)response.StatusCode} {text[..Math.Min(text.Length, 300)]}");
        return text;
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken cancel)
    {
        while (true)
        {
            using var request = build();
            var response = await http.SendAsync(request, cancel);
            if (response.StatusCode != HttpStatusCode.TooManyRequests)
                return response;
            var delay = response.Headers.RetryAfter?.Delta ?? DefaultRetryAfter;
            response.Dispose();
            await Task.Delay(delay, cancel);
        }
    }
}
