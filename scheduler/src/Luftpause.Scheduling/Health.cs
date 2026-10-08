namespace Luftpause.Scheduling;

public static class Health
{
    public static SchedulerState Apply(SchedulerState state, ReplicaHealthChanged change) =>
        state with
        {
            Unhealthy = change.Healthy ? state.Unhealthy.Remove(change.ReplicaId) : state.Unhealthy.Add(change.ReplicaId),
        };
}
