using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace Luftpause.Runtime.Tests;

public sealed class TaskJournalTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"tasks-{Guid.NewGuid():N}.jsonl");
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
    private readonly List<TaskJournal> opened = [];

    public void Dispose()
    {
        foreach (var store in opened)
            store.Dispose();
        File.Delete(path);
    }

    private TaskJournal Open()
    {
        foreach (var store in opened)
            store.Dispose();
        var journal = new TaskJournal(path, clock);
        opened.Add(journal);
        return journal;
    }

    private static readonly JsonElement Result = JsonDocument.Parse("""{"content":[{"type":"text","text":"done"}]}""").RootElement;
    private static readonly JsonElement Error = JsonDocument.Parse("""{"code":-32603,"message":"boom"}""").RootElement;

    private static InputRequest Ask(string message) =>
        InputRequest.ForElicitation(new ElicitRequestParams { Message = message, RequestedSchema = new() });

    private static InputResponse Allow() =>
        InputResponse.FromElicitResult(new ElicitResult { Action = "accept" });

    private int Lines()
    {
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        return reader.ReadToEnd().Split('\n').Count(l => l.Length > 0);
    }

    [Fact]
    public async Task A_created_task_is_working_and_found_by_its_id()
    {
        var store = Open();

        var created = await store.CreateTaskAsync();
        var found = await store.GetTaskAsync(created.TaskId);

        Assert.Equal(McpTaskStatus.Working, created.Status);
        Assert.Equal(clock.GetUtcNow(), created.CreatedAt);
        Assert.NotNull(created.PollIntervalMs);
        Assert.Equal(created, found);
        Assert.Null(await store.GetTaskAsync("missing"));
    }

    [Fact]
    public async Task Tasks_round_trip_through_the_file()
    {
        var store = Open();
        var completed = await store.CreateTaskAsync();
        var failed = await store.CreateTaskAsync();
        var cancelled = await store.CreateTaskAsync();
        clock.Advance(TimeSpan.FromSeconds(3));
        await store.SetCompletedAsync(completed.TaskId, Result);
        await store.SetFailedAsync(failed.TaskId, Error);
        await store.SetCancelledAsync(cancelled.TaskId);

        var reopened = Open();

        var done = (await reopened.GetTaskAsync(completed.TaskId))!;
        Assert.Equal((McpTaskStatus.Completed, completed.CreatedAt, clock.GetUtcNow()), (done.Status, done.CreatedAt, done.LastUpdatedAt));
        Assert.Equal(Result.GetRawText(), done.Result!.Value.GetRawText());
        Assert.Equal(Error.GetRawText(), (await reopened.GetTaskAsync(failed.TaskId))!.Error!.Value.GetRawText());
        Assert.Equal(McpTaskStatus.Cancelled, (await reopened.GetTaskAsync(cancelled.TaskId))!.Status);
    }

    [Fact]
    public async Task Terminal_transitions_are_idempotent_and_write_nothing()
    {
        var store = Open();
        var task = await store.CreateTaskAsync();
        await store.SetCompletedAsync(task.TaskId, Result);
        var lines = Lines();

        await store.SetFailedAsync(task.TaskId, Error);
        await store.SetCompletedAsync(task.TaskId, Error);
        var cancelled = await store.SetCancelledAsync(task.TaskId);
        await store.SetInputRequestsAsync(task.TaskId, new Dictionary<string, InputRequest> { ["a"] = Ask("late") });

        var info = (await store.GetTaskAsync(task.TaskId))!;
        Assert.Equal(McpTaskStatus.Completed, info.Status);
        Assert.Equal(Result.GetRawText(), info.Result!.Value.GetRawText());
        Assert.False(cancelled);
        Assert.Equal(lines, Lines());
    }

    [Fact]
    public async Task Cancelling_a_working_task_returns_true_once()
    {
        var store = Open();
        var task = await store.CreateTaskAsync();

        Assert.True(await store.SetCancelledAsync(task.TaskId));
        Assert.False(await store.SetCancelledAsync(task.TaskId));
        Assert.False(await store.SetCancelledAsync("missing"));
    }

    [Fact]
    public async Task Input_requests_wait_in_input_required_and_answers_raise_the_event_and_resume_work()
    {
        var store = Open();
        var task = await store.CreateTaskAsync();
        var heard = new List<InputResponseReceivedEventArgs>();
        store.InputResponseReceived += heard.Add;

        await store.SetInputRequestsAsync(task.TaskId, new Dictionary<string, InputRequest> { ["a"] = Ask("first"), ["b"] = Ask("second") });
        var waiting = (await store.GetTaskAsync(task.TaskId))!;
        await store.ResolveInputRequestsAsync(task.TaskId, new Dictionary<string, InputResponse> { ["a"] = Allow(), ["stale"] = Allow() });
        var half = (await store.GetTaskAsync(task.TaskId))!;
        await store.ResolveInputRequestsAsync(task.TaskId, new Dictionary<string, InputResponse> { ["b"] = Allow() });
        var resumed = (await store.GetTaskAsync(task.TaskId))!;

        Assert.Equal(McpTaskStatus.InputRequired, waiting.Status);
        Assert.Equal(["a", "b"], waiting.InputRequests!.Keys.Order());
        Assert.Equal("first", waiting.InputRequests["a"].ElicitationParams!.Message);
        Assert.Equal((McpTaskStatus.InputRequired, "b"), (half.Status, Assert.Single(half.InputRequests!).Key));
        Assert.Equal(McpTaskStatus.Working, resumed.Status);
        Assert.Empty(resumed.InputRequests ?? new Dictionary<string, InputRequest>());
        Assert.Equal([(task.TaskId, "a"), (task.TaskId, "b")], heard.Select(h => (h.TaskId, h.RequestId)));
    }

    [Fact]
    public async Task Answers_for_an_unknown_task_are_refused()
    {
        var store = Open();

        var error = await Assert.ThrowsAsync<McpProtocolException>(() =>
            store.ResolveInputRequestsAsync("missing", new Dictionary<string, InputResponse> { ["a"] = Allow() }));

        Assert.Equal(McpErrorCode.InvalidParams, error.ErrorCode);
    }

    [Fact]
    public async Task A_truncated_last_line_is_ignored_and_dropped_before_appending()
    {
        var store = Open();
        var first = await store.CreateTaskAsync();
        await store.SetCompletedAsync(first.TaskId, Result);
        store.Dispose();
        File.AppendAllText(path, "{\"kind\":\"failed\",\"TaskId\":\"" + first.TaskId);

        var reopened = Open();
        var second = await reopened.CreateTaskAsync();
        await reopened.SetFailedAsync(second.TaskId, Error);
        reopened.Dispose();

        var last = Open();
        Assert.Equal(McpTaskStatus.Completed, (await last.GetTaskAsync(first.TaskId))!.Status);
        Assert.Equal(Error.GetRawText(), (await last.GetTaskAsync(second.TaskId))!.Error!.Value.GetRawText());
    }

    [Fact]
    public async Task A_task_still_running_when_the_runtime_stopped_is_failed_on_restart()
    {
        var store = Open();
        var working = await store.CreateTaskAsync();
        var asking = await store.CreateTaskAsync();
        await store.SetInputRequestsAsync(asking.TaskId, new Dictionary<string, InputRequest> { ["a"] = Ask("allow?") });

        var reopened = Open();

        foreach (var id in new[] { working.TaskId, asking.TaskId })
        {
            var info = (await reopened.GetTaskAsync(id))!;
            Assert.Equal(McpTaskStatus.Failed, info.Status);
            Assert.Contains("restarted", info.Error!.Value.GetProperty("message").GetString());
        }
        Assert.Equal(McpTaskStatus.Failed, (await Open().GetTaskAsync(working.TaskId))!.Status);
    }
}
