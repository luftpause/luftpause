using System.Text;
using System.Text.Json.Nodes;

namespace Luftpause.LoadGen;

public static class Conversation
{
    public const int ToolRounds = 3;
    private const string Filler = "The quick brown fox jumps over the lazy dog near the quiet river bank. ";

    public static JsonArray Start(int index, AgentShape shape) =>
    [
        new JsonObject
        {
            ["role"] = "system",
            ["content"] =
                $"You are a research agent. Call the lookup tool exactly {ToolRounds} times, one call per reply, " +
                $"with a different query each time. After you have received {ToolRounds} tool results, " +
                "answer in one short sentence without calling any tool.",
        },
        new JsonObject
        {
            ["role"] = "user",
            ["content"] = $"Task {index}: find three facts about rivers.\n\nBackground notes:\n{Pad(shape.ContextChars)}",
        },
    ];

    public static JsonObject ToolResult(string? toolCallId, AgentShape shape) => new()
    {
        ["role"] = "tool",
        ["tool_call_id"] = toolCallId,
        ["content"] = $"Result: rivers carry sediment. {Pad(shape.ToolResultChars)}",
    };

    public static JsonArray Tools() =>
    [
        new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "lookup",
                ["description"] = "Look up one fact for a query.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = "string" } },
                    ["required"] = new JsonArray("query"),
                },
            },
        },
    ];

    private static string Pad(int chars)
    {
        var text = new StringBuilder(chars + Filler.Length);
        while (text.Length < chars)
            text.Append(Filler);
        return text.ToString(0, Math.Min(chars, text.Length));
    }
}
