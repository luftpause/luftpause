namespace Luftpause.Scheduling.Tests;

public class FairnessTests
{
    private static readonly Replica A = new("a", "http://a", EngineClass.Generative);
    private static readonly Replica B = new("b", "http://b", EngineClass.Generative);
    private static readonly Decider Decider = new(new AffinityRouting(new LeastLoadedRouting()));

    private static TurnRequested Request(
        string turnId, ServiceClass service, string caller, long at = 0, string? runId = null) =>
        new(turnId, runId ?? turnId, EngineClass.Generative, "m", false, at, service, Caller: caller);

    private static TurnCompleted Completed(string turnId, string? runId = null, long at = 100) =>
        new(turnId, runId ?? turnId, "stop", new TokenUsage(100, 10, 0), false, at);

    private static SchedulerState CappedAt(int capacity, params Replica[] replicas)
    {
        var initial = SchedulerState.Initial(replicas);
        return initial with { Capacity = replicas.Aggregate(initial.Capacity, (c, r) => c.SetItem(r.Id, capacity)) };
    }

    private static Decision Run(SchedulerState state, params SchedulerEvent[] events) =>
        events.Aggregate(Decision.Unchanged(state), (d, e) => d.Then(s => Decider.Decide(s, e)));

    private static IEnumerable<string> RoutedTurns(Decision decision) =>
        decision.Actions.OfType<RouteTurn>().Select(r => r.TurnId);

    [Fact]
    public void A_quiet_callers_batch_turn_is_released_before_a_flooding_callers_next_one()
    {
        var flooded = Run(CappedAt(2, A),
            Request("b1", ServiceClass.Batch, "a", at: 1),
            Request("b2", ServiceClass.Batch, "a", at: 2),
            Request("b3", ServiceClass.Batch, "a", at: 3),
            Request("b4", ServiceClass.Batch, "a", at: 4),
            Request("b5", ServiceClass.Batch, "b", at: 5));

        var completion = Decider.Decide(flooded.State, Completed("b1"));

        Assert.Equal(["b5"], RoutedTurns(completion));
    }

    [Fact]
    public void Interactive_turns_go_first_whatever_the_caller()
    {
        var held = Run(CappedAt(1, A),
            Request("b1", ServiceClass.Batch, "a", at: 1),
            Request("b3", ServiceClass.Batch, "b", at: 2),
            Request("i2", ServiceClass.Interactive, "a", at: 3));

        var completion = Decider.Decide(held.State, Completed("b1"));

        Assert.Equal(["i2"], RoutedTurns(completion));
    }

    [Fact]
    public void In_flight_turns_count_pool_wide()
    {
        var held = Run(CappedAt(1, A, B),
            Request("a1", ServiceClass.Batch, "a", at: 1),
            Request("a2", ServiceClass.Batch, "a", at: 2),
            Request("a3", ServiceClass.Batch, "a", at: 3),
            Request("b4", ServiceClass.Batch, "b", at: 4));

        var completion = Decider.Decide(held.State, Completed("a1"));

        Assert.Equal(["b4"], RoutedTurns(completion));
    }

    [Fact]
    public void A_run_keeps_the_caller_of_its_first_turn()
    {
        var decision = Run(SchedulerState.Initial([A]),
            Request("t1", ServiceClass.Interactive, "a", at: 1, runId: "r1"),
            Completed("t1", runId: "r1", at: 2),
            Request("t2", ServiceClass.Interactive, "", at: 3, runId: "r1"));

        Assert.Equal("a", decision.State.Runs["r1"].Caller);
    }

    [Fact]
    public void An_abandoned_held_turn_does_not_count_for_its_caller()
    {
        var held = Run(CappedAt(1, A),
            Request("a1", ServiceClass.Batch, "a", at: 1),
            Request("a2", ServiceClass.Batch, "a", at: 2));

        var abandoned = Decider.Decide(held.State, new TurnAbandoned("a2", "a2", 3));

        Assert.Empty(abandoned.State.Waiting);
        Assert.Equal(1, abandoned.State.InFlightOf("a"));
    }

    [Fact]
    public void An_abandoned_turn_in_flight_no_longer_counts_for_its_caller()
    {
        var routed = Run(CappedAt(1, A), Request("a1", ServiceClass.Batch, "a", at: 1));

        var abandoned = Decider.Decide(routed.State, new TurnAbandoned("a1", "a1", 2));

        Assert.Equal(0, abandoned.State.InFlightOf("a"));
    }
}
