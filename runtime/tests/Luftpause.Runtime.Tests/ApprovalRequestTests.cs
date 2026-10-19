using System.Text.Json.Nodes;

namespace Luftpause.Runtime.Tests;

public sealed class ApprovalRequestTests
{
    private static JsonObject Call(string arguments) => new()
    {
        ["id"] = "c1",
        ["type"] = "function",
        ["function"] = new JsonObject { ["name"] = "drop", ["arguments"] = arguments },
    };

    [Fact]
    public void A_tool_call_becomes_its_tool_and_parsed_arguments()
    {
        var request = ApprovalRequest.Of(Call("""{"table":"users"}"""));

        Assert.Equal("drop", request.Tool);
        Assert.Equal("""{"table":"users"}""", request.Arguments!.ToJsonString());
    }

    [Fact]
    public void Arguments_that_are_not_json_are_kept_as_text()
    {
        var request = ApprovalRequest.Of(Call("table=users"));

        Assert.Equal("table=users", request.Arguments!.GetValue<string>());
    }

    [Fact]
    public void Empty_arguments_are_an_empty_object() =>
        Assert.Equal("{}", ApprovalRequest.Of(Call("")).Arguments!.ToJsonString());

    [Fact]
    public void A_decision_matches_the_same_tool_and_equal_arguments_only()
    {
        var request = ApprovalRequest.Of(Call("""{"table":"users","cascade":true}"""));

        Assert.True(request.Matches(new RunApprovalDecided("run-1", "drop", JsonNode.Parse("""{"cascade":true,"table":"users"}"""), true)));
        Assert.False(request.Matches(new RunApprovalDecided("run-1", "drop", JsonNode.Parse("""{"table":"orders"}"""), true)));
        Assert.False(request.Matches(new RunApprovalDecided("run-1", "truncate", JsonNode.Parse("""{"table":"users","cascade":true}"""), true)));
    }
}
