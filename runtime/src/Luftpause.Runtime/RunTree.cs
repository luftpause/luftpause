namespace Luftpause.Runtime;

public sealed record WorkTotals(
    int Turns, int ToolCalls, int FailedToolCalls, long PromptTokens, long CompletionTokens, double TurnSeconds, double ToolSeconds)
{
    public static WorkTotals Of(RunWork work) =>
        new(work.Turns, work.ToolCalls, work.FailedToolCalls, work.PromptTokens, work.CompletionTokens, work.TurnSeconds, work.ToolSeconds);

    public WorkTotals Plus(WorkTotals other) =>
        new(
            Turns + other.Turns,
            ToolCalls + other.ToolCalls,
            FailedToolCalls + other.FailedToolCalls,
            PromptTokens + other.PromptTokens,
            CompletionTokens + other.CompletionTokens,
            TurnSeconds + other.TurnSeconds,
            ToolSeconds + other.ToolSeconds);
}

public sealed record RunNode(
    string Handle,
    RunStatus Status,
    string Class,
    string? Parent,
    double ElapsedSeconds,
    WorkTotals Own,
    WorkTotals Subtree,
    IReadOnlyList<RunNode> Children);

public static class RunTree
{
    public static IReadOnlyList<RunNode> Build(IReadOnlyCollection<RunRecord> records, DateTimeOffset now)
    {
        var byParent = records.ToLookup(r => r.Parent);
        return NodesUnder(null, byParent, now);
    }

    private static IReadOnlyList<RunNode> NodesUnder(string? parent, ILookup<string?, RunRecord> byParent, DateTimeOffset now) =>
        byParent[parent]
            .OrderBy(r => r.Started)
            .ThenBy(r => r.Handle, StringComparer.Ordinal)
            .Select(r => NodeOf(r, NodesUnder(r.Handle, byParent, now), now))
            .ToList();

    private static RunNode NodeOf(RunRecord record, IReadOnlyList<RunNode> children, DateTimeOffset now)
    {
        var view = record.View(now, []);
        var own = WorkTotals.Of(record.Work);
        return new RunNode(
            record.Handle,
            view.Status,
            record.Spec.ServiceClass,
            record.Parent,
            view.ElapsedSeconds,
            own,
            children.Aggregate(own, (total, child) => total.Plus(child.Subtree)),
            children);
    }
}
