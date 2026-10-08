namespace Luftpause.Scheduling;

public static class EngineLoad
{
    public static SchedulerState Apply(SchedulerState state, ReplicaLoadReported load)
    {
        if (load.Waiting > 0)
            return state with { Capacity = state.Capacity.SetItem(load.ReplicaId, load.Running) };

        return state.Capacity.TryGetValue(load.ReplicaId, out var capacity) && state.InFlightOn(load.ReplicaId) >= capacity
            ? state with { Capacity = state.Capacity.Remove(load.ReplicaId) }
            : state;
    }
}
