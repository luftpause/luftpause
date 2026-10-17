using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Luftpause.Runtime;

public sealed class RuntimeDefaults
{
    public required string Model { get; init; }
    public string? DecideModel { get; init; }
}

[McpServerToolType]
public sealed class LuftpauseTools(RunRegistry runs, LlmClient llm, ToolCatalog catalog, RuntimeDefaults defaults, TimeProvider time)
{
    [McpServerTool(Name = "spawn"), Description(
        "Start a sub-agent run on the pooled local models and return its handle immediately. " +
        "The run uses the configured tool servers. Collect the result later with await.")]
    public object Spawn(
        McpServer server,
        [Description("What the sub-agent should do.")] string task,
        [Description("Model to run on; defaults to the runtime's model.")] string? model = null,
        [Description("interactive (default) or batch. Batch runs yield to interactive ones.")] string serviceClass = "interactive",
        [Description("Optional system prompt for the sub-agent.")] string? system = null,
        [Description("Tool calls allowed before the sub-agent must answer without tools.")] int maxToolCalls = 8,
        [Description("Names of the tools this run may use; all configured tools when omitted.")] string[]? tools = null,
        [Description("Longest reply per model turn, in tokens; 0 leaves it to the engine. Engines that reserve memory per request (SGLang) batch far fewer requests without it.")] int maxTokens = 256,
        [Description("Let the model ask for several tool calls in one reply, run at once. Off by default: Llama 3.x takes one call per reply.")] bool parallelToolCalls = false,
        [Description("Handle of the run this one belongs to; cancelling the parent cancels it.")] string? parent = null)
    {
        var allowed = tools is { Length: > 0 } ? tools.ToHashSet(StringComparer.Ordinal) : null;
        var spec = new RunSpec(task, model ?? defaults.Model, serviceClass, system, Math.Max(maxToolCalls, 0), allowed, maxTokens > 0 ? maxTokens : null, parallelToolCalls, server.SessionId);
        try
        {
            return new { handle = runs.Spawn(spec, parent).Handle };
        }
        catch (InvalidOperationException e)
        {
            throw new McpException(e.Message);
        }
    }

    [McpServerTool(Name = "tools", ReadOnly = true), Description(
        "List the tools sub-agents can use, with the server each comes from. Pass names to spawn(tools: [...]) to restrict a run.")]
    public Task<IReadOnlyList<ToolInfo>> Tools(CancellationToken cancellationToken = default) => catalog.DescribeAsync(cancellationToken);

    [McpServerTool(Name = "await"), Description(
        "Wait for runs to finish. mode=all waits for every handle, mode=any returns when at least one is done. " +
        "Returns the state of every handle when it returns, including runs still in progress after the timeout. " +
        "failedToolCalls counts tool calls that returned an error; when it equals toolCalls, the answer came from the model's own memory, not from tools.")]
    public Task<IReadOnlyList<RunView>> Await(
        [Description("Handles returned by spawn.")] string[] handles,
        IProgress<ProgressNotificationValue> progress,
        [Description("all (default) or any.")] string mode = "all",
        [Description("Longest time to wait, in seconds.")] int timeoutSeconds = 60,
        CancellationToken cancellationToken = default) =>
        runs.AwaitAsync(handles, !string.Equals(mode, "any", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 0, 600)), new AwaitProgress(progress), cancellationToken);

    [McpServerTool(Name = "status", ReadOnly = true), Description("Current state of a run, without waiting.")]
    public object Status([Description("Handle returned by spawn.")] string handle) =>
        runs.Find(handle) is { } record ? runs.View(record, time.GetUtcNow()) : (object)new { handle, error = "unknown handle" };

    [McpServerTool(Name = "cancel"), Description("Cancel a run that is still in progress, and every run spawned under it.")]
    public object Cancel([Description("Handle returned by spawn.")] string handle) =>
        new { handle, cancelled = runs.Cancel(handle) };

    [McpServerTool(Name = "decide", ReadOnly = true), Description(
        "One-pass typed decision: returns labels with probabilities for the input. " +
        "Without labels, a single-pass model answers with its own label set. " +
        "With labels, a generative model scores them by the logprob of its first answer token.")]
    public async Task<string> Decide(
        [Description("Text or state to classify.")] string input,
        [Description("2 to 20 labels to choose from, each with a distinct first word; omit to use the single-pass model's labels.")] string[]? labels = null,
        [Description("Model; defaults to the runtime's decision model without labels and its generative model with labels.")] string? model = null,
        CancellationToken cancellationToken = default)
    {
        if (labels is null)
            return await llm.DecideAsync(model ?? defaults.DecideModel ?? "", input, cancellationToken);
        try
        {
            return await LabelDecision.DecideAsync(llm, model ?? defaults.Model, input, labels, cancellationToken);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            throw new McpException(e.Message);
        }
    }
}
