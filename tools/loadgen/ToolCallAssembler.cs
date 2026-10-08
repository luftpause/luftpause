using System.Text;
using System.Text.Json.Nodes;

namespace Luftpause.LoadGen;

public sealed class ToolCallAssembler
{
    private readonly SortedDictionary<int, (string? Id, string? Name, StringBuilder Arguments)> calls = new();

    public void Add(JsonNode? delta)
    {
        var index = delta?["index"]?.GetValue<int>() ?? calls.Count;
        var (id, name, arguments) = calls.GetValueOrDefault(index, (null, null, new StringBuilder()));
        arguments.Append(delta?["function"]?["arguments"]?.GetValue<string>());
        calls[index] = (
            id ?? delta?["id"]?.GetValue<string>(),
            name ?? delta?["function"]?["name"]?.GetValue<string>(),
            arguments);
    }

    public JsonArray Build() =>
    [
        .. calls.Values.Select(c => (JsonNode)new JsonObject
        {
            ["id"] = c.Id,
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments.ToString() },
        }),
    ];
}
