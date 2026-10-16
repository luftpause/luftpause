using System.Collections.Concurrent;

namespace Luftpause.Runtime;

public sealed class RunRegistry(AgentLoop loop, LlmClient llm, TimeProvider time, ILogger<RunRegistry> log)
{
    private readonly ConcurrentDictionary<string, RunRecord> runs = new();

    public RunRecord Spawn(RunSpec spec)
    {
        var record = new RunRecord($"run-{Guid.NewGuid():N}", spec, time.GetUtcNow(), new CancellationTokenSource());
        runs[record.Handle] = record;
        record.Completion = ExecuteAsync(record);
        return record;
    }

    public RunRecord? Find(string handle) => runs.GetValueOrDefault(handle);

    public bool Cancel(string handle)
    {
        if (!runs.TryGetValue(handle, out var record) || record.Completion.IsCompleted)
            return false;
        record.Cancel.Cancel();
        return true;
    }

    public async Task<IReadOnlyList<RunView>> AwaitAsync(
        IReadOnlyList<string> handles, bool all, TimeSpan timeout, CancellationToken cancel)
    {
        var records = handles.Select(Find).OfType<RunRecord>().ToList();
        var pending = records.Select(r => r.Completion).Where(t => !t.IsCompleted).ToList();
        if (pending.Count > 0 && (all || records.All(r => !r.Completion.IsCompleted)))
        {
            Task waitFor = all ? Task.WhenAll(pending) : Task.WhenAny(pending);
            await Task.WhenAny(waitFor, Task.Delay(timeout, time, cancel));
        }
        var now = time.GetUtcNow();
        return records.Select(r => r.View(now)).ToList();
    }

    private async Task<RunOutcome> ExecuteAsync(RunRecord record)
    {
        var outcome = await OutcomeAsync(record);
        await EndAsync(record.Handle);
        return outcome;
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
            return await loop.RunAsync(record.Handle, record.Spec, work => record.Work = work, record.Cancel.Token);
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
        finally
        {
            record.Finished = time.GetUtcNow();
        }
    }
}
