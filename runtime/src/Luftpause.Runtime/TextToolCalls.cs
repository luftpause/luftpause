using System.Text.Json;
using System.Text.Json.Nodes;

namespace Luftpause.Runtime;

public static class TextToolCalls
{
    private const string PythonTag = "<|python_tag|>";

    public static JsonObject? Recover(string? content, JsonArray offered)
    {
        if (string.IsNullOrWhiteSpace(content) || offered.Count == 0)
            return null;

        var text = Unwrap(content.Trim());
        if (!text.StartsWith('{'))
            return null;

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }

        var call = parsed?["function"] is JsonObject function ? function : parsed as JsonObject;
        var name = call?["name"]?.GetValue<string>();
        if (name is null || !Offers(offered, name))
            return null;

        var arguments = call!["parameters"] ?? call["arguments"];
        var serialized = arguments switch
        {
            JsonValue value when value.TryGetValue<string>(out var raw) => raw,
            null => "{}",
            _ => arguments.ToJsonString(),
        };
        return new JsonObject
        {
            ["id"] = $"call_text_{Guid.NewGuid():N}",
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = name, ["arguments"] = serialized },
        };
    }

    private static string Unwrap(string text)
    {
        var tagged = text.StartsWith(PythonTag, StringComparison.Ordinal) ? text[PythonTag.Length..].Trim() : text;
        if (!tagged.StartsWith("```", StringComparison.Ordinal))
            return tagged;
        var firstLineEnd = tagged.IndexOf('\n');
        var closing = tagged.LastIndexOf("```", StringComparison.Ordinal);
        return firstLineEnd > 0 && closing > firstLineEnd ? tagged[(firstLineEnd + 1)..closing].Trim() : tagged;
    }

    private static bool Offers(JsonArray offered, string name) =>
        offered.Any(d => d?["function"]?["name"]?.GetValue<string>() == name);
}
