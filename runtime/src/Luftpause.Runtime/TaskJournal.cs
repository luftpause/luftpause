using System.Collections.Immutable;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace Luftpause.Runtime;

public sealed class TaskJournal : IMcpTaskStore, IDisposable
{
    private const long PollIntervalMs = 1000;
    private static readonly JsonSerializerOptions Options = new();

    private readonly Lock gate = new();
    private readonly TimeProvider time;
    private readonly JsonLines<TaskEvent> lines;
    private ImmutableDictionary<string, McpTaskInfo> tasks;

    public TaskJournal(string path, TimeProvider time)
    {
        this.time = time;
        tasks = JsonLines<TaskEvent>.Read(path, Options).Aggregate(
            ImmutableDictionary.Create<string, McpTaskInfo>(StringComparer.Ordinal),
            (all, change) => all.SetItem(change.TaskId, TaskState.Apply(all.GetValueOrDefault(change.TaskId), change)));
        lines = new JsonLines<TaskEvent>(path, Options);
        foreach (var interrupted in tasks.Values.Where(t => !TaskState.Terminal(t.Status)))
            Record(new TaskFailed(interrupted.TaskId, time.GetUtcNow(), Interrupted));
    }

    public event Action<InputResponseReceivedEventArgs>? InputResponseReceived;

    public Task<McpTaskInfo> CreateTaskAsync(CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("N");
        Record(new TaskCreated(id, time.GetUtcNow(), PollIntervalMs));
        return Task.FromResult(tasks[id]);
    }

    public Task<McpTaskInfo?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default) =>
        Task.FromResult(tasks.GetValueOrDefault(taskId));

    public Task SetCompletedAsync(string taskId, JsonElement result, CancellationToken cancellationToken = default)
    {
        Record(new TaskCompleted(taskId, time.GetUtcNow(), result.Clone()));
        return Task.CompletedTask;
    }

    public Task SetFailedAsync(string taskId, JsonElement error, CancellationToken cancellationToken = default)
    {
        Record(new TaskFailed(taskId, time.GetUtcNow(), error.Clone()));
        return Task.CompletedTask;
    }

    public Task<bool> SetCancelledAsync(string taskId, CancellationToken cancellationToken = default) =>
        Task.FromResult(tasks.ContainsKey(taskId) && Record(new TaskCancelled(taskId, time.GetUtcNow())));

    public Task SetInputRequestsAsync(string taskId, IDictionary<string, InputRequest> inputRequests, CancellationToken cancellationToken = default)
    {
        Record(new TaskInputRequested(taskId, time.GetUtcNow(), inputRequests.ToImmutableDictionary(StringComparer.Ordinal)));
        return Task.CompletedTask;
    }

    public Task ResolveInputRequestsAsync(string taskId, IDictionary<string, InputResponse> inputResponses, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> resolved;
        lock (gate)
        {
            var task = tasks.GetValueOrDefault(taskId)
                ?? throw new McpProtocolException($"Unknown task: '{taskId}'", McpErrorCode.InvalidParams);
            resolved = TaskState.Terminal(task.Status)
                ? []
                : [.. inputResponses.Keys.Where(key => task.InputRequests?.ContainsKey(key) == true)];
            if (resolved.Count > 0)
                RecordLocked(new TaskInputResolved(taskId, time.GetUtcNow(), resolved));
        }
        foreach (var key in resolved)
            InputResponseReceived?.Invoke(new InputResponseReceivedEventArgs { TaskId = taskId, RequestId = key, Response = inputResponses[key] });
        return Task.CompletedTask;
    }

    public void Dispose() => lines.Dispose();

    private static JsonElement Interrupted =>
        JsonSerializer.SerializeToElement(
            new JsonRpcErrorDetail { Code = (int)McpErrorCode.InternalError, Message = "the runtime restarted while this task was running" },
            McpJsonUtilities.DefaultOptions);

    private bool Record(TaskEvent change)
    {
        lock (gate)
            return RecordLocked(change);
    }

    private bool RecordLocked(TaskEvent change)
    {
        var current = tasks.GetValueOrDefault(change.TaskId);
        var next = TaskState.Apply(current, change);
        if (ReferenceEquals(next, current))
            return false;
        lines.Append(change);
        tasks = tasks.SetItem(change.TaskId, next);
        return true;
    }
}
