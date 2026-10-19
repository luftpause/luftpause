using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace Luftpause.Runtime;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TaskCreated), "created")]
[JsonDerivedType(typeof(TaskCompleted), "completed")]
[JsonDerivedType(typeof(TaskFailed), "failed")]
[JsonDerivedType(typeof(TaskCancelled), "cancelled")]
[JsonDerivedType(typeof(TaskInputRequested), "inputRequested")]
[JsonDerivedType(typeof(TaskInputResolved), "inputResolved")]
public abstract record TaskEvent(string TaskId, DateTimeOffset At);

public sealed record TaskCreated(string TaskId, DateTimeOffset At, long PollIntervalMs) : TaskEvent(TaskId, At);

public sealed record TaskCompleted(string TaskId, DateTimeOffset At, JsonElement Result) : TaskEvent(TaskId, At);

public sealed record TaskFailed(string TaskId, DateTimeOffset At, JsonElement Error) : TaskEvent(TaskId, At);

public sealed record TaskCancelled(string TaskId, DateTimeOffset At) : TaskEvent(TaskId, At);

public sealed record TaskInputRequested(string TaskId, DateTimeOffset At, IReadOnlyDictionary<string, InputRequest> Requests) : TaskEvent(TaskId, At);

public sealed record TaskInputResolved(string TaskId, DateTimeOffset At, IReadOnlyList<string> Keys) : TaskEvent(TaskId, At);

public static class TaskState
{
    public static bool Terminal(McpTaskStatus status) =>
        status is McpTaskStatus.Completed or McpTaskStatus.Failed or McpTaskStatus.Cancelled;

    public static McpTaskInfo Apply(McpTaskInfo? task, TaskEvent change) => (task, change) switch
    {
        (null, TaskCreated created) => new McpTaskInfo(created.TaskId, McpTaskStatus.Working, created.At, created.At, PollIntervalMs: created.PollIntervalMs),
        (null, _) => throw new InvalidOperationException($"Task '{change.TaskId}' not found."),
        (_, TaskCreated) => throw new InvalidOperationException($"Task '{change.TaskId}' already exists."),
        ({ } current, _) when Terminal(current.Status) => current,
        ({ } current, TaskCompleted completed) => current with { Status = McpTaskStatus.Completed, Result = completed.Result, LastUpdatedAt = completed.At },
        ({ } current, TaskFailed failed) => current with { Status = McpTaskStatus.Failed, Error = failed.Error, LastUpdatedAt = failed.At },
        ({ } current, TaskCancelled cancelled) => current with { Status = McpTaskStatus.Cancelled, LastUpdatedAt = cancelled.At },
        ({ } current, TaskInputRequested requested) => current with
        {
            Status = McpTaskStatus.InputRequired,
            InputRequests = Requests(current).SetItems(requested.Requests),
            LastUpdatedAt = requested.At,
        },
        ({ } current, TaskInputResolved resolved) => Resolved(current, Requests(current).RemoveRange(resolved.Keys), resolved.At),
        _ => throw new InvalidOperationException($"Unknown task event {change.GetType().Name}."),
    };

    private static McpTaskInfo Resolved(McpTaskInfo task, ImmutableDictionary<string, InputRequest> open, DateTimeOffset at) =>
        task with
        {
            Status = open.IsEmpty ? McpTaskStatus.Working : McpTaskStatus.InputRequired,
            InputRequests = open.IsEmpty ? null : open,
            LastUpdatedAt = at,
        };

    private static ImmutableDictionary<string, InputRequest> Requests(McpTaskInfo task) =>
        task.InputRequests?.ToImmutableDictionary(StringComparer.Ordinal) ?? ImmutableDictionary.Create<string, InputRequest>(StringComparer.Ordinal);
}
