using Luftpause.Scheduling;

namespace Luftpause.Scheduler;

public sealed class Dispatcher(StateStore store, AdmissionWaiters waiters)
{
    public Decision Apply(SchedulerEvent @event)
    {
        var decision = store.Apply(@event);
        foreach (var action in decision.Actions)
            waiters.Resolve(action);
        return decision;
    }
}
