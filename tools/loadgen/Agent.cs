using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Luftpause.LoadGen;

public sealed record TurnSample(bool Resumed, double TtftMs, double TotalMs, int Status, string? RunId, string? Replica, int CachedTokens, int PromptTokens = 0);

public sealed record TurnReply(int Status, double TtftMs, string? FinishReason, JsonArray ToolCalls, int CachedTokens, int PromptTokens = 0);

public sealed class Agent(HttpClient http, AgentShape shape)
{
    private const int MaxTurns = 32;
    private const string RunIdHeader = "x-luftpause-run-id";
    private const string ReplicaHeader = "x-luftpause-replica";

    public async Task<IReadOnlyList<TurnSample>> RunAsync(int index, CancellationToken cancel)
    {
        var runId = Guid.NewGuid().ToString();
        var messages = Conversation.Start(index, shape);
        var samples = new List<TurnSample>();
        var toolCalls = 0;

        for (var turn = 0; turn < MaxTurns; turn++)
        {
            var clock = Stopwatch.StartNew();
            var (reply, servedRun, replica) = await SendAsync(runId, messages, toolCalls < shape.MaxToolCalls, cancel);
            samples.Add(new TurnSample(turn > 0, reply.TtftMs, clock.Elapsed.TotalMilliseconds, reply.Status, servedRun, replica, reply.CachedTokens, reply.PromptTokens));

            if (reply.Status != 200 || reply.FinishReason != "tool_calls")
                break;

            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = null, ["tool_calls"] = reply.ToolCalls.DeepClone() });
            foreach (var call in reply.ToolCalls)
                messages.Add(await RunToolAsync(call, runId, cancel));
            toolCalls += reply.ToolCalls.Count;
            await Task.Delay(shape.ThinkMs, cancel);
        }
        return samples;
    }

    private McpClient? tools;

    private async Task<JsonObject> RunToolAsync(JsonNode? call, string runId, CancellationToken cancel)
    {
        var id = call?["id"]?.GetValue<string>();
        if (shape.ToolsMcp is null)
        {
            await Task.Delay(shape.NextToolPause(), cancel);
            return Conversation.ToolResult(id, shape);
        }

        tools ??= await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions { Endpoint = shape.ToolsMcp, Name = "tools" }),
            cancellationToken: cancel);
        var name = call?["function"]?["name"]?.GetValue<string>() ?? "";
        var raw = call?["function"]?["arguments"]?.GetValue<string>();
        Dictionary<string, object?> arguments;
        try
        {
            arguments = string.IsNullOrWhiteSpace(raw)
                ? []
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(raw)!.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        }
        catch (JsonException)
        {
            return new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = "Error: tool arguments were not valid JSON." };
        }
        var result = await tools.CallToolAsync(name, arguments, cancellationToken: cancel);
        var text = string.Join(Environment.NewLine, result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        return new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = text };
    }

    private async Task<(TurnReply Reply, string? RunId, string? Replica)> SendAsync(
        string runId, JsonArray messages, bool offerTools, CancellationToken cancel)
    {
        var body = new JsonObject
        {
            ["model"] = shape.Model,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["max_tokens"] = 128,
            ["messages"] = messages.DeepClone(),
        };
        if (offerTools)
        {
            body["tools"] = Conversation.Tools();
            body["parallel_tool_calls"] = false;
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (shape.SendRunHeader)
            request.Headers.Add(RunIdHeader, runId);
        request.Headers.Add("x-luftpause-class", shape.Service);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var clock = Stopwatch.StartNew();
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        var servedRun = Header(response, RunIdHeader);
        var replica = Header(response, ReplicaHeader);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancel);
            Console.Error.WriteLine($"{(int)response.StatusCode}: {error[..Math.Min(error.Length, 400)]}");
            return (new TurnReply((int)response.StatusCode, double.NaN, null, [], 0), servedRun, replica);
        }

        var reply = await SseReader.ReadAsync(await response.Content.ReadAsStreamAsync(cancel), clock, cancel);
        return (reply, servedRun, replica);
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
