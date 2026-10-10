using System.Collections.Immutable;

namespace Luftpause.Scheduling;

public abstract record SchedulerAction;

public sealed record RouteTurn(string TurnId, Replica Replica) : SchedulerAction;

public sealed record RefuseTurn(string TurnId, string Reason) : SchedulerAction;

public sealed record Decision(SchedulerState State, ImmutableArray<SchedulerAction> Actions)
{
    public static Decision Unchanged(SchedulerState state) => new(state, []);

    public Decision Then(Func<SchedulerState, Decision> next)
    {
        var following = next(State);
        return new Decision(following.State, Actions.AddRange(following.Actions));
    }
}
