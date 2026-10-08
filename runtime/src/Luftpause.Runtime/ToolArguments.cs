using System.Text.Json;

namespace Luftpause.Runtime;

public static class ToolArguments
{
    public static JsonElement Coerce(JsonElement schema, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
            return arguments;

        var coerced = new System.Text.Json.Nodes.JsonObject();
        foreach (var property in arguments.EnumerateObject())
        {
            var target = properties.TryGetProperty(property.Name, out var s) ? TypeOf(s) : "any";
            coerced[property.Name] = CoerceScalar(target, property.Value)
                ?? System.Text.Json.Nodes.JsonNode.Parse(property.Value.GetRawText());
        }
        return JsonDocument.Parse(coerced.ToJsonString()).RootElement.Clone();
    }

    private static System.Text.Json.Nodes.JsonNode? CoerceScalar(string target, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            return null;
        var text = value.GetString()!.Trim();
        return target switch
        {
            "integer" when long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var i) => i,
            "number" when double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
            "boolean" when bool.TryParse(text, out var b) => b,
            _ => null,
        };
    }

    public static IReadOnlyList<string> Problems(JsonElement schema, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            return [$"arguments must be a JSON object, got {arguments.ValueKind}"];

        var properties = schema.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object
            ? p.EnumerateObject().ToDictionary(x => x.Name, x => x.Value)
            : [];
        var given = arguments.EnumerateObject().ToDictionary(x => x.Name, x => x.Value);

        var missing = Required(schema).Where(name => !given.ContainsKey(name)).Select(name => $"missing required '{name}'");
        var unknown = properties.Count == 0 || AllowsAdditional(schema)
            ? []
            : given.Keys.Where(name => !properties.ContainsKey(name)).Select(name => $"unexpected '{name}'");
        var mistyped = given
            .Where(kv => properties.TryGetValue(kv.Key, out var s) && !Matches(s, kv.Value))
            .Select(kv => $"'{kv.Key}' should be {TypeOf(properties[kv.Key])}, got {kv.Value.ValueKind}");

        return [.. missing, .. unknown, .. mistyped];
    }

    public static string Expected(JsonElement schema)
    {
        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            return "{}";
        var required = Required(schema).ToHashSet();
        var fields = properties.EnumerateObject()
            .Select(p => $"\"{p.Name}\": <{TypeOf(p.Value)}>{(required.Contains(p.Name) ? "" : " (optional)")}");
        return "{" + string.Join(", ", fields) + "}";
    }

    private static IEnumerable<string> Required(JsonElement schema) =>
        schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array
            ? required.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.String).Select(r => r.GetString()!)
            : [];

    private static bool AllowsAdditional(JsonElement schema) =>
        !schema.TryGetProperty("additionalProperties", out var additional) || additional.ValueKind != JsonValueKind.False;

    private static string TypeOf(JsonElement propertySchema) =>
        propertySchema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString()! : "any";

    private static bool Matches(JsonElement propertySchema, JsonElement value) => TypeOf(propertySchema) switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "array" => value.ValueKind == JsonValueKind.Array,
        "object" => value.ValueKind == JsonValueKind.Object,
        _ => true,
    };
}
