using System.Text.Json.Nodes;

namespace Luftpause.Runtime;

public enum RunStatus { Running, Completed, Failed, Cancelled }

public sealed record RunSpec(
    string Task, string Model, string ServiceClass, string? System, int MaxToolCalls, IReadOnlySet<string>? Tools = null, int? MaxTokens = null, bool ParallelToolCalls = false, string? Caller = null);

public sealed record RunOutcome(RunStatus Status, string? Answer, string? Error);

public sealed record RunView(
    string Handle, string? Parent, IReadOnlyList<string> Children, RunStatus Status, string? Answer, string? Error, int Turns, int ToolCalls, int FailedToolCalls, double ElapsedSeconds,
    long PromptTokens, long? CachedTokens, long CompletionTokens, double TurnSeconds, double ToolSeconds);

public sealed class RunRecord(string handle, RunSpec spec, DateTimeOffset started, CancellationTokenSource cancel, string? parent = null)
{
    private volatile RunWork work = RunWork.None;
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile IReadOnlyList<JsonObject> messages = AgentLoop.InitialMessages(spec);

    public string Handle { get; } = handle;
    public string? Parent { get; } = parent;
    public RunSpec Spec { get; } = spec;
    public DateTimeOffset Started { get; } = started;
    public CancellationTokenSource Cancel { get; } = cancel;
    public Task<RunOutcome> Completion { get; set; } = Task.FromResult(new RunOutcome(RunStatus.Running, null, null));
    public DateTimeOffset? Finished { get; set; }

    public RunWork Work
    {
        get => work;
        set
        {
            work = value;
            Interlocked.Exchange(ref changed, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        }
    }

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
            ((Finished ?? now) - Started).TotalSeconds,
            done.PromptTokens, done.CachedTokens, done.CompletionTokens, done.TurnSeconds, done.ToolSeconds);
    }
}
