namespace Luftpause.Scheduling.Tests.Properties;

public abstract record Command(long AdvanceMs)
{
    protected string After => AdvanceMs == 0 ? "" : $"+{AdvanceMs}ms ";
}

public sealed record RequestTurn(
    long AdvanceMs, int Run, EngineClass Engine, ServiceClass Service, string Model, string Caller, bool Resumed)
    : Command(AdvanceMs)
{
    public override string ToString() =>
        $"{After}Request run{Run} {Engine} {Service} {Model} {Caller}{(Resumed ? " resumed" : "")}";
}

public sealed record CompleteTurn(long AdvanceMs, int Pick, bool ToolCalls, int PromptTokens, int CompletionTokens) : Command(AdvanceMs)
{
    public override string ToString() =>
        $"{After}Complete in-flight #{Pick} {(ToolCalls ? "tool_calls" : "stop")} prompt={PromptTokens} completion={CompletionTokens}";
}

public sealed record AbandonTurn(long AdvanceMs, int Pick, bool Held) : Command(AdvanceMs)
{
    public override string ToString() => $"{After}Abandon {(Held ? "held" : "in-flight")} #{Pick}";
}

public sealed record ReportLoad(long AdvanceMs, int Replica, int Running, int Waiting) : Command(AdvanceMs)
{
    public override string ToString() =>
        $"{After}Load replica #{Replica} running={Running} waiting={Waiting}";
}

public sealed record SetHealth(long AdvanceMs, int Replica, bool Healthy) : Command(AdvanceMs)
{
    public override string ToString() => $"{After}Health replica #{Replica} {(Healthy ? "healthy" : "unhealthy")}";
}

public sealed record EndRun(long AdvanceMs, int Run) : Command(AdvanceMs)
{
    public override string ToString() => $"{After}End run{Run}";
}

public sealed record TickClock(long AdvanceMs) : Command(AdvanceMs)
{
    public override string ToString() => $"{After}Tick";
}

public sealed record SnapshotRun(int Run, int Phase, int? Replica, ServiceClass Service)
{
    public override string ToString() => $"run{Run} phase{Phase} r{(Replica?.ToString() ?? "-")} {Service}";
}

public sealed record SnapshotTurn(int Run, int Replica, string Caller)
{
    public override string ToString() => $"run{Run}@r{Replica} {Caller}";
}

public sealed record ResyncState(long AdvanceMs, SnapshotRun[] Runs, SnapshotTurn[] Turns) : Command(AdvanceMs)
{
    public override string ToString() => $"{After}Resync runs=[{string.Join("; ", Runs.Select(r => r.ToString()))}] turns=[{string.Join("; ", Turns.Select(t => t.ToString()))}]";
}

public sealed record Scenario(Replica[] Replicas, int[]? Capacity, Command[] Commands)
{
    public override string ToString() =>
        string.Join(Environment.NewLine, [
            "",
            $"replicas: {string.Join(", ", Replicas.Select(Describe))}",
            .. Capacity is null ? Array.Empty<string>() : [$"capacity: [{string.Join(", ", Capacity)}]"],
            .. Commands.Select((c, i) => $"  {i}: {c}"),
        ]);

    private static string Describe(Replica r) =>
        $"{r.Id} {r.Class} [{(r.Models is null ? "*" : string.Join(",", r.Models))}]";
}
