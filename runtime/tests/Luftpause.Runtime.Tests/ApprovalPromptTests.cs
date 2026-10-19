using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace Luftpause.Runtime.Tests;

public sealed class ApprovalPromptTests
{
    private static readonly ApprovalRequest Drop = new("drop", JsonNode.Parse("""{"table":"users"}"""));

    private static ElicitResult Result(string action, string? allow) => new()
    {
        Action = action,
        Content = allow is null ? null : new Dictionary<string, JsonElement> { ["allow"] = JsonDocument.Parse(allow).RootElement },
    };

    [Fact]
    public void The_message_names_the_run_the_tool_and_its_arguments()
    {
        var request = ApprovalPrompt.Request("run-1", Drop);

        Assert.Contains("run-1", request.Message);
        Assert.Contains("drop", request.Message);
        Assert.Contains("""{"table":"users"}""", request.Message);
    }

    [Fact]
    public void The_schema_asks_for_one_required_boolean_allow()
    {
        var schema = ApprovalPrompt.Request("run-1", Drop).RequestedSchema!;

        Assert.IsType<ElicitRequestParams.BooleanSchema>(Assert.Single(schema.Properties, p => p.Key == "allow").Value);
        Assert.Single(schema.Properties);
        Assert.Equal(["allow"], schema.Required!);
    }

    [Fact]
    public void Accepting_with_allow_true_allows() =>
        Assert.True(ApprovalPrompt.Allowed(Result("accept", "true")));

    [Theory]
    [InlineData("accept", "false")]
    [InlineData("accept", null)]
    [InlineData("accept", "\"true\"")]
    [InlineData("decline", "true")]
    [InlineData("cancel", null)]
    public void Anything_else_declines(string action, string? allow) =>
        Assert.False(ApprovalPrompt.Allowed(Result(action, allow)));
}
