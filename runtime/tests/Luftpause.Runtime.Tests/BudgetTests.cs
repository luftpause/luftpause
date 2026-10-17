namespace Luftpause.Runtime.Tests;

public sealed class BudgetTests
{
    private static readonly WorkTotals Spent = new(Turns: 3, ToolCalls: 4, FailedToolCalls: 1, PromptTokens: 900, CompletionTokens: 100, TurnSeconds: 2, ToolSeconds: 1);

    [Fact]
    public void Nothing_is_exceeded_within_every_limit()
    {
        Assert.Null(Budget.Check(new BudgetLimits(1000, 4, 10), Spent, 9.9));
    }

    [Fact]
    public void Tokens_count_prompt_plus_completion()
    {
        Assert.Equal(new BudgetOverrun("tokens", 1000, 999), Budget.Check(new BudgetLimits(999, null, null), Spent, 0));
    }

    [Fact]
    public void Tool_calls_trip_past_the_limit()
    {
        Assert.Equal(new BudgetOverrun("toolCalls", 4, 3), Budget.Check(new BudgetLimits(null, 3, null), Spent, 0));
    }

    [Fact]
    public void Seconds_trip_when_the_limit_is_reached()
    {
        Assert.Equal(new BudgetOverrun("seconds", 10, 10), Budget.Check(new BudgetLimits(null, null, 10), Spent, 10));
    }

    [Fact]
    public void The_first_exceeded_limit_is_reported()
    {
        Assert.Equal("tokens", Budget.Check(new BudgetLimits(10, 1, 1), Spent, 5)!.Which);
    }

    [Fact]
    public void Absent_limits_never_trip()
    {
        Assert.Null(Budget.Check(new BudgetLimits(null, null, null), Spent, 1e9));
    }

    [Fact]
    public void An_overrun_describes_which_limit_and_by_how_much()
    {
        Assert.Equal("budget exceeded: seconds 30.5/30", new BudgetOverrun("seconds", 30.5, 30).Describe());
    }

    [Fact]
    public void No_limits_make_no_budget()
    {
        Assert.Null(BudgetLimits.Of(null, null, null));
    }

    [Theory]
    [InlineData(0L, null, null)]
    [InlineData(null, -1, null)]
    [InlineData(null, null, 0.0)]
    [InlineData(null, null, double.NaN)]
    public void Limits_must_be_positive(long? tokens, int? toolCalls, double? seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BudgetLimits.Of(tokens, toolCalls, seconds));
    }
}
