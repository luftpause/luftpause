using Grpc.Core;
using Luftpause.Scheduling;
using Proto = Luftpause.Protocol.V1;

namespace Luftpause.Scheduler;

public sealed class SchedulerService(
    Dispatcher dispatcher, AdmissionWaiters waiters, EngineCredentials credentials, TimeProvider time, ILogger<SchedulerService> log)
    : Proto.Scheduler.SchedulerBase
{
    public override async Task<Proto.Decision> Admit(Proto.TurnRequest request, ServerCallContext context)
    {
        var requested = time.GetTimestamp();
        var placed = waiters.Register(request.TurnId);
        dispatcher.Apply(ProtocolMapping.ToEvent(request));
        try
        {
            var action = await placed.WaitAsync(context.CancellationToken);
            context.CancellationToken.ThrowIfCancellationRequested();
            LogAdmitted(request, action, time.GetElapsedTime(requested));
            return ProtocolMapping.ToDecision(action, credentials);
        }
        catch (OperationCanceledException)
        {
            waiters.Forget(request.TurnId);
            dispatcher.Apply(new TurnAbandoned(request.TurnId, request.RunId, time.GetUtcNow().ToUnixTimeMilliseconds()));
            throw;
        }
    }

    public override async Task<Proto.ReportAck> Report(
        IAsyncStreamReader<Proto.TurnEvent> requestStream, ServerCallContext context)
    {
        await foreach (var e in requestStream.ReadAllAsync(context.CancellationToken))
        {
            if (ProtocolMapping.ToEvent(e) is { } schedulerEvent)
                dispatcher.Apply(schedulerEvent);
        }
        return new Proto.ReportAck();
    }

    private void LogAdmitted(Proto.TurnRequest request, SchedulerAction action, TimeSpan waited) =>
        log.LogInformation(
            "turn_admitted turn={Turn} run={Run} replica={Replica} waitedMs={WaitedMs}",
            request.TurnId, request.RunId, action is RouteTurn route ? route.Replica.Id : "refused", (long)waited.TotalMilliseconds);
}
