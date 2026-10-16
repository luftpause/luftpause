namespace Luftpause.Runtime.Tests;

public class RunWorkTests
{
    [Fact]
    public void Turns_add_their_tokens_and_time()
    {
        var work = RunWork.None
            .AfterTurn(new TokenUsage(120, 100, 9), TimeSpan.FromSeconds(1.5))
            .AfterTurn(new TokenUsage(200, 150, 20), TimeSpan.FromSeconds(0.5));

        Assert.Equal(new RunWork(2, 0, 0, 320, 250, 29, 2.0, 0), work);
    }

    [Fact]
    public void Cached_tokens_stay_unknown_once_a_turn_does_not_report_them()
    {
        var work = RunWork.None
            .AfterTurn(new TokenUsage(120, 100, 9), TimeSpan.FromSeconds(1))
            .AfterTurn(new TokenUsage(200, null, 20), TimeSpan.FromSeconds(1))
            .AfterTurn(new TokenUsage(300, 250, 20), TimeSpan.FromSeconds(1));

        Assert.Null(work.CachedTokens);
        Assert.Equal(620, work.PromptTokens);
    }

    [Fact]
    public void Tool_rounds_add_calls_failures_and_time()
    {
        var work = RunWork.None
            .AfterTools(1, 0, TimeSpan.FromSeconds(2))
            .AfterTools(2, 1, TimeSpan.FromSeconds(8));

        Assert.Equal(new RunWork(0, 3, 1, 0, 0, 0, 0, 10.0), work);
    }

    [Fact]
    public void A_cancelled_run_still_shows_the_work_it_did()
    {
        var record = new RunRecord("run-1", new RunSpec("task", "fake", "interactive", null, 8), DateTimeOffset.UnixEpoch, new CancellationTokenSource())
        {
            Completion = Task.FromResult(new RunOutcome(RunStatus.Cancelled, null, "cancelled")),
            Work = RunWork.None.AfterTurn(new TokenUsage(120, 100, 9), TimeSpan.FromSeconds(1)).AfterTools(1, 0, TimeSpan.FromSeconds(2)),
        };

        var view = record.View(DateTimeOffset.UnixEpoch.AddSeconds(5));

        Assert.Equal((RunStatus.Cancelled, 1, 1, 120L, 100L), (view.Status, view.Turns, view.ToolCalls, view.PromptTokens, view.CachedTokens));
    }
}
