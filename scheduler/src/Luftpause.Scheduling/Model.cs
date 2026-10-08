using System.Collections.Immutable;

namespace Luftpause.Scheduling;

public enum EngineClass { Generative, SinglePass }

public enum ServiceClass { Interactive, Batch }

public sealed record Replica(
    string Id,
    string BaseUrl,
    EngineClass Class,
    string Path = "/v1/chat/completions",
    string[]? Models = null)
{
    public bool Serves(string model) => Models is not { Length: > 0 } || Models.Contains(model, StringComparer.Ordinal);
}

public sealed record Turn(string TurnId, string RunId, string ReplicaId, long StartedUnixMs);

public abstract record RunPhase;

public sealed record Generating(string TurnId) : RunPhase;

public sealed record AwaitingTool(long SinceUnixMs) : RunPhase;

public sealed record Idle(long SinceUnixMs) : RunPhase;

public sealed record Run(
    string RunId,
    RunPhase Phase,
    string? LastReplicaId,
    int Turns,
    long LastActivityUnixMs,
    ServiceClass Service = ServiceClass.Interactive);

public sealed record SchedulerState(
    ImmutableArray<Replica> Replicas,
    ImmutableDictionary<string, int> InFlight,
    ImmutableDictionary<string, Turn> Turns,
    ImmutableDictionary<string, Run> Runs,
    long Routed,
    ImmutableList<TurnRequested> Waiting,
    ImmutableHashSet<string> Unhealthy,
    ImmutableDictionary<string, int> Capacity)
{
    public static SchedulerState Initial(IEnumerable<Replica> replicas)
    {
        var all = replicas.ToImmutableArray();
        return new SchedulerState(
            all,
            all.ToImmutableDictionary(r => r.Id, _ => 0),
            ImmutableDictionary<string, Turn>.Empty,
            ImmutableDictionary<string, Run>.Empty,
            0,
            ImmutableList<TurnRequested>.Empty,
            ImmutableHashSet<string>.Empty,
            ImmutableDictionary<string, int>.Empty);
    }

    public int InFlightOn(string replicaId) => InFlight.GetValueOrDefault(replicaId);

    public int InteractivePausedOn(string replicaId) =>
        Runs.Values.Count(r => r is { Service: ServiceClass.Interactive, Phase: AwaitingTool } && r.LastReplicaId == replicaId);

    public ImmutableArray<Replica> ReplicasOf(EngineClass engineClass) =>
        Replicas.Where(r => r.Class == engineClass).ToImmutableArray();

    public bool IsHealthy(Replica replica) => !Unhealthy.Contains(replica.Id);

    public SchedulerState WithRun(Run run) => this with { Runs = Runs.SetItem(run.RunId, run) };
}
