namespace Luftpause.Scheduling.Tests;

public class KvBudgetTests
{
    private static readonly Replica A = new("a", "http://a", EngineClass.Generative);
    private static readonly Replica B = new("b", "http://b", EngineClass.Generative);

    private static TurnRequested Request(string model, int bytes) =>
        new("t", "r", EngineClass.Generative, model, false, 0, RequestBytes: bytes);

    private static SchedulerState Completed(SchedulerState state, Replica replica, string model, int bytes, int prompt, int completion) =>
        KvBudget.Record(state, KvBudget.Turn(state, Request(model, bytes), replica), new TokenUsage(prompt, completion, 0));

    private static int Need(SchedulerState state, Replica replica, string model, int bytes) =>
        KvBudget.Turn(state, Request(model, bytes), replica).KvTokens;

    private static SchedulerState TwoModelsOnA() =>
        Completed(Completed(SchedulerState.Initial([A, B]), A, "x", 1000, 500, 20), A, "y", 1000, 100, 40);

    [Fact]
    public void Two_models_on_one_replica_learn_separate_ratios()
    {
        var state = TwoModelsOnA();

        Assert.Equal(1000 + 20, Need(state, A, "x", 2000));
        Assert.Equal(200 + 40, Need(state, A, "y", 2000));
    }

    [Fact]
    public void A_model_unseen_on_a_replica_with_history_uses_the_replica_across_models()
    {
        Assert.Equal(600 + 30, Need(TwoModelsOnA(), A, "z", 2000));
    }

    [Fact]
    public void A_replica_without_history_uses_the_default_ratio()
    {
        Assert.Equal((int)(2000 * KvBudget.DefaultTokensPerByte), Need(TwoModelsOnA(), B, "x", 2000));
    }

    [Fact]
    public void A_turn_remembers_its_model()
    {
        Assert.Equal("x", KvBudget.Turn(SchedulerState.Initial([A]), Request("x", 10), A).Model);
    }
}
