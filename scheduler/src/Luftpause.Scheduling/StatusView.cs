namespace Luftpause.Scheduling;

public sealed record ReplicaStatus(
    string Id, EngineClass Class, bool Healthy, int InFlight, int? Capacity, int ActiveRuns);

public sealed record HeldGroup(ServiceClass Class, string Caller, int Count, double OldestWaitSeconds);

public sealed record RunGroup(string Phase, ServiceClass Class, int Count);

public sealed record StatusTotals(int Replicas, int HealthyReplicas, int InFlight, int Held, int Runs);

public sealed record StatusView(
    long AtUnixMs,
    IReadOnlyList<ReplicaStatus> Replicas,
    IReadOnlyList<HeldGroup> Held,
    IReadOnlyList<RunGroup> Runs,
    StatusTotals Totals)
{
    public static StatusView From(SchedulerState state, long nowUnixMs)
    {
        var replicas = state.Replicas.Select(r => ReplicaOf(state, r)).ToList();
        return new StatusView(
            nowUnixMs,
            replicas,
            HeldOf(state, nowUnixMs),
            RunsOf(state),
            new StatusTotals(
                replicas.Count,
                replicas.Count(r => r.Healthy),
                state.Turns.Count,
                state.Waiting.Count,
                state.Runs.Count));
    }

    private static ReplicaStatus ReplicaOf(SchedulerState state, Replica replica) =>
        new(
            replica.Id,
            replica.Class,
            state.IsHealthy(replica),
            state.InFlightOn(replica.Id),
            state.Capacity.TryGetValue(replica.Id, out var capacity) ? capacity : null,
            state.Runs.Values.Count(run => run.LastReplicaId == replica.Id && run.Phase is not Idle));

    private static IReadOnlyList<HeldGroup> HeldOf(SchedulerState state, long nowUnixMs) =>
        state.Waiting
            .GroupBy(w => (w.Service, Caller: WaitQueue.CallerOf(state, w)))
            .Select(g => new HeldGroup(g.Key.Service, g.Key.Caller, g.Count(), (nowUnixMs - g.Min(w => w.AtUnixMs)) / 1000.0))
            .OrderBy(g => g.Class)
            .ThenBy(g => g.Caller, StringComparer.Ordinal)
            .ToList();

    private static IReadOnlyList<RunGroup> RunsOf(SchedulerState state) =>
        state.Runs.Values
            .GroupBy(r => (Phase: PhaseName(r.Phase), r.Service))
            .Select(g => new RunGroup(g.Key.Phase, g.Key.Service, g.Count()))
            .OrderBy(g => g.Phase, StringComparer.Ordinal)
            .ThenBy(g => g.Class)
            .ToList();

    private static string PhaseName(RunPhase phase) => phase switch
    {
        Generating => nameof(Generating),
        AwaitingTool => nameof(AwaitingTool),
        Idle => nameof(Idle),
        _ => throw new System.Diagnostics.UnreachableException(phase.GetType().Name),
    };
}
