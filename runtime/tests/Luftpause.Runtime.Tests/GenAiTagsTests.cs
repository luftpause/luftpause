using System.Text.Json.Nodes;

namespace Luftpause.Runtime.Tests;

public sealed class GenAiTagsTests
{
    private static readonly RunSpec Spec = new("task", "llama", "batch", null, 4, MaxTokens: 256);

    private static Dictionary<string, object?> Map(IEnumerable<KeyValuePair<string, object?>> tags) => tags.ToDictionary();

    [Fact]
    public void A_run_is_an_agent_invocation_identified_by_its_handle()
    {
        var tags = Map(GenAiTags.Run("run-1", Spec));

        Assert.Equal("invoke_agent", tags["gen_ai.operation.name"]);
        Assert.Equal("run-1", tags["gen_ai.agent.id"]);
        Assert.Equal("llama", tags["gen_ai.request.model"]);
        Assert.Equal("batch", tags["luftpause.service_class"]);
    }

    [Fact]
    public void A_failed_run_carries_an_error_type()
    {
        var failed = Map(GenAiTags.RunOutcome(new RunOutcome(RunStatus.Failed, null, "boom")));
        var completed = Map(GenAiTags.RunOutcome(new RunOutcome(RunStatus.Completed, "ok", null)));

        Assert.Equal("Failed", failed["luftpause.run.status"]);
        Assert.Equal("run_failed", failed["error.type"]);
        Assert.Equal("Completed", completed["luftpause.run.status"]);
        Assert.False(completed.ContainsKey("error.type"));
    }

    [Fact]
    public void A_chat_request_names_the_model_and_its_token_limit()
    {
        var tags = Map(GenAiTags.ChatRequest(Spec));

        Assert.Equal("chat", tags["gen_ai.operation.name"]);
        Assert.Equal("llama", tags["gen_ai.request.model"]);
        Assert.Equal(256, tags["gen_ai.request.max_tokens"]);
    }

    [Fact]
    public void A_chat_request_without_a_token_limit_leaves_it_out()
    {
        Assert.False(Map(GenAiTags.ChatRequest(Spec with { MaxTokens = null })).ContainsKey("gen_ai.request.max_tokens"));
    }

    [Fact]
    public void A_chat_response_reports_usage_and_finish_reason()
    {
        var tags = Map(GenAiTags.ChatResponse(new LlmReply("hi", [], "stop", new TokenUsage(120, 100, 7))));

        Assert.Equal(120, tags["gen_ai.usage.input_tokens"]);
        Assert.Equal(7, tags["gen_ai.usage.output_tokens"]);
        Assert.Equal(new[] { "stop" }, tags["gen_ai.response.finish_reasons"]);
    }

    [Fact]
    public void A_chat_response_without_a_finish_reason_leaves_it_out()
    {
        var tags = Map(GenAiTags.ChatResponse(new LlmReply("hi", [], null, new TokenUsage(1, null, 1))));

        Assert.False(tags.ContainsKey("gen_ai.response.finish_reasons"));
    }

    [Fact]
    public void A_tool_call_names_the_tool_and_the_call()
    {
        var call = new JsonObject { ["id"] = "c1", ["function"] = new JsonObject { ["name"] = "search", ["arguments"] = "{}" } };

        var tags = Map(GenAiTags.Tool(call));

        Assert.Equal("execute_tool", tags["gen_ai.operation.name"]);
        Assert.Equal("search", tags["gen_ai.tool.name"]);
        Assert.Equal("c1", tags["gen_ai.tool.call.id"]);
    }
}
