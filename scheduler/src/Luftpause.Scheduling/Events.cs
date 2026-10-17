namespace Luftpause.Scheduling;

public abstract record SchedulerEvent(long AtUnixMs);

public abstract record TurnEvent(string TurnId, string RunId, long AtUnixMs) : SchedulerEvent(AtUnixMs);

public sealed record TurnRequested(
    string TurnId,
    string RunId,
    EngineClass Class,
    string Model,
    bool Resumed,
    long AtUnixMs,
    ServiceClass Service = ServiceClass.Interactive,
    string Caller = "")
    : TurnEvent(TurnId, RunId, AtUnixMs);

public sealed record TurnStarted(string TurnId, string RunId, string ReplicaId, long AtUnixMs)
    : TurnEvent(TurnId, RunId, AtUnixMs);

public sealed record FirstToken(string TurnId, string RunId, long AtUnixMs)
    : TurnEvent(TurnId, RunId, AtUnixMs);

public sealed record TokenUsage(int Prompt, int Completion, int Cached);

public sealed record TurnCompleted(
    string TurnId, string RunId, string FinishReason, TokenUsage Usage, bool Failed, long AtUnixMs)
    : TurnEvent(TurnId, RunId, AtUnixMs)
{
    public bool OpensToolPause => !Failed && FinishReason == "tool_calls";
}

public sealed record TurnAbandoned(string TurnId, string RunId, long AtUnixMs)
    : TurnEvent(TurnId, RunId, AtUnixMs);

public sealed record RunEnded(string RunId, long AtUnixMs) : SchedulerEvent(AtUnixMs);

public sealed record ReplicaHealthChanged(string ReplicaId, bool Healthy, long AtUnixMs) : SchedulerEvent(AtUnixMs);

public sealed record ReplicaLoadReported(string ReplicaId, int Running, int Waiting, long AtUnixMs) : SchedulerEvent(AtUnixMs);

public sealed record Tick(long AtUnixMs) : SchedulerEvent(AtUnixMs);

public sealed record StateResynced(Snapshot Snapshot, long AtUnixMs) : SchedulerEvent(AtUnixMs);
