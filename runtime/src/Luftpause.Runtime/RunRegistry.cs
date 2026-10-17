using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;

namespace Luftpause.Runtime;

public sealed class RunRegistry(
    IRunLoop loop, RunJournal journal, IReadOnlyList<JournalRecord> history, LlmClient llm, TimeProvider time, ILogger<RunRegistry> log)
{
    private readonly ConcurrentDictionary<string, RunRecord> runs = new();
    private readonly ConcurrentDictionary<string, ImmutableList<string>> children = new();

    public RunRecord Spawn(RunSpec spec, string? parent = null)
    {
        if (parent is not null && !runs.ContainsKey(parent))
            throw new InvalidOperationException($"unknown parent handle '{parent}'");
        var record = new RunRecord($"run-{Guid.NewGuid():N}", spec, time.GetUtcNow(), new CancellationTokenSource(), parent);
        journal.Append(new RunSpawned(record.Handle, spec, record.Started, parent));
        Adopt(record);
        Start(record, parent is null ? Activity.Current?.Context ?? default : runs[parent].Trace);
        return record;
    }

    public void ResumeAll()
    {
        foreach (var replayed in ReplayedRun.From(history))
        {
            var record = replayed.Record();
            Adopt(record);
            if (replayed.Finished is null)
                Start(record, record.Parent is { } parent && runs.TryGetValue(parent, out var parentRecord) ? parentRecord.Trace : default);
            else
                runs[record.Handle] = record;
        }
    }

    public IReadOnlyCollection<RunRecord> All => [.. runs.Values];

    public RunRecord? Find(string handle) => runs.GetValueOrDefault(handle);

    public IReadOnlyList<string> Children(string handle) => children.GetValueOrDefault(handle, []);

    public RunView View(RunRecord record, DateTimeOffset now) => record.View(now, Children(record.Handle));

    public bool Cancel(string handle)
    {
        if (!runs.TryGetValue(handle, out var record))
            return false;
        var childCancelled = Children(handle).Select(Cancel).Count(cancelled => cancelled) > 0;
        if (record.Completion.IsCompleted)
            return childCancelled;
        record.Cancel.Cancel();
        return true;
    }

    public async Task<IReadOnlyList<RunView>> AwaitAsync(
        IReadOnlyList<string> handles, bool all, TimeSpan timeout, IProgress<RunView> progress, CancellationToken cancel)
    {
        var records = handles.Select(Find).OfType<RunRecord>().ToList();
        var deadline = Task.Delay(timeout, time, cancel);
        var reported = Views(records).Select(v => v.Status == RunStatus.Running ? (RunMark?)null : RunMark.Of(v)).ToList();
        while (true)
        {
            var signals = records.Select(r => r.WorkChanged).ToList();
            reported = Report(Views(records), reported, progress);
            if (!Waiting(records, all) || deadline.IsCompleted)
                break;
            await Task.WhenAny([deadline, .. signals, .. records.Select(r => r.Completion)]);
        }
        return Views(records);
    }

    private List<RunView> Views(IReadOnlyList<RunRecord> records)
    {
        var now = time.GetUtcNow();
        return records.Select(r => View(r, now)).ToList();
    }

    private static bool Waiting(IReadOnlyList<RunRecord> records, bool all) =>
        records.Any(r => !r.Completion.IsCompleted) && (all || records.All(r => !r.Completion.IsCompleted));

    private static List<RunMark?> Report(IReadOnlyList<RunView> views, IReadOnlyList<RunMark?> reported, IProgress<RunView> progress)
    {
        var current = views.Select(v => (RunMark?)RunMark.Of(v)).ToList();
        foreach (var view in views.Zip(current, reported).Where(v => v.Second != v.Third).Select(v => v.First))
            progress.Report(view);
        return current;
    }

    private sealed record RunMark(RunStatus Status, int Turns, int ToolCalls)
    {
        public static RunMark Of(RunView view) => new(view.Status, view.Turns, view.ToolCalls);
    }

    private void Adopt(RunRecord record)
    {
        if (record.Parent is { } parent)
            children.AddOrUpdate(parent, _ => [record.Handle], (_, siblings) => siblings.Add(record.Handle));
    }

    private void Start(RunRecord record, ActivityContext parentTrace)
    {
        runs[record.Handle] = record;
        record.Completion = ExecuteAsync(record, parentTrace);
    }

    private async Task<RunOutcome> ExecuteAsync(RunRecord record, ActivityContext parentTrace)
    {
        using var activity = Tracing.Source.StartActivity("invoke_agent", ActivityKind.Internal, parentTrace, GenAiTags.Run(record.Handle, record.Spec));
        record.Trace = activity?.Context ?? default;
        var outcome = await OutcomeAsync(record);
        activity?.SetTags(GenAiTags.RunOutcome(outcome));
        if (outcome.Status == RunStatus.Failed)
            activity?.SetStatus(ActivityStatusCode.Error, outcome.Error);
        var finishedAt = time.GetUtcNow();
        record.Finished = finishedAt;
        journal.Append(new RunFinished(record.Handle, outcome, finishedAt));
        await EndAsync(record.Handle);
        return outcome;
    }

    private void Progress(RunRecord record, RunProgress progress)
    {
        record.Work = progress.Work;
        if (progress.NewMessages.Count == 0)
            return;
        journal.Append(new RunTurned(record.Handle, progress.NewMessages, progress.Work));
        record.Messages = [.. record.Messages, .. progress.NewMessages];
    }

    private async Task EndAsync(string handle)
    {
        try
        {
            await llm.EndRunAsync(handle, CancellationToken.None);
        }
        catch (HttpRequestException e)
        {
            log.LogWarning(e, "Run {Handle} ended but the data plane was not told", handle);
        }
    }

    private async Task<RunOutcome> OutcomeAsync(RunRecord record)
    {
        await Task.Yield();
        try
        {
            return await loop.RunAsync(
                new RunStart(record.Handle, record.Spec, record.Messages, record.Work),
                progress => Progress(record, progress), record.Cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return new RunOutcome(RunStatus.Cancelled, null, "cancelled");
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Run {Handle} failed", record.Handle);
            return new RunOutcome(RunStatus.Failed, null, e.Message);
        }
    }
}
