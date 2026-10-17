using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Luftpause.Runtime;

public sealed class AgentLoop(LlmClient llm, ToolCatalog tools, TimeProvider time, ILogger<AgentLoop> log) : IRunLoop
{
    private const string SingleCallSystem =
        "You are a sub-agent. Use the available tools when they help, one call per reply. " +
        "When you have what you need, answer in a few sentences without calling a tool.";
    private const string ParallelCallSystem =
        "You are a sub-agent. Use the available tools when they help; independent calls may go in one reply. " +
        "When you have what you need, answer in a few sentences without calling a tool.";

    public static IReadOnlyList<JsonObject> InitialMessages(RunSpec spec) =>
    [
        new JsonObject { ["role"] = "system", ["content"] = spec.System ?? (spec.ParallelToolCalls ? ParallelCallSystem : SingleCallSystem) },
        new JsonObject { ["role"] = "user", ["content"] = spec.Task },
    ];

    public async Task<RunOutcome> RunAsync(RunStart start, Action<RunProgress> report, CancellationToken cancel)
    {
        var (runId, spec) = (start.RunId, start.Spec);
        var definitions = Allowed(await tools.DefinitionsAsync(cancel), spec.Tools);
        var messages = new JsonArray([.. start.Messages.Select(m => m.DeepClone())]);
        var work = start.Work;

        while (true)
        {
            var offered = work.ToolCalls < spec.MaxToolCalls ? definitions : [];
            var turnStarted = time.GetTimestamp();
            var reply = await llm.ChatAsync(runId, spec, messages, offered, cancel);
            var took = time.GetElapsedTime(turnStarted);
            work = work.AfterTurn(reply.Usage, took);
            report(new RunProgress(work, []));
            LogTurn(runId, work.Turns, reply, took);
            var calls = offered.Count == 0
                ? []
                : reply.ToolCalls.Count > 0
                ? reply.ToolCalls.Take(spec.ParallelToolCalls ? spec.MaxToolCalls - work.ToolCalls : 1).ToList()
                : TextToolCalls.Recover(reply.Content, offered) is { } recovered ? [Logged(runId, recovered)] : [];
            if (calls.Count == 0)
                return string.IsNullOrWhiteSpace(reply.Content)
                    ? new RunOutcome(RunStatus.Failed, null, "empty answer")
                    : new RunOutcome(RunStatus.Completed, reply.Content, null);

            var reported = messages.Count;
            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = null,
                ["tool_calls"] = new JsonArray([.. calls.Select(c => (JsonNode)c.DeepClone())]),
            });
            var toolsStarted = time.GetTimestamp();
            var results = await Task.WhenAll(calls.Select(c => CallReportedAsync(runId, c, cancel)));
            foreach (var (call, result) in calls.Zip(results))
                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = call["id"]?.GetValue<string>(),
                    ["content"] = result.Text,
                });
            work = work.AfterTools(calls.Count, results.Count(r => r.Failed), time.GetElapsedTime(toolsStarted));
            report(new RunProgress(work, [.. messages.Skip(reported).Select(m => m!.DeepClone().AsObject())]));
        }
    }

    private void LogTurn(string runId, int turn, LlmReply reply, TimeSpan took) =>
        log.LogInformation(
            "turn run={Run} turn={Turn} ms={Ms} prompt={Prompt} cached={Cached} completion={Completion} finish={Finish}",
            runId, turn, (long)took.TotalMilliseconds, reply.Usage.Prompt, reply.Usage.Cached, reply.Usage.Completion, reply.FinishReason);

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
        using var activity = Tracing.Source.StartActivity($"execute_tool {tool}", ActivityKind.Internal, default(ActivityContext), GenAiTags.Tool(call));
        var started = time.GetTimestamp();
        var text = await tools.CallAsync(call, cancel);
        var result = new ToolResult(text, text.StartsWith("Error:", StringComparison.Ordinal));
        if (result.Failed)
            activity?.SetStatus(ActivityStatusCode.Error, text[..Math.Min(text.Length, 200)]);
        log.LogInformation(
            "tool_call run={Run} tool={Tool} ms={Ms} failed={Failed} reason={Reason}",
            runId, tool, (long)time.GetElapsedTime(started).TotalMilliseconds, result.Failed,
            result.Failed ? text[..Math.Min(text.Length, 200)] : "");
        return result;
    }

    private sealed record ToolResult(string Text, bool Failed);
}
