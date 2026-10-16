namespace Luftpause.Scheduling;

public static class WorkingSet
{
    public static SchedulerState Record(SchedulerState state, string replicaId, TokenUsage usage) =>
        state with
        {
            PeakRunTokens = state.PeakRunTokens.SetItem(replicaId,
                Math.Max(state.PeakRunTokens.GetValueOrDefault(replicaId), usage.Prompt + usage.Completion)),
        };

    public static bool Admits(SchedulerState state, Replica replica, TurnRequested request)
    {
        if (request.Class != EngineClass.Generative)
            return true;
        if (state.Runs.GetValueOrDefault(request.RunId) is { } run && HeldOnHealthyReplica(state, run))
            return run.LastReplicaId == replica.Id;
        return ActiveRunsOn(state, replica.Id) < RunsThatFit(state, replica.Id);
    }

    private static bool HeldOnHealthyReplica(SchedulerState state, Run run) =>
        IsActive(run) && state.Replicas.Any(r => r.Id == run.LastReplicaId && state.IsHealthy(r));

    private static bool IsActive(Run run) => run.Phase is Generating or AwaitingTool;

    public static int ActiveRunsOn(SchedulerState state, string replicaId) =>
        state.Runs.Values.Count(r => IsActive(r) && r.LastReplicaId == replicaId);

    public static int RunsThatFit(SchedulerState state, string replicaId) =>
        state.KvPool.TryGetValue(replicaId, out var pool) && state.PeakRunTokens.TryGetValue(replicaId, out var peak)
            ? Math.Max(pool / peak, 1)
            : int.MaxValue;
}
