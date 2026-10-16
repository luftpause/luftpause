namespace Luftpause.Scheduling.Tests;

public class WorkingSetTests
{
    private static readonly Replica A = new("a", "http://a", EngineClass.Generative);
    private static readonly Replica B = new("b", "http://b", EngineClass.Generative);
    private static readonly Decider Decider = new(new AffinityRouting(new LeastLoadedRouting()));

    private static TurnRequested Request(string turnId, string runId, long at = 0, int bytes = 0, bool resumed = false) =>
        new(turnId, runId, EngineClass.Generative, "m", resumed, at, RequestBytes: bytes);

    private static TurnCompleted Paused(string turnId, string runId, int prompt = 400, int completion = 10) =>
        new(turnId, runId, "tool_calls", new TokenUsage(prompt, completion, 0), false, 1);

    private static TurnCompleted Finished(string turnId, string runId, string finish = "stop") =>
        new(turnId, runId, finish, new TokenUsage(100, 10, 0), false, 2);

    private static SchedulerState WithPool(SchedulerState state, string replica, int kvTokens) =>
        state with { KvPool = state.KvPool.SetItem(replica, kvTokens) };

    private static Decision Run(SchedulerState state, params SchedulerEvent[] events) =>
        events.Aggregate(Decision.Unchanged(state), (d, e) => d.Then(s => Decider.Decide(s, e)));

    private static IEnumerable<string> RoutedTurns(Decision decision) =>
        decision.Actions.OfType<RouteTurn>().Select(r => r.TurnId);

    [Fact]
    public void Turns_go_to_a_replica_while_their_memory_needs_fit_its_pool()
    {
        var decision = Run(WithPool(SchedulerState.Initial([A]), "a", 1000),
            Request("t1", "r1", bytes: 2000), Request("t2", "r2", bytes: 2000), Request("t3", "r3", bytes: 2000));

        Assert.Equal(["t1", "t2"], RoutedTurns(decision));
        Assert.Equal("t3", Assert.Single(decision.State.Waiting).TurnId);
    }

    [Fact]
    public void A_finished_turn_frees_its_memory_for_a_waiting_one()
    {
        var full = Run(WithPool(SchedulerState.Initial([A]), "a", 1000),
            Request("t1", "r1", bytes: 2000), Request("t2", "r2", bytes: 2000), Request("t3", "r3", bytes: 2000));

        var freed = Decider.Decide(full.State, Finished("t1", "r1"));

        Assert.Equal(["t3"], RoutedTurns(freed));
    }

    [Fact]
    public void A_turn_larger_than_the_pool_runs_alone()
    {
        var decision = Run(WithPool(SchedulerState.Initial([A]), "a", 1000), Request("t1", "r1", bytes: 40000));

        Assert.Equal(["t1"], RoutedTurns(decision));
    }

    [Fact]
    public void Runs_paused_on_a_tool_take_no_memory_from_new_runs()
    {
        var paused = Run(WithPool(SchedulerState.Initial([A]), "a", 1000),
            Request("t1", "r1", bytes: 2000), Paused("t1", "r1"),
            Request("t2", "r2", bytes: 2000), Paused("t2", "r2"));

        var decision = Run(paused.State, Request("t3", "r3", bytes: 2000, at: 3), Request("t4", "r4", bytes: 2000, at: 3));

        Assert.Equal(["t3", "t4"], RoutedTurns(decision));
    }

    [Fact]
    public void The_bytes_per_token_of_completed_turns_size_the_next_ones()
    {
        var learned = Run(WithPool(SchedulerState.Initial([A]), "a", 1000),
            Request("t1", "r1", bytes: 1000), Paused("t1", "r1", prompt: 500, completion: 0));

        var decision = Run(learned.State, Request("t2", "r2", bytes: 1000, at: 3), Request("t3", "r3", bytes: 1000, at: 3), Request("t4", "r4", bytes: 1000, at: 3));

        Assert.Equal(["t2", "t3"], RoutedTurns(decision));
    }

    [Fact]
    public void A_replica_without_a_known_memory_size_takes_any_number_of_turns()
    {
        var decision = Run(SchedulerState.Initial([A]),
            Request("t1", "r1", bytes: 40000), Request("t2", "r2", bytes: 40000), Request("t3", "r3", bytes: 40000));

        Assert.Equal(["t1", "t2", "t3"], RoutedTurns(decision));
    }

    [Fact]
    public void Paused_interactive_runs_do_not_hold_back_batch_turns_on_a_replica_with_a_known_pool()
    {
        var paused = Run(WithPool(SchedulerState.Initial([A]), "a", 1000),
            Request("i1", "chat", bytes: 400), Paused("i1", "chat"));

        var batch = Decider.Decide(paused.State, new TurnRequested("b1", "job", EngineClass.Generative, "m", false, 3, ServiceClass.Batch, 400));

        Assert.Equal(["b1"], RoutedTurns(batch));
    }

    [Fact]
    public void A_paused_run_waits_for_its_own_replica_rather_than_moving_to_one_without_its_context()
    {
        var paused = Run(SchedulerState.Initial([A, B]), Request("r1-1", "r1"), Paused("r1-1", "r1")).State;
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
    public void A_resumed_turn_of_a_run_that_looks_finished_still_returns_to_its_replica()
    {
        var idle = Run(SchedulerState.Initial([A, B]), Request("r1-1", "r1"), Finished("r1-1", "r1", finish: "length")).State;
        var aFull = idle with
        {
            Capacity = idle.Capacity.SetItem("a", 1),
            InFlight = idle.InFlight.SetItem("a", 1),
            Turns = idle.Turns.SetItem("x1", new Turn("x1", "x", "a", 2)),
        };

        var resumed = Decider.Decide(aFull, Request("r1-2", "r1", at: 3, resumed: true));

        Assert.Empty(RoutedTurns(resumed));
    }

    [Fact]
    public void A_paused_run_whose_replica_is_down_may_move()
    {
        var paused = Run(SchedulerState.Initial([A, B]),
            Request("r1-1", "r1"), Paused("r1-1", "r1"), new ReplicaHealthChanged("a", false, 2));

        var resumed = Decider.Decide(paused.State, Request("r1-2", "r1", at: 3));

        Assert.Equal("b", Assert.IsType<RouteTurn>(Assert.Single(resumed.Actions)).Replica.Id);
    }

    [Fact]
    public void An_ended_run_is_forgotten_and_its_paused_place_freed()
    {
        var initial = SchedulerState.Initial([A]);
        var paused = Run(initial with { Capacity = initial.Capacity.SetItem("a", 2) },
            Request("i1", "chat"), Paused("i1", "chat"),
            new TurnRequested("b1", "job", EngineClass.Generative, "m", false, 3, ServiceClass.Batch),
            new TurnRequested("b2", "job2", EngineClass.Generative, "m", false, 3, ServiceClass.Batch));

        var ended = Decider.Decide(paused.State, new RunEnded("chat", 4));

        Assert.Equal(["i1", "b1"], RoutedTurns(paused));
        Assert.Equal(["b2"], RoutedTurns(ended));
        Assert.False(ended.State.Runs.ContainsKey("chat"));
    }

    [Fact]
    public void A_load_report_records_the_engines_memory_size()
    {
        var decision = Decider.Decide(SchedulerState.Initial([A]), new ReplicaLoadReported("a", 0, 0, 0, 24000));

        Assert.Equal(24000, decision.State.KvPool["a"]);
    }
}
