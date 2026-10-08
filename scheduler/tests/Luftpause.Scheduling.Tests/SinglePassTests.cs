namespace Luftpause.Scheduling.Tests;

public class SinglePassTests
{
    private static readonly Replica Generative = new("gen", "http://gen", EngineClass.Generative);
    private static readonly Replica ClassifierA = new("cls-a", "http://cls-a", EngineClass.SinglePass, Path: "/classify");
    private static readonly Replica ClassifierB = new("cls-b", "http://cls-b", EngineClass.SinglePass, Path: "/classify");
    private static readonly Decider Decider = new(new AffinityRouting(new LeastLoadedRouting()));

    private static TurnRequested Generate(string turnId, string runId = "r1", long at = 0) =>
        new(turnId, runId, EngineClass.Generative, "m", false, at);

    private static TurnRequested Decide(string turnId, string runId = "r1", long at = 0) =>
        new(turnId, runId, EngineClass.SinglePass, "c", false, at);

    private static Decision Run(SchedulerState state, params SchedulerEvent[] events) =>
        events.Aggregate(Decision.Unchanged(state), (d, e) => d.Then(s => Decider.Decide(s, e)));

    [Fact]
    public void A_decision_goes_to_a_single_pass_replica_outside_run_state()
    {
        var decision = Decider.Decide(SchedulerState.Initial([Generative, ClassifierA]), Decide("d1"));

        var route = Assert.IsType<RouteTurn>(Assert.Single(decision.Actions));
        Assert.Equal(ClassifierA, route.Replica);
        Assert.Empty(decision.State.Runs);
    }

    [Fact]
    public void A_decision_inside_a_paused_run_leaves_the_run_alone()
    {
        var paused = Run(SchedulerState.Initial([Generative, ClassifierA]),
            Generate("t1"), new TurnCompleted("t1", "r1", "tool_calls", new TokenUsage(100, 10, 0), false, 1));
        var before = paused.State.Runs["r1"];

        var decided = Run(paused.State, Decide("d1", at: 2), new TurnCompleted("d1", "r1", "", new TokenUsage(50, 0, 0), false, 3));

        Assert.Equal(before, decided.State.Runs["r1"]);
    }

    [Fact]
    public void Decisions_spread_across_single_pass_replicas_and_queue_when_all_are_full()
    {
        var decision = Run(SchedulerState.Initial([ClassifierA, ClassifierB]),
            new ReplicaLoadReported("cls-a", 2, 1, 0), new ReplicaLoadReported("cls-b", 2, 1, 0),
            Decide("d1", "a"), Decide("d2", "b"), Decide("d3", "c"), Decide("d4", "d"), Decide("d5", "e"));

        var routed = decision.Actions.OfType<RouteTurn>().ToList();
        Assert.Equal(4, routed.Count);
        Assert.Equal(2, routed.Count(r => r.Replica == ClassifierA));
        Assert.Equal("d5", Assert.Single(decision.State.Waiting).TurnId);
    }
}
