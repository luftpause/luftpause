using System.Text.Json.Nodes;

namespace Luftpause.Runtime.Tests;

public sealed class RunJournalTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"runs-{Guid.NewGuid():N}.jsonl");

    public void Dispose() => File.Delete(path);

    private static readonly RunSpec Spec = new(
        "task", "model", "batch", "system", 4, new HashSet<string>(StringComparer.Ordinal) { "a", "b" }, 128, true);

    [Fact]
    public void Records_round_trip_through_the_file()
    {
        var started = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        var work = new RunWork(1, 1, 0, 100, 20, 10, 0.5, 0.25);
        var messages = new List<JsonObject>
        {
            new() { ["role"] = "assistant", ["content"] = null, ["tool_calls"] = new JsonArray(new JsonObject { ["id"] = "c1" }) },
            new() { ["role"] = "tool", ["tool_call_id"] = "c1", ["content"] = "result" },
        };
        using (var journal = new RunJournal(path))
        {
            journal.Append(new RunSpawned("run-1", Spec, started, "run-0"));
            journal.Append(new RunTurned("run-1", messages, work));
            journal.Append(new RunFinished("run-1", new RunOutcome(RunStatus.Completed, "answer", null), started.AddSeconds(3)));
        }

        var records = RunJournal.Replay(path);

        Assert.Equal(3, records.Count);
        var spawned = Assert.IsType<RunSpawned>(records[0]);
        Assert.Equal(("run-1", started, "run-0"), (spawned.Handle, spawned.StartedAt, spawned.Parent));
        Assert.Equal(Spec with { Tools = null }, spawned.Spec with { Tools = null });
        Assert.Equal(["a", "b"], spawned.Spec.Tools!.Order());
        var turned = Assert.IsType<RunTurned>(records[1]);
        Assert.Equal(work, turned.Work);
        Assert.Equal(messages.Select(m => m.ToJsonString()), turned.NewMessages.Select(m => m.ToJsonString()));
        var finished = Assert.IsType<RunFinished>(records[2]);
        Assert.Equal(new RunOutcome(RunStatus.Completed, "answer", null), finished.Outcome);
        Assert.Equal(started.AddSeconds(3), finished.FinishedAt);
    }

    [Fact]
    public void Replay_of_a_missing_file_is_empty() =>
        Assert.Empty(RunJournal.Replay(path));

    [Fact]
    public void A_truncated_last_line_is_ignored()
    {
        using (var journal = new RunJournal(path))
        {
            journal.Append(new RunSpawned("run-1", Spec, DateTimeOffset.UnixEpoch, null));
            journal.Append(new RunTurned("run-1", [], RunWork.None));
        }
        File.AppendAllText(path, "{\"kind\":\"turned\",\"Handle\":\"r");

        var records = RunJournal.Replay(path);

        Assert.Equal(2, records.Count);
    }

    [Fact]
    public void Reopening_after_a_truncated_line_drops_it_before_appending()
    {
        using (var journal = new RunJournal(path))
            journal.Append(new RunSpawned("run-1", Spec, DateTimeOffset.UnixEpoch, null));
        File.AppendAllText(path, "{\"kind\":\"turned\",\"Handle\":\"r");

        using (var journal = new RunJournal(path))
            journal.Append(new RunTurned("run-1", [], RunWork.None));

        Assert.Equal([typeof(RunSpawned), typeof(RunTurned)], RunJournal.Replay(path).Select(r => r.GetType()));
    }

    [Fact]
    public void A_malformed_line_before_the_last_throws()
    {
        File.WriteAllText(path, "{\"kind\":\"turned\",\"Handle\":\"r\n{\"kind\":\"finished\"}\n");

        Assert.ThrowsAny<System.Text.Json.JsonException>(() => RunJournal.Replay(path));
    }
}
