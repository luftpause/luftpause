using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Luftpause.Runtime;

public static class ApprovalPrompt
{
    private const string Allow = "allow";

    public static ElicitRequestParams Request(string handle, ApprovalRequest request) => new()
    {
        Message = $"luftpause run {handle} wants to call the tool '{request.Tool}' with {request.Arguments.ToJsonString()}. Allow this call?",
        RequestedSchema = new ElicitRequestParams.RequestSchema
        {
            Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
            {
                [Allow] = new ElicitRequestParams.BooleanSchema
                {
                    Title = "Allow",
                    Description = $"Let the sub-agent run '{request.Tool}' with these arguments.",
                },
            },
            Required = [Allow],
        },
    };

    public static bool Allowed(ElicitResult? result) =>
        result is { IsAccepted: true, Content: { } content }
        && content.TryGetValue(Allow, out var allow)
        && allow.ValueKind == JsonValueKind.True;
}
