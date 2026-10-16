using System.Globalization;
using ModelContextProtocol;

namespace Luftpause.Runtime;

public sealed class AwaitProgress(IProgress<ProgressNotificationValue> notify) : IProgress<RunView>
{
    private int sent;

    public void Report(RunView value) =>
        notify.Report(new ProgressNotificationValue { Progress = Interlocked.Increment(ref sent), Message = Describe(value) });

    private static string Describe(RunView view) =>
        string.Create(CultureInfo.InvariantCulture, $"{view.Handle} turns={view.Turns} toolCalls={view.ToolCalls} elapsed={view.ElapsedSeconds:0.0}s");
}
