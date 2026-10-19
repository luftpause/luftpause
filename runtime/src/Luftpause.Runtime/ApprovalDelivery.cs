using System.Text.Json;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Luftpause.Runtime;

public static class ApprovalDelivery
{
    public static IApprovalAsker? Asker(McpServer server, RequestContext<CallToolRequestParams> request, bool inTask, string? requestState) =>
        ApprovalChannels.Choose(inTask, server.ClientCapabilities, request.JsonRpcRequest.Context?.ClientCapabilities, ProtocolVersion(request)) switch
        {
            ApprovalChannel.Elicit => new ElicitingAsker(server),
            ApprovalChannel.InputRequired => new InputRequiredAsker(requestState),
            _ => null,
        };

    public static bool TaskOptIn(RequestContext<CallToolRequestParams> request) =>
        ApprovalChannels.Sessionless(ProtocolVersion(request))
        && request.JsonRpcRequest.Context?.ClientCapabilities?.Extensions?.ContainsKey(TasksProtocol.ExtensionId) == true;

    public static void Answer(RunRegistry runs, IReadOnlyList<string> handles, IDictionary<string, InputResponse>? responses, ILogger log)
    {
        foreach (var (key, response) in responses ?? new Dictionary<string, InputResponse>())
        {
            var result = response.Deserialize(InputResponse.ElicitResultJsonTypeInfo);
            log.LogInformation("approval_answered approval={Approval} action={Action} content={Content}",
                key, result?.Action, result?.Content is { } content ? JsonSerializer.Serialize(content) : null);
            foreach (var handle in handles)
                runs.Decide(handle, key, ApprovalPrompt.Allowed(result));
        }
    }

    private static string? ProtocolVersion(RequestContext<CallToolRequestParams> request) =>
        request.JsonRpcRequest.Context?.ProtocolVersion;

    private sealed class ElicitingAsker(McpServer server) : IApprovalAsker
    {
        public async Task<bool> AskAsync(string handle, PendingApproval approval, CancellationToken cancel) =>
            ApprovalPrompt.Allowed(await server.ElicitAsync(ApprovalPrompt.Request(handle, approval.Request), cancel));
    }

    private sealed class InputRequiredAsker(string? requestState) : IApprovalAsker
    {
        public Task<bool> AskAsync(string handle, PendingApproval approval, CancellationToken cancel) =>
            throw new InputRequiredException(
                new Dictionary<string, InputRequest> { [approval.Id] = InputRequest.ForElicitation(ApprovalPrompt.Request(handle, approval.Request)) },
                requestState);
    }
}
