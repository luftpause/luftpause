namespace Luftpause.Scheduling.Tests.Properties;

public abstract record Command(long AdvanceMs)
{
    protected string After => AdvanceMs == 0 ? "" : $"+{AdvanceMs}ms ";
}

public sealed record RequestTurn(
    long AdvanceMs, int Run, EngineClass Engine, ServiceClass Service, string Model, string Caller, int RequestBytes, bool Resumed)
    : Command(AdvanceMs)
{
    public override string ToString() =>
        $"{After}Request run{Run} {Engine} {Service} {Model} {Caller} {RequestBytes}B{(Resumed ? " resumed" : "")}";
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

public sealed record ReportLoad(long AdvanceMs, int Replica, int Running, int Waiting, int? KvTokens) : Command(AdvanceMs)
{
    public override string ToString() =>
        $"{After}Load replica #{Replica} running={Running} waiting={Waiting}{(KvTokens is { } kv ? $" kv={kv}" : "")}";
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

public sealed record Scenario(Replica[] Replicas, int[]? Capacity, int[]? KvPool, Command[] Commands)
{
    public override string ToString() =>
        string.Join(Environment.NewLine, [
            "",
            $"replicas: {string.Join(", ", Replicas.Select(Describe))}",
            .. Capacity is null ? Array.Empty<string>() : [$"capacity: [{string.Join(", ", Capacity)}]"],
            .. KvPool is null ? Array.Empty<string>() : [$"kv pool: [{string.Join(", ", KvPool)}]"],
            .. Commands.Select((c, i) => $"  {i}: {c}"),
        ]);

    private static string Describe(Replica r) =>
        $"{r.Id} {r.Class} [{(r.Models is null ? "*" : string.Join(",", r.Models))}]";
}
