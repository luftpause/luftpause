namespace Luftpause.Scheduling.Tests;

public class StatusViewTests
{
    private static readonly Replica A = new("a", "http://a", EngineClass.Generative);
    private static readonly Replica S = new("s", "http://s", EngineClass.SinglePass);

    private static TurnRequested Held(string turnId, ServiceClass service, string caller, long at, string? runId = null) =>
        new(turnId, runId ?? turnId, EngineClass.Generative, "m", false, at, service, Caller: caller);

    [Fact]
    public void An_empty_pool_has_no_replicas_and_zero_totals()
    {
        var view = StatusView.From(SchedulerState.Initial([]), 1_000);

        Assert.Empty(view.Replicas);
        Assert.Empty(view.Held);
        Assert.Empty(view.Runs);
        Assert.Equal(new StatusTotals(0, 0, 0, 0, 0), view.Totals);
    }

    [Fact]
    public void A_replica_without_probed_limits_reports_them_as_unknown()
    {
        var view = StatusView.From(SchedulerState.Initial([A]), 0);

        Assert.Equal([new ReplicaStatus("a", EngineClass.Generative, true, 0, null, 0)], view.Replicas);
    }

    [Fact]
    public void A_replica_shows_its_load_limits_health_and_active_runs()
    {
        var initial = SchedulerState.Initial([A, S]);
        var state = initial with
        {
            InFlight = initial.InFlight.SetItem("a", 2),
            Turns = initial.Turns
                .Add("t1", new Turn("t1", "r1", "a", 0))
                .Add("t2", new Turn("t2", "r2", "a", 0)),
            Capacity = initial.Capacity.Add("a", 8),
            Unhealthy = initial.Unhealthy.Add("s"),
        };
        state = state
            .WithRun(new Run("r1", new Generating("t1"), "a", 1, 0, 0))
            .WithRun(new Run("r2", new AwaitingTool(0), "a", 1, 0, 0))
            .WithRun(new Run("r3", new Idle(0), "a", 1, 0, 0));

        var view = StatusView.From(state, 0);

        Assert.Equal(
            [
                new ReplicaStatus("a", EngineClass.Generative, true, 2, 8, 2),
                new ReplicaStatus("s", EngineClass.SinglePass, false, 0, null, 0),
            ],
            view.Replicas);
        Assert.Equal(new StatusTotals(2, 1, 2, 0, 3), view.Totals);
    }

    [Fact]
    public void Held_turns_group_by_class_and_caller_with_the_oldest_wait()
    {
        var initial = SchedulerState.Initial([A]);
        var state = initial with
        {
            Waiting =
            [
                Held("b1", ServiceClass.Batch, "x", at: 1_000),
                Held("b2", ServiceClass.Batch, "x", at: 4_000),
                Held("b3", ServiceClass.Batch, "y", at: 7_000),
                Held("i1", ServiceClass.Interactive, "x", at: 9_000),
            ],
        };

        var view = StatusView.From(state, 10_000);

        Assert.Equal(
            [
                new HeldGroup(ServiceClass.Interactive, "x", 1, 1.0),
                new HeldGroup(ServiceClass.Batch, "x", 2, 9.0),
                new HeldGroup(ServiceClass.Batch, "y", 1, 3.0),
            ],
            view.Held);
        Assert.Equal(4, view.Totals.Held);
    }

    [Fact]
    public void A_held_turn_counts_against_its_runs_caller()
    {
        var state = SchedulerState.Initial([A])
            .WithRun(new Run("r1", new Idle(0), "a", 1, 0, 0, ServiceClass.Batch, "owner"));
        state = state with { Waiting = [Held("t2", ServiceClass.Batch, "", at: 0, runId: "r1")] };

        var view = StatusView.From(state, 0);

        Assert.Equal("owner", Assert.Single(view.Held).Caller);
    }

    [Fact]
    public void Runs_group_by_phase_and_class()
    {
        var state = SchedulerState.Initial([A])
            .WithRun(new Run("r1", new Generating("t1"), "a", 1, 0, 0))
            .WithRun(new Run("r2", new Generating("t2"), "a", 1, 0, 0, ServiceClass.Batch))
            .WithRun(new Run("r3", new Generating("t3"), "a", 1, 0, 0, ServiceClass.Batch))
            .WithRun(new Run("r4", new AwaitingTool(0), "a", 1, 0, 0));

        var view = StatusView.From(state, 0);

        Assert.Equal(
            [
                new RunGroup("AwaitingTool", ServiceClass.Interactive, 1),
                new RunGroup("Generating", ServiceClass.Interactive, 1),
                new RunGroup("Generating", ServiceClass.Batch, 2),
            ],
            view.Runs);
    }
}
