namespace Luftpause.Scheduling.Tests;

public class ModelRoutingTests
{
    private static readonly Replica Local = new("local", "http://sglang", EngineClass.Generative, Models: ["llama-3b"]);
    private static readonly Replica Hosted = new("hosted", "https://api.example.com", EngineClass.Generative, Models: ["gpt-big"]);
    private static readonly Replica Anything = new("any", "http://any", EngineClass.Generative);
    private static readonly Decider Decider = new(new AffinityRouting(new LeastLoadedRouting()));

    private static TurnRequested Request(string turnId, string model) =>
        new(turnId, turnId, EngineClass.Generative, model, false, 0);

    private static string RoutedTo(Decision decision) =>
        Assert.IsType<RouteTurn>(Assert.Single(decision.Actions)).Replica.Id;

    [Fact]
    public void A_turn_goes_only_to_a_replica_that_serves_its_model()
    {
        var state = SchedulerState.Initial([Local, Hosted]);

        Assert.Equal("hosted", RoutedTo(Decider.Decide(state, Request("t1", "gpt-big"))));
        Assert.Equal("local", RoutedTo(Decider.Decide(state, Request("t2", "llama-3b"))));
    }

    [Fact]
    public void A_replica_without_a_model_list_serves_any_model()
    {
        Assert.Equal("any", RoutedTo(Decider.Decide(SchedulerState.Initial([Local, Anything]), Request("t1", "something-else"))));
    }

    [Fact]
    public void A_model_nobody_serves_is_rejected_at_once_instead_of_queued()
    {
        var decision = Decider.Decide(SchedulerState.Initial([Local, Hosted]), Request("t1", "unknown"));

        var refuse = Assert.IsType<RefuseTurn>(Assert.Single(decision.Actions));
        Assert.Contains("unknown", refuse.Reason);
        Assert.Empty(decision.State.Waiting);
    }
}
