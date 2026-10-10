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

    private static Decision Run(SchedulerState state, params SchedulerEvent[] events) =>
        events.Aggregate(Decision.Unchanged(state), (d, e) => d.Then(s => Decider.Decide(s, e)));

    private static IEnumerable<string> RoutedTurns(Decision decision) =>
        decision.Actions.OfType<RouteTurn>().Select(r => r.TurnId);

    [Fact]
    public void Turns_go_straight_to_an_engine_that_has_reported_no_queue()
    {
        var decision = Run(SchedulerState.Initial([A]),
            Request("b1", ServiceClass.Batch), Request("b2", ServiceClass.Batch), Request("i1", ServiceClass.Interactive));

        Assert.Equal(["b1", "b2", "i1"], RoutedTurns(decision));
        Assert.Empty(decision.State.Waiting);
    }

    [Fact]
    public void An_engine_that_queues_caps_admission_at_what_it_runs()
    {
        var decision = Run(SchedulerState.Initial([A]),
            Request("b1", ServiceClass.Batch), Request("b2", ServiceClass.Batch), Request("b3", ServiceClass.Batch),
            Load(running: 2, waiting: 1), Request("b4", ServiceClass.Batch));

        Assert.Equal(2, decision.State.Capacity["a"]);
        Assert.Equal("b4", Assert.Single(decision.State.Waiting).TurnId);
    }

    [Fact]
    public void Batch_turns_may_take_every_turn_the_engine_runs()
    {
        var decision = Run(SchedulerState.Initial([A]), Load(running: 2, waiting: 1),
            Request("b1", ServiceClass.Batch), Request("b2", ServiceClass.Batch));

        Assert.Equal(["b1", "b2"], RoutedTurns(decision));
    }

    [Fact]
    public void Batch_turns_leave_room_for_each_interactive_run_paused_on_a_tool()
    {
        var paused = Run(SchedulerState.Initial([A]), Load(running: 3, waiting: 1),
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
        var settled = Run(SchedulerState.Initial([A]), Load(running: 2, waiting: 1),
            Request("p1", ServiceClass.Batch), Completed("p1", "tool_calls"),
            Request("i1", ServiceClass.Interactive), Completed("i1"));

        var flood = Run(settled.State, Request("b1", ServiceClass.Batch), Request("b2", ServiceClass.Batch));

        Assert.Equal(["b1", "b2"], RoutedTurns(flood));
    }

    [Fact]
    public void A_freed_turn_goes_to_the_waiting_interactive_turn_before_an_older_batch_turn()
    {
        var full = Run(SchedulerState.Initial([A]), Load(running: 2, waiting: 1),
            Request("i1", ServiceClass.Interactive), Request("i2", ServiceClass.Interactive),
            Request("b1", ServiceClass.Batch, at: 1), Request("i3", ServiceClass.Interactive, at: 2));

        var freed = Decider.Decide(full.State, Completed("i1"));

        Assert.Equal(["i3"], RoutedTurns(freed));
        Assert.Equal("b1", Assert.Single(freed.State.Waiting).TurnId);
    }

    [Fact]
    public void An_engine_with_no_queue_at_the_cap_is_opened_again()
    {
        var capped = Run(SchedulerState.Initial([A]), Load(running: 1, waiting: 1),
            Request("i1", ServiceClass.Interactive), Request("i2", ServiceClass.Interactive));

        var opened = Decider.Decide(capped.State, Load(running: 1, waiting: 0));

        Assert.Equal(["i2"], RoutedTurns(opened));
        Assert.False(opened.State.Capacity.ContainsKey("a"));
    }

    [Fact]
    public void An_engine_with_no_queue_below_the_cap_keeps_it()
    {
        var capped = Run(SchedulerState.Initial([A]), Load(running: 2, waiting: 1), Request("i1", ServiceClass.Interactive));

        var report = Decider.Decide(capped.State, Load(running: 1, waiting: 0));

        Assert.Equal(2, report.State.Capacity["a"]);
    }

    [Fact]
    public void A_waiting_turn_stays_queued_however_long_it_waits()
    {
        var full = Run(SchedulerState.Initial([A]), Load(running: 1, waiting: 1),
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
        var full = Run(SchedulerState.Initial([A]), Load(running: 1, waiting: 1),
            Request("i1", ServiceClass.Interactive), Request("i2", ServiceClass.Interactive));

        var waitingGone = Decider.Decide(full.State, new TurnAbandoned("i2", "i2", 1));
        var freed = Decider.Decide(waitingGone.State, new TurnAbandoned("i1", "i1", 2));

        Assert.Empty(waitingGone.State.Waiting);
        Assert.Equal(0, freed.State.InFlightOn("a"));
    }
}
