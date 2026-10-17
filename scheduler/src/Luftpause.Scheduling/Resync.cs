using System.Collections.Immutable;

namespace Luftpause.Scheduling;

public sealed record Snapshot(ImmutableArray<Run> Runs, ImmutableArray<Turn> Turns);

public static class Resync
{
    public static SchedulerState Apply(SchedulerState state, Snapshot snapshot)
    {
        var known = state.Replicas.Select(r => r.Id).ToImmutableHashSet();
        var turns = snapshot.Turns.Where(t => known.Contains(t.ReplicaId)).ToImmutableDictionary(t => t.TurnId);
        var perReplica = turns.Values.CountBy(t => t.ReplicaId).ToDictionary();
        return state with
        {
            Turns = turns,
            InFlight = state.Replicas.ToImmutableDictionary(r => r.Id, r => perReplica.GetValueOrDefault(r.Id)),
            Runs = snapshot.Runs.ToImmutableDictionary(r => r.RunId, r => Reconciled(r, known, turns)),
        };
    }

    private static Run Reconciled(Run run, ImmutableHashSet<string> known, ImmutableDictionary<string, Turn> turns) => run with
    {
        LastReplicaId = run.LastReplicaId is { } id && known.Contains(id) ? id : null,
        Phase = run.Phase is Generating g && !turns.ContainsKey(g.TurnId) ? new Idle(run.LastActivityUnixMs) : run.Phase,
    };
}
