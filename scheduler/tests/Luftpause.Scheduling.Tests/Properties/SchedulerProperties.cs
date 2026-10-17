using System.Collections.Immutable;
using FsCheck.Xunit;

namespace Luftpause.Scheduling.Tests.Properties;

public class SchedulerProperties
{
    private const int Cases = 300;

    [Property(MaxTest = Cases, Arbitrary = new[] { typeof(AnyScenario) })]
    public void In_flight_per_replica_equals_the_turns_on_it(Scenario scenario) =>
        Invariant.HoldsAtEveryStep(scenario, step =>
        {
            var state = step.After.State;
            return state.Replicas
                .Select(r => (r.Id, inFlight: state.InFlightOn(r.Id), turns: state.Turns.Values.Count(t => t.ReplicaId == r.Id)))
                .Where(r => r.inFlight != r.turns)
                .Select(r => $"{r.Id} has InFlight {r.inFlight} but {r.turns} turns")
                .FirstOrDefault();
        });

    [Property(MaxTest = Cases, Arbitrary = new[] { typeof(CappedScenario) })]
    public void Without_a_kv_pool_routing_never_takes_a_replica_past_its_capacity(Scenario scenario) =>
        Invariant.HoldsAtEveryStep(scenario, step =>
        {
            var state = step.After.State;
            if (!state.KvPool.IsEmpty)
                return "a capped scenario reported a kv pool";
            return RoutedReplicas(step)
                .Where(id => state.Capacity.TryGetValue(id, out var capacity) && state.InFlightOn(id) > capacity)
                .Select(id => $"{id} has InFlight {state.InFlightOn(id)} over capacity {state.Capacity[id]}")
                .FirstOrDefault();
        });

    [Property(MaxTest = Cases, Arbitrary = new[] { typeof(PooledScenario) })]
    public void With_a_kv_pool_routing_never_overcommits_it_beyond_one_turn_on_an_idle_replica(Scenario scenario) =>
        Invariant.HoldsAtEveryStep(scenario, step =>
        {
            var state = step.After.State;
            return RoutedReplicas(step)
                .Where(id => state.KvPool.ContainsKey(id))
                .Select(id => (id, tokens: state.Turns.Values.Where(t => t.ReplicaId == id).Sum(t => t.KvTokens), pool: state.KvPool[id], inFlight: state.InFlightOn(id)))
                .Where(r => r.tokens > r.pool && r.inFlight > 1)
                .Select(r => $"{r.id} holds {r.tokens} kv tokens over pool {r.pool} across {r.inFlight} turns")
                .FirstOrDefault();
        });

    [Property(MaxTest = Cases, Arbitrary = new[] { typeof(AnyScenario) })]
    public void Every_route_names_a_healthy_replica_serving_the_model(Scenario scenario) =>
        Invariant.HoldsAtEveryStep(scenario, step =>
            step.Routes
                .Select(route => (route, request: step.Requested(route.TurnId)))
                .Where(r => !step.After.State.IsHealthy(r.route.Replica) || !r.route.Replica.Serves(r.request.Model))
                .Select(r => $"{r.route.TurnId} ({r.request.Model}) routed to {r.route.Replica.Id}, healthy={step.After.State.IsHealthy(r.route.Replica)}")
                .FirstOrDefault());

    [Property(MaxTest = Cases, Arbitrary = new[] { typeof(AnyScenario) })]
    public void A_turn_is_either_waiting_or_in_flight_never_both(Scenario scenario) =>
        Invariant.HoldsAtEveryStep(scenario, step =>
        {
            var state = step.After.State;
            var waiting = state.Waiting.Select(w => w.TurnId).ToList();
            return waiting.GroupBy(id => id).Where(g => g.Count() > 1).Select(g => $"{g.Key} is waiting {g.Count()} times")
                .Concat(waiting.Where(state.Turns.ContainsKey).Select(id => $"{id} is both waiting and in flight"))
                .FirstOrDefault();
        });

    [Property(MaxTest = Cases, Arbitrary = new[] { typeof(AnyScenario) })]
    public void A_drain_routes_no_batch_turn_ahead_of_an_interactive_one_waiting_with_it(Scenario scenario) =>
        Invariant.HoldsAtEveryStep(scenario, step =>
        {
            if (step.Event is TurnRequested)
                return null;
            var waiting = step.Before.Waiting.ToDictionary(w => w.TurnId, w => w.Service);
            var drained = step.Routes.Where(r => waiting.ContainsKey(r.TurnId)).Select(r => (r.TurnId, service: waiting[r.TurnId])).ToList();
            return drained
                .Select((batch, i) => (batch, interactive: drained.Skip(i + 1).FirstOrDefault(d => d.service == ServiceClass.Interactive)))
                .Where(p => p.batch.service == ServiceClass.Batch && p.interactive.TurnId is not null)
                .Select(p => $"batch {p.batch.TurnId} routed before interactive {p.interactive.TurnId}")
                .FirstOrDefault();
        });

