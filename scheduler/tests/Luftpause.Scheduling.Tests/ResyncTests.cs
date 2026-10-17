using System.Collections.Immutable;

namespace Luftpause.Scheduling.Tests;

public class ResyncTests
{
    private static readonly Replica A = new("a", "http://a", EngineClass.Generative);
    private static readonly Replica B = new("b", "http://b", EngineClass.Generative);
    private static readonly Decider Decider = new(new AffinityRouting(new LeastLoadedRouting()));

    private static Run RunOn(string runId, RunPhase phase, string? replica, ServiceClass service = ServiceClass.Interactive, string caller = "") =>
        new(runId, phase, replica, 3, 100, 10, service, caller);

    private static SchedulerState Resynced(SchedulerState state, Run[] runs, Turn[] turns) =>
        Decider.Decide(state, new StateResynced(new Snapshot([.. runs], [.. turns]), 500)).State;

    [Fact]
    public void Rebuilds_turns_and_in_flight_counts_from_the_snapshot()
    {
        var stale = Decider.Decide(SchedulerState.Initial([A, B]), new TurnRequested("old", "run-0", EngineClass.Generative, "m", false, 0)).State;

        var state = Resynced(stale, [RunOn("run-1", new Generating("t1"), "a")], [new Turn("t1", "run-1", "a", 50, "c1"), new Turn("t2", "run-2", "a", 60)]);

        Assert.Equal(["t1", "t2"], state.Turns.Keys.Order());
        Assert.Equal(2, state.InFlightOn("a"));
        Assert.Equal(0, state.InFlightOn("b"));
        Assert.Equal(["run-1"], state.Runs.Keys);
        Assert.Equal("c1", state.Turns["t1"].Caller);
    }

    [Fact]
    public void Keeps_each_runs_phase_class_caller_and_affinity()
    {
        var state = Resynced(
            SchedulerState.Initial([A, B]),
            [
                RunOn("generating", new Generating("t1"), "a", ServiceClass.Batch, "c1"),
                RunOn("paused", new AwaitingTool(90), "b", ServiceClass.Interactive, "c2"),
                RunOn("idle", new Idle(80), "a", ServiceClass.Batch, "c3"),
            ],
            [new Turn("t1", "generating", "a", 50)]);

        Assert.Equal(RunOn("generating", new Generating("t1"), "a", ServiceClass.Batch, "c1"), state.Runs["generating"]);
        Assert.Equal(RunOn("paused", new AwaitingTool(90), "b", ServiceClass.Interactive, "c2"), state.Runs["paused"]);
        Assert.Equal(RunOn("idle", new Idle(80), "a", ServiceClass.Batch, "c3"), state.Runs["idle"]);
        Assert.Equal(1, state.InteractivePausedOn("b"));
    }

    [Fact]
    public void Forgets_replicas_it_does_not_know_and_idles_runs_whose_turn_was_on_one()
    {
        var state = Resynced(
            SchedulerState.Initial([A]),
            [RunOn("run-1", new Generating("t1"), "gone"), RunOn("run-2", new AwaitingTool(90), "gone")],
            [new Turn("t1", "run-1", "gone", 50)]);

        Assert.Empty(state.Turns);
        Assert.DoesNotContain("gone", state.InFlight.Keys);
        Assert.Equal(RunOn("run-1", new Idle(100), null), state.Runs["run-1"]);
        Assert.Null(state.Runs["run-2"].LastReplicaId);
    }

    [Fact]
    public void Keeps_waiting_turns_health_and_capacity_and_drains_into_freed_capacity()
    {
        var capped = SchedulerState.Initial([A]) with { Capacity = ImmutableDictionary<string, int>.Empty.Add("a", 1) };
        var full = Decider.Decide(capped, new TurnRequested("t1", "run-1", EngineClass.Generative, "m", false, 0)).State;
        var held = Decider.Decide(full, new TurnRequested("t2", "run-2", EngineClass.Generative, "m", false, 1));
        Assert.Empty(held.Actions);

        var resync = Decider.Decide(held.State, new StateResynced(new Snapshot([], []), 2));

        Assert.Equal("t2", Assert.IsType<RouteTurn>(Assert.Single(resync.Actions)).TurnId);
        Assert.Equal(1, resync.State.Capacity["a"]);
        Assert.Empty(resync.State.Waiting);
    }

    [Fact]
    public void A_resynced_turn_completes_like_any_other()
    {
        var resynced = Resynced(SchedulerState.Initial([A]), [RunOn("run-1", new Generating("t1"), "a")], [new Turn("t1", "run-1", "a", 50)]);

        var state = Decider.Decide(resynced, new TurnCompleted("t1", "run-1", "tool_calls", new TokenUsage(1, 1, 0), false, 600)).State;

        Assert.Empty(state.Turns);
        Assert.Equal(0, state.InFlightOn("a"));
        Assert.Equal(new AwaitingTool(600), state.Runs["run-1"].Phase);
    }
}
