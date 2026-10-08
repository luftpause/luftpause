using Grpc.Core;
using Luftpause.Scheduling;
using Proto = Luftpause.Protocol.V1;

namespace Luftpause.Scheduler;

public sealed class SchedulerService(Dispatcher dispatcher, AdmissionWaiters waiters, EngineCredentials credentials, TimeProvider time)
    : Proto.Scheduler.SchedulerBase
{
    public override async Task<Proto.Decision> Admit(Proto.TurnRequest request, ServerCallContext context)
    {
        var placed = waiters.Register(request.TurnId);
        dispatcher.Apply(ProtocolMapping.ToEvent(request));
        try
        {
            var action = await placed.WaitAsync(context.CancellationToken);
            context.CancellationToken.ThrowIfCancellationRequested();
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
}
