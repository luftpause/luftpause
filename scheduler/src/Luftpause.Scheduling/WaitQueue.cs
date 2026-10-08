namespace Luftpause.Scheduling;

public static class WaitQueue
{
    public const long MaxWaitMs = 60_000;
    public const int RetryAfterMs = 5_000;

    public static Decision Enqueue(SchedulerState state, TurnRequested request) =>
        Decision.Unchanged(state with { Waiting = state.Waiting.Add(request) });

    public static Decision Drain(SchedulerState state, Func<SchedulerState, TurnRequested, Decision?> place) =>
        state.Waiting
            .OrderBy(w => w.Service == ServiceClass.Interactive ? 0 : 1)
            .ThenBy(w => w.AtUnixMs)
            .Aggregate(Decision.Unchanged(state), (decision, waiting) =>
            {
                var placed = place(decision.State with { Waiting = decision.State.Waiting.Remove(waiting) }, waiting);
                return placed is null ? decision : decision.Then(_ => placed);
            });

    public static Decision Expire(SchedulerState state, Tick tick)
    {
        var expired = state.Waiting.Where(w => tick.AtUnixMs - w.AtUnixMs > MaxWaitMs).ToList();
        return new Decision(
            state with { Waiting = state.Waiting.RemoveRange(expired) },
            [.. expired.Select(w => new RejectTurn(w.TurnId, RetryAfterMs, "queue wait exceeded"))]);
    }

    public static SchedulerState Remove(SchedulerState state, string turnId) =>
        state with { Waiting = state.Waiting.RemoveAll(w => w.TurnId == turnId) };
}
