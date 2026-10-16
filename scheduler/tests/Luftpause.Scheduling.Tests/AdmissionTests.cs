namespace Luftpause.Scheduling.Tests;

public class AdmissionTests
{
    private static readonly Replica A = new("a", "http://a", EngineClass.Generative);
    private static readonly Decider Decider = new(new AffinityRouting(new LeastLoadedRouting()));

    private static TurnRequested Request(string turnId, ServiceClass service, long at = 0, string? runId = null) =>
        new(turnId, runId ?? turnId, EngineClass.Generative, "m", false, at, service);

    private static TurnCompleted Completed(string turnId, string finishReason = "stop", int promptTokens = 100, long at = 1) =>
        new(turnId, turnId, finishReason, new TokenUsage(promptTokens, 10, 0), false, at);

    private static ReplicaLoadReported Load(int running, int waiting, string replica = "a") => new(replica, running, waiting, 0);

    private static SchedulerState CappedAt(int capacity) =>
        SchedulerState.Initial([A]) with { Capacity = SchedulerState.Initial([A]).Capacity.SetItem("a", capacity) };

    private static Decision Run(SchedulerState state, params SchedulerEvent[] events) =>
        events.Aggregate(Decision.Unchanged(state), (d, e) => d.Then(s => Decider.Decide(s, e)));

    private static IEnumerable<string> RoutedTurns(Decision decision) =>
        decision.Actions.OfType<RouteTurn>().Select(r => r.TurnId);

    [Fact]
    public void Turns_go_straight_to_an_engine_that_reports_no_load()
    {
        var decision = Run(SchedulerState.Initial([A]),
            Request("b1", ServiceClass.Batch), Request("b2", ServiceClass.Batch), Request("i1", ServiceClass.Interactive));

        Assert.Equal(["b1", "b2", "i1"], RoutedTurns(decision));
        Assert.Empty(decision.State.Waiting);
    }

    [Fact]
    public void An_engine_queueing_more_than_the_buffer_is_capped_so_its_queue_settles_at_the_buffer()
    {
        var decision = Run(CappedAt(6),
            Request("b1", ServiceClass.Batch), Request("b2", ServiceClass.Batch), Request("b3", ServiceClass.Batch),
            Request("b4", ServiceClass.Batch), Request("b5", ServiceClass.Batch), Request("b6", ServiceClass.Batch),
            Load(running: 2, waiting: 4), Request("b7", ServiceClass.Batch));

        Assert.Equal(4, decision.State.Capacity["a"]);
        Assert.Equal("b7", Assert.Single(decision.State.Waiting).TurnId);
    }

    [Fact]
    public void An_engine_at_the_cap_with_the_buffer_queued_keeps_its_cap()
    {
        var full = Run(CappedAt(3), Request("i1", ServiceClass.Interactive), Request("i2", ServiceClass.Interactive), Request("i3", ServiceClass.Interactive));

        var report = Decider.Decide(full.State, Load(running: 1, waiting: 2));

        Assert.Equal(3, report.State.Capacity["a"]);
    }

    [Fact]
    public void An_engine_queued_by_other_clients_still_takes_one_turn()
    {
        var decision = Run(SchedulerState.Initial([A]), Load(running: 0, waiting: 10), Request("i1", ServiceClass.Interactive));

        Assert.Equal(["i1"], RoutedTurns(decision));
    }

    [Fact]
    public void Batch_turns_may_take_every_turn_the_engine_runs()
    {
        var decision = Run(CappedAt(2),
            Request("b1", ServiceClass.Batch), Request("b2", ServiceClass.Batch));

        Assert.Equal(["b1", "b2"], RoutedTurns(decision));
    }

    [Fact]
    public void Batch_turns_leave_room_for_each_interactive_run_paused_on_a_tool()
    {
        var paused = Run(CappedAt(3),
            Request("i1", ServiceClass.Interactive, runId: "chat"), Completed("i1", "tool_calls") with { RunId = "chat" });

        var flood = Run(paused.State,
            Request("b1", ServiceClass.Batch), Request("b2", ServiceClass.Batch), Request("b3", ServiceClass.Batch));
        var resumed = Decider.Decide(flood.State, Request("i2", ServiceClass.Interactive, at: 2, runId: "chat") with { Resumed = true });

        Assert.Equal(["b1", "b2"], RoutedTurns(flood));
        Assert.Equal(["i2"], RoutedTurns(resumed));
    }

    [Fact]
    public void A_paused_batch_run_or_a_finished_interactive_run_holds_nothing()
    {
        var settled = Run(CappedAt(2),
            Request("p1", ServiceClass.Batch), Completed("p1", "tool_calls"),
            Request("i1", ServiceClass.Interactive), Completed("i1"));

        var flood = Run(settled.State, Request("b1", ServiceClass.Batch), Request("b2", ServiceClass.Batch));

        Assert.Equal(["b1", "b2"], RoutedTurns(flood));
    }

