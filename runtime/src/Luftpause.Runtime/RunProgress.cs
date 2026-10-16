using System.Text.Json.Nodes;

namespace Luftpause.Runtime;

public sealed record RunProgress(RunWork Work, IReadOnlyList<JsonObject> NewMessages);

public sealed record RunStart(string RunId, RunSpec Spec, IReadOnlyList<JsonObject> Messages, RunWork Work);

public interface IRunLoop
{
    Task<RunOutcome> RunAsync(RunStart start, Action<RunProgress> report, CancellationToken cancel);
}
