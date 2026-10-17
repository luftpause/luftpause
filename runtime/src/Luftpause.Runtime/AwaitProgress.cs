using System.Text.Json;
using ModelContextProtocol;

namespace Luftpause.Runtime;

public sealed class AwaitProgress(IProgress<ProgressNotificationValue> notify) : IProgress<RunView>
{
    private int sent;

    public void Report(RunView value) =>
        notify.Report(new ProgressNotificationValue { Progress = Interlocked.Increment(ref sent), Message = Describe(value) });

    private static string Describe(RunView view) =>
        JsonSerializer.Serialize(new
        {
            handle = view.Handle,
            status = view.Status.ToString().ToLowerInvariant(),
            turns = view.Turns,
            toolCalls = view.ToolCalls,
            elapsedSeconds = (long)view.ElapsedSeconds,
        });
}
