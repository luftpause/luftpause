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
        request.ServiceClass == Proto.ServiceClass.Batch ? ServiceClass.Batch : ServiceClass.Interactive);

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
        _ => null,
    };

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
            RejectTurn reject => new Proto.Decision
            {
                Reject = new Proto.Reject { RetryAfterMs = (uint)reject.RetryAfterMs, Reason = reject.Reason },
            },
            RefuseTurn refuse => new Proto.Decision
            {
                Refuse = new Proto.Refuse { Reason = refuse.Reason },
            },
            _ => new Proto.Decision
            {
                Reject = new Proto.Reject { RetryAfterMs = Decider.NoReplicaRetryAfterMs, Reason = "no decision" },
            },
        };

    private static EngineClass ToEngineClass(Proto.EngineClass engineClass) => engineClass switch
    {
        Proto.EngineClass.SinglePass => EngineClass.SinglePass,
        _ => EngineClass.Generative,
    };
}
