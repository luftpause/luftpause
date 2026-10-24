using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Luftpause.Runtime;

public sealed class RuntimeDefaults
{
    public required string Model { get; init; }
    public string? DecideModel { get; init; }
}

[McpServerToolType]
public sealed class LuftpauseTools(RunRegistry runs, LlmClient llm, ToolCatalog catalog, RuntimeDefaults defaults, TimeProvider time, ILogger<LuftpauseTools> log)
{
    [McpServerTool(Name = "spawn", Destructive = false, Idempotent = false, OpenWorld = true), Description(
        "Start a sub-agent run on the pooled models and return {handle} at once, without waiting for it; use run instead to start one and block until it finishes. " +
        "The sub-agent loops over model turns and calls the configured tool servers (list them with tools). " +
        "Collect results with await, look at one run with status, stop it with cancel. " +
        "A tool call that needs approval pauses the run as AwaitingApproval until it is answered in await or with approve. " +
        "An unknown parent handle or invalid arguments fail the call; a run that later fails, runs out of budget or is cancelled keeps its handle and reports Failed or Cancelled with an error.")]
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
        [Description("Handle of the run this one belongs to; cancelling the parent cancels it.")] string? parent = null,
        [Description("Prompt plus completion tokens this run and every run under it may use; over it, the subtree is cancelled.")] long? budgetTokens = null,
        [Description("Tool calls this run and every run under it may make; over it, the subtree is cancelled.")] int? budgetToolCalls = null,
        [Description("Wall-clock seconds this run may take, its subtree included; when they pass, the subtree is cancelled.")] double? budgetSeconds = null,
        [Description("Tools whose every call waits for the user's approval, on top of tools their server marks destructive.")] string[]? approveTools = null) =>
        new { handle = Start(server, task, model, serviceClass, system, maxToolCalls, tools, maxTokens, parallelToolCalls, parent,
            budgetTokens, budgetToolCalls, budgetSeconds, approveTools) };

    [McpServerTool(Name = "run", Destructive = false, Idempotent = false, OpenWorld = true), Description(
        "Spawn a sub-agent run and wait in the same call until it finishes; takes the same arguments as spawn and returns the run as await does. " +
        "Clients that opt into MCP Tasks get a task to poll instead, and an approval inside the run arrives as the task's input request. " +
        "Other clients are asked approvals the way await asks them; without elicitation the call returns the run with status AwaitingApproval. Cancelling the call cancels the run.")]
    public async Task<RunView> Run(
        McpServer server,
        RequestContext<CallToolRequestParams> context,
        IProgress<ProgressNotificationValue> progress,
        [Description("What the sub-agent should do.")] string task,
        [Description("Model to run on; defaults to the runtime's model.")] string? model = null,
        [Description("interactive (default) or batch. Batch runs yield to interactive ones.")] string serviceClass = "interactive",
        [Description("Optional system prompt for the sub-agent.")] string? system = null,
        [Description("Tool calls allowed before the sub-agent must answer without tools.")] int maxToolCalls = 8,
        [Description("Names of the tools this run may use; all configured tools when omitted.")] string[]? tools = null,
        [Description("Longest reply per model turn, in tokens; 0 leaves it to the engine. Engines that reserve memory per request (SGLang) batch far fewer requests without it.")] int maxTokens = 256,
        [Description("Let the model ask for several tool calls in one reply, run at once. Off by default: Llama 3.x takes one call per reply.")] bool parallelToolCalls = false,
        [Description("Handle of the run this one belongs to; cancelling the parent cancels it.")] string? parent = null,
        [Description("Prompt plus completion tokens this run and every run under it may use; over it, the subtree is cancelled.")] long? budgetTokens = null,
        [Description("Tool calls this run and every run under it may make; over it, the subtree is cancelled.")] int? budgetToolCalls = null,
        [Description("Wall-clock seconds this run may take, its subtree included; when they pass, the subtree is cancelled.")] double? budgetSeconds = null,
        [Description("Tools whose every call waits for the user's approval, on top of tools their server marks destructive.")] string[]? approveTools = null,
        CancellationToken cancellationToken = default)
    {
        var handle = context.Params?.RequestState is { } resumed
            ? runs.Find(resumed)?.Handle ?? throw new McpException($"unknown run '{resumed}' in requestState")
            : Start(server, task, model, serviceClass, system, maxToolCalls, tools, maxTokens, parallelToolCalls, parent,
                budgetTokens, budgetToolCalls, budgetSeconds, approveTools);
        ApprovalDelivery.Answer(runs, [handle], context.Params?.InputResponses, log);
        try
        {
            var views = await runs.AwaitAsync([handle], true, Timeout.InfiniteTimeSpan, new AwaitProgress(progress),
                ApprovalDelivery.Asker(server, context, ApprovalDelivery.TaskOptIn(context), handle), cancellationToken);
            return views.Single();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            runs.Cancel(handle);
            throw;
        }
    }

    private string Start(
        McpServer server, string task, string? model, string serviceClass, string? system, int maxToolCalls, string[]? tools, int maxTokens,
        bool parallelToolCalls, string? parent, long? budgetTokens, int? budgetToolCalls, double? budgetSeconds, string[]? approveTools)
    {
        var allowed = tools is { Length: > 0 } ? tools.ToHashSet(StringComparer.Ordinal) : null;
        try
        {
            var spec = new RunSpec(task, model ?? defaults.Model, serviceClass, system, Math.Max(maxToolCalls, 0), allowed, maxTokens > 0 ? maxTokens : null, parallelToolCalls, server.SessionId,
                BudgetLimits.Of(budgetTokens, budgetToolCalls, budgetSeconds), approveTools is { Length: > 0 } ? approveTools.ToHashSet(StringComparer.Ordinal) : null);
            return runs.Spawn(spec, parent).Handle;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            throw new McpException(e.Message);
        }
    }

    [McpServerTool(Name = "tools", ReadOnly = true, Idempotent = true), Description(
        "List the tools sub-agents can use, with the server each comes from. Pass names to spawn(tools: [...]) to restrict a run.")]
    public Task<IReadOnlyList<ToolInfo>> Tools(CancellationToken cancellationToken = default) => catalog.DescribeAsync(cancellationToken);

    [McpServerTool(Name = "await", Destructive = false, OpenWorld = false), Description(
        "Wait for runs to finish. mode=all waits for every handle, mode=any returns when at least one is done; use status to look without waiting. " +
        "Returns every known handle's state, with the same fields as status, including runs still in progress after the timeout; unknown handles are left out. " +
        "failedToolCalls counts tool calls that returned an error; when it equals toolCalls, the answer came from the model's own memory, not from tools. " +
        "A run about to call a destructive tool waits for approval: clients with elicitation are asked here, " +
        "others get the run back with status AwaitingApproval and pendingApproval {tool, arguments} and answer with approve.")]
    public Task<IReadOnlyList<RunView>> Await(
        McpServer server,
        RequestContext<CallToolRequestParams> context,
        [Description("Handles returned by spawn.")] string[] handles,
        IProgress<ProgressNotificationValue> progress,
        [Description("all (default) or any.")] string mode = "all",
        [Description("Longest time to wait, in seconds.")] int timeoutSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        ApprovalDelivery.Answer(runs, handles, context.Params?.InputResponses, log);
        return runs.AwaitAsync(handles, !string.Equals(mode, "any", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 0, 600)), new AwaitProgress(progress),
            ApprovalDelivery.Asker(server, context, inTask: false, requestState: null), cancellationToken);
    }

    [McpServerTool(Name = "approve", Idempotent = true, OpenWorld = false), Description(
        "Answer a run waiting with status AwaitingApproval when the client was not asked through elicitation: " +
        "allow=true runs the pending tool call, allow=false tells the sub-agent the user declined it. " +
        "Find waiting runs with await or status, whose pendingApproval shows the tool and its arguments. " +
        "Returns {handle, decided}; decided is false when the handle is unknown or the run has no pending approval.")]
    public object Approve(
        [Description("Handle of the waiting run.")] string handle,
        [Description("true to run the tool call, false to decline it.")] bool allow) =>
        new { handle, decided = runs.Decide(handle, allow) };

    [McpServerTool(Name = "status", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
        "Read one run's current state at once, without waiting; use await to block until runs finish. " +
        "Returns handle, status (Running, AwaitingApproval, Completed, Failed or Cancelled), answer once Completed, error once Failed or Cancelled, " +
        "parent and children handles, turns, toolCalls, failedToolCalls, prompt, cached and completion tokens, elapsedSeconds, budget when one was set, " +
        "and pendingApproval {tool, arguments} while AwaitingApproval. An unknown handle returns {handle, error: \"unknown handle\"}.")]
    public object Status([Description("Handle returned by spawn.")] string handle) =>
        runs.Find(handle) is { } record ? runs.View(record, time.GetUtcNow()) : (object)new { handle, error = "unknown handle" };

    [McpServerTool(Name = "cancel", Destructive = true, Idempotent = true, OpenWorld = false), Description(
        "Stop a run and every run spawned under it. It cannot be undone: each stopped run ends as Cancelled without an answer. " +
        "Returns {handle, cancelled}; cancelled is true when the run or one of its descendants was still in progress, " +
        "false when the handle is unknown or everything had already finished, so calling it again is harmless. " +
        "Use status or await to read the final state.")]
    public object Cancel([Description("Handle returned by spawn.")] string handle) =>
        new { handle, cancelled = runs.Cancel(handle) };

    [McpServerTool(Name = "decide", ReadOnly = true, OpenWorld = false), Description(
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