    [Property(MaxTest = Cases, Arbitrary = new[] { typeof(AnyScenario) })]
    public void A_run_whose_turn_ended_in_tool_calls_awaits_the_tool_until_its_next_turn_or_expiry(Scenario scenario) =>
        Invariant.HoldsAcrossSteps(scenario, ImmutableHashSet<string>.Empty, (paused, step) =>
        {
            var expected = paused.Union(PausedBy(step)).Except(Released(step));
            var state = step.After.State;
            var why = expected
                .Where(run => state.Runs.GetValueOrDefault(run)?.Phase is not AwaitingTool)
                .Select(run => $"{run} should await its tool but is {Phase(state, run)}")
                .FirstOrDefault();
            return (expected, why);
        });

    [Property(MaxTest = Cases, Arbitrary = new[] { typeof(AnyScenario) })]
    public void Ending_a_run_removes_it_and_changes_nothing_else_but_the_drain(Scenario scenario) =>
        Invariant.HoldsAtEveryStep(scenario, step => step.Event is RunEnded ended ? EndedCleanly(step, ended.RunId) : null);

    private static IEnumerable<string> RoutedReplicas(Step step) => step.Routes.Select(r => r.Replica.Id).Distinct();

    private static IEnumerable<string> PausedBy(Step step) =>
        step.Event is TurnCompleted { OpensToolPause: true } completed
        && step.Before.Turns.ContainsKey(completed.TurnId)
        && step.Before.Runs.GetValueOrDefault(completed.RunId)?.Phase is Generating g
        && g.TurnId == completed.TurnId
            ? [completed.RunId]
            : [];

    private static IEnumerable<string> Released(Step step)
    {
        var nextTurns = step.Routes.Select(r => step.Requested(r.TurnId)).Append(step.Event as TurnRequested)
            .Where(r => r is { Class: EngineClass.Generative })
            .Select(r => r!.RunId);
        var gone = step.Event is Tick or RunEnded
            ? step.Before.Runs.Keys.Where(run => !step.After.State.Runs.ContainsKey(run))
            : [];
        return nextTurns.Concat(gone);
    }

    private static string Phase(SchedulerState state, string runId) =>
        state.Runs.TryGetValue(runId, out var run) ? run.Phase.ToString() : "gone";

    private static string? EndedCleanly(Step step, string runId)
    {
        var before = step.Before;
        var after = step.After.State;
        var routes = step.Routes.ToList();
        var routed = routes.Select(r => r.TurnId).ToHashSet();
        var drainedRuns = routes.Select(r => step.Requested(r.TurnId)).Where(r => r.Class == EngineClass.Generative).Select(r => r.RunId).ToHashSet();
        var expectedInFlight = before.InFlight.SetItems(routes.GroupBy(r => r.Replica.Id).Select(g => KeyValuePair.Create(g.Key, before.InFlightOn(g.Key) + g.Count())));

        string?[] differences =
        [
            after.Runs.ContainsKey(runId) ? $"{runId} is still in Runs as {after.Runs[runId]}" : null,
            Same(before.Runs.Remove(runId).RemoveRange(drainedRuns), after.Runs.Remove(runId).RemoveRange(drainedRuns)) ? null : "other runs changed",
            Same(before.Turns, after.Turns.RemoveRange(routed)) ? null : "turns other than the drained ones changed",
            before.Waiting.Where(w => !routed.Contains(w.TurnId)).SequenceEqual(after.Waiting) ? null : "waiting changed beyond the drain",
            Same(expectedInFlight, after.InFlight) ? null : "in-flight counts changed beyond the drain",
            after.Routed == before.Routed + routes.Count ? null : "routed count changed beyond the drain",
            after.Replicas.SequenceEqual(before.Replicas) ? null : "replicas changed",
            after.Unhealthy.SetEquals(before.Unhealthy) ? null : "health changed",
            Same(before.Capacity, after.Capacity) ? null : "capacity changed",
            Same(before.KvPool, after.KvPool) ? null : "kv pool changed",
            Same(before.Usage, after.Usage) ? null : "usage changed",
        ];
        return differences.FirstOrDefault(d => d is not null);
    }

    private static bool Same<TValue>(IReadOnlyDictionary<string, TValue> left, IReadOnlyDictionary<string, TValue> right) =>
        left.Count == right.Count && left.All(kv => right.TryGetValue(kv.Key, out var other) && Equals(kv.Value, other));
}
