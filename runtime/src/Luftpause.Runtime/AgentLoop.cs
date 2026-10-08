using System.Text.Json.Nodes;

namespace Luftpause.Runtime;

public sealed class AgentLoop(LlmClient llm, ToolCatalog tools, TimeProvider time, ILogger<AgentLoop> log)
{
    private const int MaxTurns = 16;
    private const string DefaultSystem =
        "You are a sub-agent. Use the available tools when they help, one call per reply. " +
        "When you have what you need, answer in a few sentences without calling a tool.";

    public async Task<RunOutcome> RunAsync(string runId, RunSpec spec, CancellationToken cancel)
    {
        var definitions = Allowed(await tools.DefinitionsAsync(cancel), spec.Tools);
        var messages = new JsonArray(
            new JsonObject { ["role"] = "system", ["content"] = spec.System ?? DefaultSystem },
            new JsonObject { ["role"] = "user", ["content"] = spec.Task });
        var toolCalls = 0;
        var failedToolCalls = 0;

        for (var turn = 1; turn <= MaxTurns; turn++)
        {
            var offered = toolCalls < spec.MaxToolCalls ? definitions : [];
            var reply = await llm.ChatAsync(runId, spec, messages, offered, cancel);
            var calls = reply.ToolCalls.Count > 0
                ? reply.ToolCalls.Take(1).ToList()
                : TextToolCalls.Recover(reply.Content, offered) is { } recovered ? [Logged(runId, recovered)] : [];
            if (calls.Count == 0)
                return string.IsNullOrWhiteSpace(reply.Content)
                    ? new RunOutcome(RunStatus.Failed, null, "empty answer", turn, toolCalls, failedToolCalls)
                    : new RunOutcome(RunStatus.Completed, reply.Content, null, turn, toolCalls, failedToolCalls);

            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = null,
                ["tool_calls"] = new JsonArray([.. calls.Select(c => (JsonNode)c.DeepClone())]),
            });
            var results = await Task.WhenAll(calls.Select(c => CallReportedAsync(runId, c, cancel)));
            foreach (var (call, result) in calls.Zip(results))
                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = call["id"]?.GetValue<string>(),
                    ["content"] = result.Text,
                });
            toolCalls += calls.Count;
            failedToolCalls += results.Count(r => r.Failed);
        }
        return new RunOutcome(RunStatus.Failed, null, $"no answer after {MaxTurns} turns", MaxTurns, toolCalls, failedToolCalls);
    }

    private JsonObject Logged(string runId, JsonObject recovered)
    {
        log.LogInformation("tool_call_recovered run={Run} tool={Tool}", runId, recovered["function"]?["name"]?.GetValue<string>());
        return recovered;
    }

    private static JsonArray Allowed(JsonArray definitions, IReadOnlySet<string>? names) =>
        names is null
            ? definitions
            : [.. definitions.Where(d => names.Contains(d?["function"]?["name"]?.GetValue<string>() ?? "")).Select(d => d!.DeepClone())];

    private async Task<ToolResult> CallReportedAsync(string runId, JsonObject call, CancellationToken cancel)
    {
        var tool = call["function"]?["name"]?.GetValue<string>() ?? "";
        var started = time.GetTimestamp();
        var text = await tools.CallAsync(call, cancel);
        var result = new ToolResult(text, text.StartsWith("Error:", StringComparison.Ordinal));
        log.LogInformation(
            "tool_call run={Run} tool={Tool} ms={Ms} failed={Failed} reason={Reason}",
            runId, tool, (long)time.GetElapsedTime(started).TotalMilliseconds, result.Failed,
            result.Failed ? text[..Math.Min(text.Length, 200)] : "");
        return result;
    }

    private sealed record ToolResult(string Text, bool Failed);
}
