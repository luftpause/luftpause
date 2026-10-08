using System.Collections.Concurrent;
using Luftpause.Scheduling;

namespace Luftpause.Scheduler;

public sealed class AdmissionWaiters
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<SchedulerAction>> waiting = new();

    public Task<SchedulerAction> Register(string turnId) =>
        waiting.GetOrAdd(turnId, _ => new TaskCompletionSource<SchedulerAction>(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    public void Resolve(SchedulerAction action)
    {
        var turnId = action switch
        {
            RouteTurn route => route.TurnId,
            RejectTurn reject => reject.TurnId,
            RefuseTurn refuse => refuse.TurnId,
            _ => null,
        };
        if (turnId is not null && waiting.TryRemove(turnId, out var waiter))
            waiter.TrySetResult(action);
    }

    public void Forget(string turnId) => waiting.TryRemove(turnId, out _);
}