    [Fact]
    public void A_freed_turn_goes_to_the_waiting_interactive_turn_before_an_older_batch_turn()
    {
        var full = Run(CappedAt(2),
            Request("i1", ServiceClass.Interactive), Request("i2", ServiceClass.Interactive),
            Request("b1", ServiceClass.Batch, at: 1), Request("i3", ServiceClass.Interactive, at: 2));

        var freed = Decider.Decide(full.State, Completed("i1"));

        Assert.Equal(["i3"], RoutedTurns(freed));
        Assert.Equal("b1", Assert.Single(freed.State.Waiting).TurnId);
    }

    [Fact]
    public void A_freed_turn_goes_to_the_run_that_started_first_not_the_turn_that_arrived_first()
    {
        var full = Run(CappedAt(1),
            Request("old-1", ServiceClass.Interactive, at: 0, runId: "old"),
            new TurnCompleted("old-1", "old", "tool_calls", new TokenUsage(100, 10, 0), false, 5),
            Request("x1", ServiceClass.Interactive, at: 6, runId: "x"),
            Request("new-1", ServiceClass.Interactive, at: 7, runId: "new"),
            Request("old-2", ServiceClass.Interactive, at: 8, runId: "old"));

        var freed = Decider.Decide(full.State, new TurnCompleted("x1", "x", "stop", new TokenUsage(100, 10, 0), false, 9));

        Assert.Equal(["old-2"], RoutedTurns(freed));
        Assert.Equal("new-1", Assert.Single(freed.State.Waiting).TurnId);
    }

    [Fact]
    public void An_engine_first_reporting_no_queue_takes_as_many_turns_as_the_buffer()
    {
        var decision = Run(SchedulerState.Initial([A]), Load(running: 0, waiting: 0),
            Request("i1", ServiceClass.Interactive), Request("i2", ServiceClass.Interactive), Request("i3", ServiceClass.Interactive));

        Assert.Equal(["i1", "i2"], RoutedTurns(decision));
        Assert.Equal("i3", Assert.Single(decision.State.Waiting).TurnId);
    }

    [Fact]
    public void An_engine_at_the_cap_with_a_short_queue_grows_by_the_shortfall_not_by_the_whole_wait_queue()
    {
        var capped = Run(CappedAt(1),
            Request("i1", ServiceClass.Interactive), Request("i2", ServiceClass.Interactive),
            Request("i3", ServiceClass.Interactive), Request("i4", ServiceClass.Interactive));

        var grown = Decider.Decide(capped.State, Load(running: 1, waiting: 0));

        Assert.Equal(["i2", "i3"], RoutedTurns(grown));
        Assert.Equal(3, grown.State.Capacity["a"]);
        Assert.Equal("i4", Assert.Single(grown.State.Waiting).TurnId);
    }

    [Fact]
    public void An_engine_that_has_not_yet_seen_the_turns_sent_to_it_is_given_no_more()
    {
        var sent = Run(CappedAt(4),
            Request("i1", ServiceClass.Interactive), Request("i2", ServiceClass.Interactive),
            Request("i3", ServiceClass.Interactive), Request("i4", ServiceClass.Interactive), Request("i5", ServiceClass.Interactive));

        var report = Decider.Decide(sent.State, Load(running: 1, waiting: 0));

        Assert.Equal(4, report.State.Capacity["a"]);
        Assert.Empty(RoutedTurns(report));
    }

    [Fact]
    public void An_engine_below_the_cap_with_a_short_queue_keeps_it()
    {
        var capped = Run(CappedAt(4), Request("i1", ServiceClass.Interactive));

        var report = Decider.Decide(capped.State, Load(running: 1, waiting: 0));

        Assert.Equal(4, report.State.Capacity["a"]);
    }

    [Fact]
    public void A_waiting_turn_stays_queued_however_long_it_waits()
    {
        var full = Run(CappedAt(1),
            Request("i1", ServiceClass.Interactive), Request("i2", ServiceClass.Interactive, at: 0));

        var later = Decider.Decide(full.State, new Tick(Expiry.TurnTimeoutMs - 1));

        Assert.Empty(later.Actions);
        Assert.Equal("i2", Assert.Single(later.State.Waiting).TurnId);
    }

    [Fact]
    public void An_unhealthy_replica_gets_no_turns_until_it_recovers()
    {
        var down = Run(SchedulerState.Initial([A]), new ReplicaHealthChanged("a", false, 0), Request("i1", ServiceClass.Interactive));
        Assert.Empty(RoutedTurns(down));

        var up = Decider.Decide(down.State, new ReplicaHealthChanged("a", true, 1));

        Assert.Equal(["i1"], RoutedTurns(up));
    }

    [Fact]
    public void Abandoning_a_waiting_turn_removes_it_and_abandoning_a_routed_turn_frees_its_place()
    {
        var full = Run(CappedAt(1),
            Request("i1", ServiceClass.Interactive), Request("i2", ServiceClass.Interactive));

        var waitingGone = Decider.Decide(full.State, new TurnAbandoned("i2", "i2", 1));
        var freed = Decider.Decide(waitingGone.State, new TurnAbandoned("i1", "i1", 2));

        Assert.Empty(waitingGone.State.Waiting);
        Assert.Equal(0, freed.State.InFlightOn("a"));
    }
}
