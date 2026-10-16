namespace Luftpause.Scheduling;

public static class WorkingSet
{
    public static bool Admits(SchedulerState state, Replica replica, TurnRequested request) =>
        request.Class != EngineClass.Generative
        || state.Runs.GetValueOrDefault(request.RunId) is not { } run
        || !(IsActive(run) || request.Resumed)
        || !OnHealthyReplica(state, run)
        || run.LastReplicaId == replica.Id;

    public static int ActiveRunsOn(SchedulerState state, string replicaId) =>
        state.Runs.Values.Count(r => IsActive(r) && r.LastReplicaId == replicaId);

    private static bool OnHealthyReplica(SchedulerState state, Run run) =>
        state.Replicas.Any(r => r.Id == run.LastReplicaId && state.IsHealthy(r));

    private static bool IsActive(Run run) => run.Phase is Generating or AwaitingTool;
}
