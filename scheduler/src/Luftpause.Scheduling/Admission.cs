using System.Collections.Immutable;

namespace Luftpause.Scheduling;

public static class Admission
{
    public static ImmutableArray<Replica> Candidates(SchedulerState state, TurnRequested request) =>
        state.ReplicasOf(request.Class)
            .Where(r => r.Serves(request.Model) && state.IsHealthy(r) && HasRoom(state, r, request.Service) && WorkingSet.Admits(state, r, request))
            .ToImmutableArray();

    public static bool HasRoom(SchedulerState state, Replica replica, ServiceClass service) =>
        !state.Capacity.TryGetValue(replica.Id, out var capacity) || state.InFlightOn(replica.Id) + Held(state, replica, service) < capacity;

    private static int Held(SchedulerState state, Replica replica, ServiceClass service) =>
        service == ServiceClass.Batch ? state.InteractivePausedOn(replica.Id) : 0;
}
