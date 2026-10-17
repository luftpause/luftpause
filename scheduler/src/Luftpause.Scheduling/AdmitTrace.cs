using System.Diagnostics;

namespace Luftpause.Scheduling;

public static class AdmitTrace
{
    public static ActivityContext Parent(string? traceparent) =>
        ActivityContext.TryParse(traceparent, null, isRemote: true, out var parent) ? parent : default;

    public static IReadOnlyList<KeyValuePair<string, object?>> Requested(TurnRequested turn) =>
    [
        new("luftpause.turn_id", turn.TurnId),
        new("luftpause.run_id", turn.RunId),
        new("gen_ai.request.model", turn.Model),
        new("luftpause.service_class", turn.Service.ToString()),
    ];

    public static IReadOnlyList<KeyValuePair<string, object?>> Admitted(SchedulerAction action, TimeSpan held) => action switch
    {
        RouteTurn route => [new("luftpause.replica", route.Replica.Id), new("luftpause.held_ms", (long)held.TotalMilliseconds)],
        RefuseTurn refuse => [new("luftpause.refused", refuse.Reason), new("luftpause.held_ms", (long)held.TotalMilliseconds)],
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "not a decision"),
    };
}
