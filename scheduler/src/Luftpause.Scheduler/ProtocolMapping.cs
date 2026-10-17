using Luftpause.Scheduling;
using Proto = Luftpause.Protocol.V1;

namespace Luftpause.Scheduler;

public static class ProtocolMapping
{
    public static TurnRequested ToEvent(Proto.TurnRequest request) => new(
        request.TurnId,
        request.RunId,
        ToEngineClass(request.EngineClass),
        request.Model,
        request.Resumed,
        request.AtUnixMs,
        ToServiceClass(request.ServiceClass),
        request.Caller);

    public static SchedulerEvent? ToEvent(Proto.TurnEvent e) => e.KindCase switch
    {
        Proto.TurnEvent.KindOneofCase.Started => new TurnStarted(e.TurnId, e.RunId, e.Started.ReplicaId, e.AtUnixMs),
        Proto.TurnEvent.KindOneofCase.FirstToken => new FirstToken(e.TurnId, e.RunId, e.AtUnixMs),
        Proto.TurnEvent.KindOneofCase.Completed => new TurnCompleted(
            e.TurnId,
            e.RunId,
            e.Completed.FinishReason,
            new TokenUsage((int)e.Completed.PromptTokens, (int)e.Completed.CompletionTokens, (int)e.Completed.CachedTokens),
            e.Completed.Failed,
            e.AtUnixMs),
        Proto.TurnEvent.KindOneofCase.RunEnded => new RunEnded(e.RunId, e.AtUnixMs),
        _ => null,
    };

    public static StateResynced ToEvent(Proto.Snapshot snapshot) => new(
        new Snapshot([.. snapshot.Runs.Select(ToRun)], [.. snapshot.Turns.Select(ToTurn)]),
        snapshot.AtUnixMs);

    private static Run ToRun(Proto.RunState run) => new(
        run.RunId,
        ToPhase(run),
        run.LastReplicaId is "" ? null : run.LastReplicaId,
        (int)run.Turns,
        run.LastActivityUnixMs,
        run.StartedUnixMs,
        ToServiceClass(run.ServiceClass),
        run.Caller);

    private static RunPhase ToPhase(Proto.RunState run) => run.Phase switch
    {
        Proto.RunPhase.Generating => new Generating(run.GeneratingTurnId),
        Proto.RunPhase.AwaitingTool => new AwaitingTool(run.PhaseSinceUnixMs),
        Proto.RunPhase.Idle => new Idle(run.PhaseSinceUnixMs),
        _ => throw new ArgumentOutOfRangeException(nameof(run), run.Phase, $"run '{run.RunId}' has no phase"),
    };

    private static Turn ToTurn(Proto.InFlightTurn turn) =>
        new(turn.TurnId, turn.RunId, turn.ReplicaId, turn.StartedUnixMs, turn.Caller);

    public static Proto.Decision ToDecision(SchedulerAction action, EngineCredentials credentials) =>
        action switch
        {
            RouteTurn route => new Proto.Decision
            {
                Route = new Proto.Route
                {
                    ReplicaId = route.Replica.Id,
                    BaseUrl = route.Replica.BaseUrl,
                    Path = route.Replica.Path,
                    Authorization = credentials.AuthorizationFor(route.Replica.Id) ?? "",
                },
            },
            RefuseTurn refuse => new Proto.Decision
            {
                Refuse = new Proto.Refuse { Reason = refuse.Reason },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "not a decision"),
        };

    private static ServiceClass ToServiceClass(Proto.ServiceClass serviceClass) =>
        serviceClass == Proto.ServiceClass.Batch ? ServiceClass.Batch : ServiceClass.Interactive;

    private static EngineClass ToEngineClass(Proto.EngineClass engineClass) => engineClass switch
    {
        Proto.EngineClass.SinglePass => EngineClass.SinglePass,
        _ => EngineClass.Generative,
    };
}
