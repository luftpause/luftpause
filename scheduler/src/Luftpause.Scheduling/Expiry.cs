namespace Luftpause.Scheduling;

public static class Expiry
{
    public const long TurnTimeoutMs = 10 * 60 * 1000;
    public const long RunIdleTimeoutMs = 30 * 60 * 1000;

    public static SchedulerState Apply(SchedulerState state, Tick tick)
    {
        var withoutStaleTurns = state.Turns.Values
            .Where(t => tick.AtUnixMs - t.StartedUnixMs > TurnTimeoutMs)
            .Aggregate(state, (s, turn) => TurnLifecycle.Abandon(s, new TurnAbandoned(turn.TurnId, turn.RunId, tick.AtUnixMs)));

        var staleRuns = withoutStaleTurns.Runs.Values
            .Where(r => r.Phase is not Generating && tick.AtUnixMs - r.LastActivityUnixMs > RunIdleTimeoutMs)
            .Select(r => r.RunId);

        return withoutStaleTurns with { Runs = withoutStaleTurns.Runs.RemoveRange(staleRuns) };
    }
}
