namespace Luftpause.Scheduling;

public static class TurnLifecycle
{
    public static SchedulerState Start(SchedulerState state, TurnRequested request, Replica replica)
    {
        var run = state.Runs.GetValueOrDefault(request.RunId)
            ?? new Run(request.RunId, new Idle(request.AtUnixMs), null, 0, request.AtUnixMs, request.AtUnixMs, Caller: request.Caller);

        return state with
        {
            InFlight = state.InFlight.SetItem(replica.Id, state.InFlightOn(replica.Id) + 1),
            Turns = state.Turns.SetItem(request.TurnId, KvBudget.Turn(state, request, replica)),
            Runs = state.Runs.SetItem(request.RunId, run with
            {
                Phase = new Generating(request.TurnId),
                LastReplicaId = replica.Id,
                Turns = run.Turns + 1,
                LastActivityUnixMs = request.AtUnixMs,
                Service = request.Service,
            }),
            Routed = state.Routed + 1,
        };
    }

    public static SchedulerState StartDetached(SchedulerState state, TurnRequested request, Replica replica) =>
        state with
        {
            InFlight = state.InFlight.SetItem(replica.Id, state.InFlightOn(replica.Id) + 1),
            Turns = state.Turns.SetItem(request.TurnId, KvBudget.Turn(state, request, replica)),
            Routed = state.Routed + 1,
        };

    public static SchedulerState Release(SchedulerState state, string turnId)
    {
        if (!state.Turns.TryGetValue(turnId, out var turn))
            return state;

        return state with
        {
            InFlight = state.InFlight.SetItem(turn.ReplicaId, Math.Max(0, state.InFlightOn(turn.ReplicaId) - 1)),
            Turns = state.Turns.Remove(turnId),
        };
    }

    public static SchedulerState Abandon(SchedulerState state, TurnAbandoned abandoned)
    {
        var unqueued = WaitQueue.Remove(state, abandoned.TurnId);
        var released = Release(unqueued, abandoned.TurnId);
        return released.Runs.GetValueOrDefault(abandoned.RunId) is { Phase: Generating g } run && g.TurnId == abandoned.TurnId
            ? released.WithRun(run with { Phase = new Idle(abandoned.AtUnixMs) })
            : released;
    }

    public static SchedulerState SettlePrevious(SchedulerState state, TurnRequested request) =>
        state.Runs.GetValueOrDefault(request.RunId)?.Phase is Generating previous && previous.TurnId != request.TurnId
            ? Release(state, previous.TurnId)
            : state;

    public static SchedulerState Complete(SchedulerState state, TurnCompleted completed)
    {
        if (!state.Turns.ContainsKey(completed.TurnId))
            return state;

        var released = KvBudget.Record(Release(state, completed.TurnId), state.Turns[completed.TurnId], completed.Usage);
        if (released.Runs.GetValueOrDefault(completed.RunId) is not { Phase: Generating g } run || g.TurnId != completed.TurnId)
            return released;

        RunPhase phase = completed.OpensToolPause ? new AwaitingTool(completed.AtUnixMs) : new Idle(completed.AtUnixMs);
        return released with
        {
            Runs = released.Runs.SetItem(run.RunId, run with { Phase = phase, LastActivityUnixMs = completed.AtUnixMs }),
        };
    }
}
