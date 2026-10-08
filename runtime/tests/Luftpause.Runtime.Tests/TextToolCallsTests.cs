using System.Text.Json.Nodes;

namespace Luftpause.Runtime.Tests;

public class TextToolCallsTests
{
    private static readonly JsonArray Offered =
    [
        new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = "get_summary" } },
    ];

    private static (string? Name, string? Arguments) Recover(string? content)
    {
        var call = TextToolCalls.Recover(content, Offered);
        return (call?["function"]?["name"]?.GetValue<string>(), call?["function"]?["arguments"]?.GetValue<string>());
    }

    [Fact]
    public void A_llama_style_call_written_as_text_is_recovered()
    {
        var (name, arguments) = Recover("""{"name": "get_summary", "parameters": {"title": "Mekong"}}""");

        Assert.Equal("get_summary", name);
        Assert.Equal("""{"title":"Mekong"}""", arguments);
    }

    [Fact]
    public void Python_tag_and_code_fences_are_unwrapped()
    {
        Assert.Equal("get_summary", Recover("""<|python_tag|>{"name": "get_summary", "parameters": {"title": "Nile"}}""").Name);
        Assert.Equal("get_summary", Recover("```json\n{\"name\": \"get_summary\", \"arguments\": {\"title\": \"Nile\"}}\n```").Name);
    }

    [Fact]
    public void An_openai_shaped_function_object_is_recovered()
    {
        var (name, arguments) = Recover("""{"type": "function", "function": {"name": "get_summary", "arguments": "{\"title\": \"Volga\"}"}}""");

        Assert.Equal("get_summary", name);
        Assert.Equal("""{"title": "Volga"}""", arguments);
    }

    [Fact]
    public void Prose_and_calls_to_tools_the_run_was_not_offered_stay_answers()
    {
        Assert.Null(Recover("The Mekong is about 4,909 km long.").Name);
        Assert.Null(Recover("""{"name": "delete_everything", "parameters": {}}""").Name);
        Assert.Null(TextToolCalls.Recover("""{"name": "get_summary", "parameters": {}}""", []));
    }
}
