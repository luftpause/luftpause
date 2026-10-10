namespace Luftpause.Runtime;

public enum RunStatus { Running, Completed, Failed, Cancelled }

public sealed record RunSpec(
    string Task, string Model, string ServiceClass, string? System, int MaxToolCalls, IReadOnlySet<string>? Tools = null, int? MaxTokens = null, bool ParallelToolCalls = false);

public sealed record RunOutcome(RunStatus Status, string? Answer, string? Error, int Turns, int ToolCalls, int FailedToolCalls = 0);

public sealed record RunView(
    string Handle, RunStatus Status, string? Answer, string? Error, int Turns, int ToolCalls, int FailedToolCalls, double ElapsedSeconds);

public sealed class RunRecord(string handle, RunSpec spec, DateTimeOffset started, CancellationTokenSource cancel)
{
    public string Handle { get; } = handle;
    public RunSpec Spec { get; } = spec;
    public DateTimeOffset Started { get; } = started;
    public CancellationTokenSource Cancel { get; } = cancel;
    public Task<RunOutcome> Completion { get; set; } = Task.FromResult(new RunOutcome(RunStatus.Running, null, null, 0, 0));
    public DateTimeOffset? Finished { get; set; }

    public RunView View(DateTimeOffset now)
    {
        var outcome = Completion.IsCompleted ? Completion.Result : new RunOutcome(RunStatus.Running, null, null, 0, 0);
        return new RunView(Handle, outcome.Status, outcome.Answer, outcome.Error, outcome.Turns, outcome.ToolCalls, outcome.FailedToolCalls,
            ((Finished ?? now) - Started).TotalSeconds);
    }
}
