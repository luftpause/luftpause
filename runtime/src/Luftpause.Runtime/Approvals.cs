using System.Text.Json;
using System.Text.Json.Nodes;

namespace Luftpause.Runtime;

public sealed record ApprovalRequest(string Tool, JsonNode Arguments)
{
    public static ApprovalRequest Of(JsonObject toolCall) =>
        new(toolCall["function"]?["name"]?.GetValue<string>() ?? "", Parsed(toolCall["function"]?["arguments"]?.GetValue<string>()));

    private static JsonNode Parsed(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return new JsonObject();
        try
        {
            return JsonNode.Parse(arguments) ?? JsonValue.Create(arguments);
        }
        catch (JsonException)
        {
            return JsonValue.Create(arguments);
        }
    }

    public bool Matches(RunApprovalDecided decided) =>
        decided.Tool == Tool && JsonNode.DeepEquals(decided.Arguments, Arguments);
}

public sealed class PendingApproval(ApprovalRequest request)
{
    private readonly TaskCompletionSource<bool> decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int decided;
    private int claimed;

    public string Id { get; } = $"approval-{Guid.NewGuid():N}";

    public ApprovalRequest Request { get; } = request;

    public Task<bool> Decision => decision.Task;

    public bool Open => Volatile.Read(ref decided) == 0;

    public bool TryDecide(bool allow, Action<bool> record)
    {
        if (Interlocked.Exchange(ref decided, 1) == 1)
            return false;
        record(allow);
        decision.SetResult(allow);
        return true;
    }

    public bool TryClaim() => Open && Interlocked.Exchange(ref claimed, 1) == 0;

    public void Release() => Volatile.Write(ref claimed, 0);
}

public interface IApprovalAsker
{
    Task<bool> AskAsync(string handle, PendingApproval approval, CancellationToken cancel);
}
