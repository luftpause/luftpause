using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Luftpause.Runtime;

public enum RunStatus { Running, Completed, Failed, Cancelled, AwaitingApproval }

public sealed record RunSpec(
    string Task, string Model, string ServiceClass, string? System, int MaxToolCalls, IReadOnlySet<string>? Tools = null, int? MaxTokens = null, bool ParallelToolCalls = false, string? Caller = null, BudgetLimits? Budget = null,
    IReadOnlySet<string>? ApproveTools = null);

public sealed record RunOutcome(RunStatus Status, string? Answer, string? Error);

public sealed record RunView(
    string Handle, string? Parent, IReadOnlyList<string> Children, RunStatus Status, string? Answer, string? Error, int Turns, int ToolCalls, int FailedToolCalls, double ElapsedSeconds,
    long PromptTokens, long? CachedTokens, long CompletionTokens, double TurnSeconds, double ToolSeconds, BudgetView? Budget = null,
    ApprovalRequest? PendingApproval = null);

public sealed class RunRecord(string handle, RunSpec spec, DateTimeOffset started, CancellationTokenSource cancel, string? parent = null)
{
    private volatile RunWork work = RunWork.None;
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile IReadOnlyList<JsonObject> messages = AgentLoop.InitialMessages(spec);
    private BudgetOverrun? exceeded;
    private string? cancelReason;
    private volatile PendingApproval? pending;
    private ImmutableList<RunApprovalDecided> decisions = [];

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
            Signal();
        }
    }

    public PendingApproval? Pending
    {
        get => pending;
        set
        {
            pending = value;
            Signal();
        }
    }

    public IReadOnlyList<RunApprovalDecided> Decisions
    {
        init => decisions = [.. value];
    }

    public bool? TakeDecision(ApprovalRequest request)
    {
        var earlier = decisions.Find(request.Matches);
        if (earlier is null)
            return null;
        decisions = decisions.Remove(earlier);
        return earlier.Allow;
    }

    private void Signal() =>
        Interlocked.Exchange(ref changed, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

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
        var waiting = Completion.IsCompleted || pending is not { Open: true } ? null : pending;
        var outcome = Completion.IsCompleted
            ? Completion.Result
            : new RunOutcome(waiting is null ? RunStatus.Running : RunStatus.AwaitingApproval, null, null);
        var done = work;
        return new RunView(Handle, Parent, children, outcome.Status, outcome.Answer, outcome.Error, done.Turns, done.ToolCalls, done.FailedToolCalls,
            ElapsedSeconds(now),
            done.PromptTokens, done.CachedTokens, done.CompletionTokens, done.TurnSeconds, done.ToolSeconds, PendingApproval: waiting?.Request);
    }
}
