namespace Luftpause.Scheduling;

public sealed class Decider(IRoutingPolicy routing)
{
    public Decision Decide(SchedulerState state, SchedulerEvent @event) => @event switch
    {
        TurnRequested request => Admit(state, request),
        TurnCompleted completed => Drain(TurnLifecycle.Complete(state, completed)),
        TurnAbandoned abandoned => Drain(TurnLifecycle.Abandon(state, abandoned)),
        ReplicaHealthChanged health => Drain(Health.Apply(state, health)),
        RunEnded ended => End(state, ended),
        ReplicaLoadReported load => Drain(EngineLoad.Apply(state, load)),
        Tick tick => Drain(Expiry.Apply(state, tick)),
        _ => Decision.Unchanged(state),
    };

    private Decision Admit(SchedulerState state, TurnRequested request)
    {
        if (!state.ReplicasOf(request.Class).Any(r => r.Serves(request.Model)))
            return new Decision(state, [new RefuseTurn(request.TurnId, $"no {request.Class} replica serves model '{request.Model}'")]);

        var settled = request.Class == EngineClass.Generative ? TurnLifecycle.SettlePrevious(state, request) : state;
        return Place(settled, request) ?? WaitQueue.Enqueue(settled, request);
    }

    private Decision? Place(SchedulerState state, TurnRequested request)
    {
        var inRun = request.Class == EngineClass.Generative;
        var run = inRun ? state.Runs.GetValueOrDefault(request.RunId) : null;
        var replica = routing.Choose(state, run, Admission.Candidates(state, request));
        if (replica is null)
            return null;

        var started = inRun ? TurnLifecycle.Start(state, request, replica) : TurnLifecycle.StartDetached(state, request, replica);
        return new Decision(started, [new RouteTurn(request.TurnId, replica)]);
    }

    private Decision End(SchedulerState state, RunEnded ended)
    {
        var orphans = state.Waiting.Where(w => w.RunId == ended.RunId).ToList();
        var cleared = orphans.Aggregate(state, (s, w) => WaitQueue.Remove(s, w.TurnId)) with { Runs = state.Runs.Remove(ended.RunId) };
        var refused = new Decision(cleared, [.. orphans.Select(w => new RefuseTurn(w.TurnId, $"run '{ended.RunId}' has ended"))]);
        return refused.Then(Drain);
    }

    private Decision Drain(SchedulerState state) => WaitQueue.Drain(state, Place);
}
