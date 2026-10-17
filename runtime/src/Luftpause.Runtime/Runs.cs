using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Luftpause.Runtime;

public enum RunStatus { Running, Completed, Failed, Cancelled }

public sealed record RunSpec(
    string Task, string Model, string ServiceClass, string? System, int MaxToolCalls, IReadOnlySet<string>? Tools = null, int? MaxTokens = null, bool ParallelToolCalls = false, string? Caller = null, BudgetLimits? Budget = null);

public sealed record RunOutcome(RunStatus Status, string? Answer, string? Error);

public sealed record RunView(
    string Handle, string? Parent, IReadOnlyList<string> Children, RunStatus Status, string? Answer, string? Error, int Turns, int ToolCalls, int FailedToolCalls, double ElapsedSeconds,
    long PromptTokens, long? CachedTokens, long CompletionTokens, double TurnSeconds, double ToolSeconds, BudgetView? Budget = null);

public sealed class RunRecord(string handle, RunSpec spec, DateTimeOffset started, CancellationTokenSource cancel, string? parent = null)
{
    private volatile RunWork work = RunWork.None;
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile IReadOnlyList<JsonObject> messages = AgentLoop.InitialMessages(spec);
    private BudgetOverrun? exceeded;
    private string? cancelReason;

    public string Handle { get; } = handle;
    public string? Parent { get; } = parent;
    public RunSpec Spec { get; } = spec;
    public DateTimeOffset Started { get; } = started;
    public CancellationTokenSource Cancel { get; } = cancel;
    public Task<RunOutcome> Completion { get; set; } = Task.FromResult(new RunOutcome(RunStatus.Running, null, null));
    public DateTimeOffset? Finished { get; set; }
    public ActivityContext Trace { get; set; }

    public RunWork Work
    {
        get => work;
        set
        {
            work = value;
            Interlocked.Exchange(ref changed, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        }
    }

    public BudgetOverrun? Exceeded
    {
        get => Volatile.Read(ref exceeded);
        init => exceeded = value;
    }

    public string? CancelReason => Volatile.Read(ref cancelReason);

    public BudgetOverrun Exceed(BudgetOverrun overrun) => Interlocked.CompareExchange(ref exceeded, overrun, null) ?? overrun;

    public void Stop(string reason)
    {
        Interlocked.CompareExchange(ref cancelReason, reason, null);
        Cancel.Cancel();
    }

    public double ElapsedSeconds(DateTimeOffset now) => ((Finished ?? now) - Started).TotalSeconds;

    public Task WorkChanged => Volatile.Read(ref changed).Task;

    public IReadOnlyList<JsonObject> Messages
    {
        get => messages;
        set => messages = value;
    }

    public RunView View(DateTimeOffset now, IReadOnlyList<string> children)
    {
        var outcome = Completion.IsCompleted ? Completion.Result : new RunOutcome(RunStatus.Running, null, null);
        var done = work;
        return new RunView(Handle, Parent, children, outcome.Status, outcome.Answer, outcome.Error, done.Turns, done.ToolCalls, done.FailedToolCalls,
            ElapsedSeconds(now),
            done.PromptTokens, done.CachedTokens, done.CompletionTokens, done.TurnSeconds, done.ToolSeconds);
    }
}
