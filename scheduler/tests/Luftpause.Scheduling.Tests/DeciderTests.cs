
namespace Luftpause.Scheduling.Tests;

public class DeciderTests
{
    private static readonly Replica A = new("a", "http://a", EngineClass.Generative);
    private static readonly Replica B = new("b", "http://b", EngineClass.Generative);
    private static readonly Decider RoundRobin = new(new RoundRobinRouting());
    private static readonly Decider Affinity = new(new AffinityRouting(new LeastLoadedRouting()));

    private static TurnRequested Request(string turnId, string runId = "run-1", long at = 0, EngineClass engineClass = EngineClass.Generative) =>
        new(turnId, runId, engineClass, "m", false, at);

    private static TurnCompleted Completed(string turnId, string runId = "run-1", string finishReason = "stop", long at = 1) =>
        new(turnId, runId, finishReason, new TokenUsage(10, 5, 0), false, at);

    private static string RoutedTo(Decision decision) =>
        Assert.IsType<RouteTurn>(Assert.Single(decision.Actions)).Replica.Id;

    private static SchedulerState Apply(Decider decider, SchedulerState state, params SchedulerEvent[] events) =>
        events.Aggregate(state, (s, e) => decider.Decide(s, e).State);

    [Fact]
    public void Round_robin_alternates_across_replicas_of_the_class()
    {
        var state = SchedulerState.Initial([A, B]);

        var first = RoundRobin.Decide(state, Request("t1", "r1"));
        var second = RoundRobin.Decide(first.State, Request("t2", "r2"));
        var third = RoundRobin.Decide(second.State, Request("t3", "r3"));

        Assert.Equal(["a", "b", "a"], new[] { first, second, third }.Select(RoutedTo));
    }

    [Fact]
    public void Refuses_when_no_replica_serves_the_class()
    {
        var decision = Affinity.Decide(SchedulerState.Initial([A]), Request("t1", engineClass: EngineClass.SinglePass));

        Assert.IsType<RefuseTurn>(Assert.Single(decision.Actions));
        Assert.Empty(decision.State.Turns);
    }

    [Fact]
    public void A_run_resumes_on_the_replica_that_served_its_last_turn()
    {
        var state = Apply(Affinity, SchedulerState.Initial([A, B]),
            Request("t1", "r1"), Request("t2", "r2"),
            Completed("t1", "r1", "tool_calls"), Completed("t2", "r2", "tool_calls"));
        var lastReplica = state.Runs["r2"].LastReplicaId;

        var resumed = Affinity.Decide(state, Request("t3", "r2", at: 2));

        Assert.Equal(lastReplica, RoutedTo(resumed));
    }

    [Fact]
    public void A_new_run_goes_to_the_least_loaded_replica()
    {
        var busyOnA = Apply(Affinity, SchedulerState.Initial([A, B]), Request("t1", "r1"));
        var busy = busyOnA.Turns["t1"].ReplicaId;

        var fresh = Affinity.Decide(busyOnA, Request("t2", "r2"));

        Assert.NotEqual(busy, RoutedTo(fresh));
    }

    [Fact]
    public void A_tool_call_finish_opens_a_pause_and_releases_the_slot()
    {
        var state = Apply(Affinity, SchedulerState.Initial([A]), Request("t1"), Completed("t1", finishReason: "tool_calls", at: 7));

        Assert.Equal(new AwaitingTool(7), state.Runs["run-1"].Phase);
        Assert.Equal(0, state.InFlightOn("a"));
    }

    [Fact]
    public void A_stop_finish_leaves_the_run_idle()
    {
        var state = Apply(Affinity, SchedulerState.Initial([A]), Request("t1"), Completed("t1", at: 7));

        Assert.Equal(new Idle(7), state.Runs["run-1"].Phase);
    }

    [Fact]
    public void A_next_turn_arriving_before_the_completion_event_settles_the_previous_turn()
    {
        var state = Apply(Affinity, SchedulerState.Initial([A]), Request("t1"), Request("t2", at: 5), Completed("t1", finishReason: "tool_calls"));

        Assert.Equal(1, state.InFlightOn("a"));
        Assert.Equal(new Generating("t2"), state.Runs["run-1"].Phase);
        Assert.Equal(2, state.Runs["run-1"].Turns);
    }

    [Fact]
    public void Completion_of_an_unknown_turn_changes_nothing()
    {
        var state = SchedulerState.Initial([A]);

        var decision = Affinity.Decide(state, Completed("missing"));

        Assert.Same(state, decision.State);
        Assert.Empty(decision.Actions);
    }

    [Fact]
    public void A_tick_abandons_turns_that_never_completed()
    {
        var state = Apply(Affinity, SchedulerState.Initial([A]), Request("t1"), new Tick(Expiry.TurnTimeoutMs + 1));

        Assert.Empty(state.Turns);
        Assert.Equal(0, state.InFlightOn("a"));
        Assert.IsType<Idle>(state.Runs["run-1"].Phase);
    }

    [Fact]
    public void A_tick_forgets_runs_idle_past_the_timeout_but_keeps_recent_ones()
    {
        var state = Apply(Affinity, SchedulerState.Initial([A]),
            Request("t1", "old"), Completed("t1", "old", at: 0),
            Request("t2", "recent", at: Expiry.RunIdleTimeoutMs), Completed("t2", "recent", at: Expiry.RunIdleTimeoutMs),
            new Tick(Expiry.RunIdleTimeoutMs + 1));

        Assert.False(state.Runs.ContainsKey("old"));
        Assert.True(state.Runs.ContainsKey("recent"));
    }
}
