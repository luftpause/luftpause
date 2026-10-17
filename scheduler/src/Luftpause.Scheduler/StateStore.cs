using Luftpause.Scheduling;

namespace Luftpause.Scheduler;

public sealed class StateStore(Decider decider, SchedulerState initial)
{
    private readonly Lock gate = new();
    private SchedulerState state = initial;

    public SchedulerState Current
    {
        get
        {
            lock (gate)
                return state;
        }
    }

    public Decision Apply(SchedulerEvent @event)
    {
        lock (gate)
        {
            var decision = decider.Decide(state, @event);
            state = decision.State;
            return decision;
        }
    }
}
