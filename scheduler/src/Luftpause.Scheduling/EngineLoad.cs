namespace Luftpause.Scheduling;

public static class EngineLoad
{
    public const int QueueBuffer = 2;

    public static SchedulerState Apply(SchedulerState state, ReplicaLoadReported load) =>
        state with
        {
            Capacity = state.Capacity.SetItem(load.ReplicaId, Capacity(state, load)),
            KvPool = load.KvTokens is { } kv ? state.KvPool.SetItem(load.ReplicaId, kv) : state.KvPool,
        };

    private static int Capacity(SchedulerState state, ReplicaLoadReported load)
    {
        var inFlight = state.InFlightOn(load.ReplicaId);
        var target = Math.Max(inFlight - load.Waiting + QueueBuffer, 1);
        if (!state.Capacity.TryGetValue(load.ReplicaId, out var capacity) || load.Waiting > QueueBuffer)
            return target;
        var unseen = inFlight - load.Running - load.Waiting;
        return inFlight >= capacity && unseen <= QueueBuffer ? target : capacity;
    }
}
