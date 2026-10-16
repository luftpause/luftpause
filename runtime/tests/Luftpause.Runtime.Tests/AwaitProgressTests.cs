using ModelContextProtocol;

namespace Luftpause.Runtime.Tests;

public class AwaitProgressTests
{
    private sealed class Sink : IProgress<ProgressNotificationValue>
    {
        public List<ProgressNotificationValue> Sent { get; } = [];

        public void Report(ProgressNotificationValue value) => Sent.Add(value);
    }

    private static RunView View(string handle, int turns, int toolCalls, double elapsed) =>
        new(handle, null, [], RunStatus.Running, null, null, turns, toolCalls, 0, elapsed, 0, null, 0, 0, 0);

    [Fact]
    public void Each_change_becomes_a_notification_naming_the_run_and_its_work()
    {
        var sink = new Sink();
        var progress = new AwaitProgress(sink);

        progress.Report(View("run-a", 2, 1, 3.25));

        Assert.Equal("run-a turns=2 toolCalls=1 elapsed=3.3s", Assert.Single(sink.Sent).Message);
    }

    [Fact]
    public void Progress_rises_with_every_notification_across_runs()
    {
        var sink = new Sink();
        var progress = new AwaitProgress(sink);

        progress.Report(View("run-a", 1, 0, 1));
        progress.Report(View("run-b", 1, 0, 1));
        progress.Report(View("run-a", 1, 1, 2));

        Assert.Equal([1f, 2f, 3f], sink.Sent.Select(v => v.Progress));
    }
}
