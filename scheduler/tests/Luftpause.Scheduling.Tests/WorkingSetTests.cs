namespace Luftpause.Scheduling.Tests;

public class WorkingSetTests
{
    private static readonly Replica A = new("a", "http://a", EngineClass.Generative);
    private static readonly Replica B = new("b", "http://b", EngineClass.Generative);
    private static readonly Decider Decider = new(new AffinityRouting(new LeastLoadedRouting()));

    private static TurnRequested Request(string turnId, string runId, long at = 0) =>
        new(turnId, runId, EngineClass.Generative, "m", false, at);

    private static TurnCompleted Paused(string turnId, string runId, int contextTokens) =>
        new(turnId, runId, "tool_calls", new TokenUsage(contextTokens - 10, 10, 0), false, 1);

    private static TurnCompleted Finished(string turnId, string runId) =>
        new(turnId, runId, "stop", new TokenUsage(100, 10, 0), false, 2);

    private static SchedulerState WithPool(SchedulerState state, string replica, int kvTokens) =>
        state with { KvPool = state.KvPool.SetItem(replica, kvTokens) };

    private static Decision Run(SchedulerState state, params SchedulerEvent[] events) =>
        events.Aggregate(Decision.Unchanged(state), (d, e) => d.Then(s => Decider.Decide(s, e)));

    private static IEnumerable<string> RoutedTurns(Decision decision) =>
        decision.Actions.OfType<RouteTurn>().Select(r => r.TurnId);

    private static Decision TwoPausedRunsOnAPoolForTwo() =>
        Run(WithPool(SchedulerState.Initial([A]), "a", 1000),
            Request("r1-1", "r1"), Paused("r1-1", "r1", 400),
            Request("r2-1", "r2"), Paused("r2-1", "r2", 400));

    [Fact]
    public void A_new_run_waits_while_the_replica_holds_as_many_runs_as_its_memory_fits()
    {
        var full = TwoPausedRunsOnAPoolForTwo();

        var decision = Run(full.State, Request("r3-1", "r3", at: 3), Request("r1-2", "r1", at: 4));

        Assert.Equal(["r1-2"], RoutedTurns(decision));
        Assert.Equal("r3-1", Assert.Single(decision.State.Waiting).TurnId);
    }

    [Fact]
    public void A_finished_run_frees_its_place_for_a_new_run()
    {
        var waiting = Run(TwoPausedRunsOnAPoolForTwo().State, Request("r3-1", "r3", at: 3), Request("r1-2", "r1", at: 4));

        var freed = Decider.Decide(waiting.State, Finished("r1-2", "r1"));

        Assert.Equal(["r3-1"], RoutedTurns(freed));
    }

    [Fact]
    public void A_replica_without_a_known_memory_size_takes_any_number_of_runs()
    {
        var decision = Run(SchedulerState.Initial([A]),
            Request("r1-1", "r1"), Paused("r1-1", "r1", 400),
            Request("r2-1", "r2"), Paused("r2-1", "r2", 400),
            Request("r3-1", "r3"));

        Assert.Contains("r3-1", RoutedTurns(decision));
    }

    [Fact]
    public void A_paused_run_waits_for_its_own_replica_rather_than_moving_to_one_without_its_context()
    {
        var paused = Run(SchedulerState.Initial([A, B]), Request("r1-1", "r1"), Paused("r1-1", "r1", 400)).State;
        var aFull = paused with
        {
            Capacity = paused.Capacity.SetItem("a", 1),
            InFlight = paused.InFlight.SetItem("a", 1),
            Turns = paused.Turns.SetItem("x1", new Turn("x1", "x", "a", 2)),
        };

        var resumed = Decider.Decide(aFull, Request("r1-2", "r1", at: 3));
        var freed = Decider.Decide(resumed.State, Finished("x1", "x"));

        Assert.Empty(RoutedTurns(resumed));
        Assert.Equal("a", Assert.IsType<RouteTurn>(Assert.Single(freed.Actions)).Replica.Id);
    }

    [Fact]
    public void A_paused_run_whose_replica_is_down_may_move()
    {
        var paused = Run(SchedulerState.Initial([A, B]),
            Request("r1-1", "r1"), Paused("r1-1", "r1", 400), new ReplicaHealthChanged("a", false, 2));

        var resumed = Decider.Decide(paused.State, Request("r1-2", "r1", at: 3));

        Assert.Equal("b", Assert.IsType<RouteTurn>(Assert.Single(resumed.Actions)).Replica.Id);
    }

    [Fact]
    public void A_load_report_records_the_engines_memory_size()
    {
        var decision = Decider.Decide(SchedulerState.Initial([A]), new ReplicaLoadReported("a", 0, 0, 0, 24000));

        Assert.Equal(24000, decision.State.KvPool["a"]);
    }
}
