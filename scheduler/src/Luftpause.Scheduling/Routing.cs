using System.Collections.Immutable;

namespace Luftpause.Scheduling;

public interface IRoutingPolicy
{
    Replica? Choose(SchedulerState state, Run? run, ImmutableArray<Replica> candidates);
}

public sealed class RoundRobinRouting : IRoutingPolicy
{
    public Replica? Choose(SchedulerState state, Run? run, ImmutableArray<Replica> candidates) =>
        candidates.IsEmpty ? null : candidates[(int)(state.Routed % candidates.Length)];
}

public sealed class LeastLoadedRouting : IRoutingPolicy
{
    public Replica? Choose(SchedulerState state, Run? run, ImmutableArray<Replica> candidates)
    {
        if (candidates.IsEmpty)
            return null;

        var start = (int)(state.Routed % candidates.Length);
        return Enumerable.Range(0, candidates.Length)
            .Select(i => candidates[(start + i) % candidates.Length])
            .MinBy(r => state.InFlightOn(r.Id));
    }
}

public sealed class AffinityRouting(IRoutingPolicy fallback) : IRoutingPolicy
{
    public Replica? Choose(SchedulerState state, Run? run, ImmutableArray<Replica> candidates) =>
        candidates.FirstOrDefault(r => r.Id == run?.LastReplicaId)
        ?? fallback.Choose(state, run, candidates);
}
