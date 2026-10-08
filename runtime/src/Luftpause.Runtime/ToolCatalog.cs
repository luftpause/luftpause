using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Luftpause.Runtime;

public sealed class ToolServer
{
    public required string Name { get; init; }
    public string? Url { get; init; }
    public string? Command { get; init; }
    public string[] Args { get; init; } = [];
}

public sealed record ToolInfo(string Name, string? Description, string Server);

public sealed class ToolCatalog(IReadOnlyList<ToolServer> servers, ILoggerFactory logs) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IReadOnlyDictionary<string, (McpClient Client, McpClientTool Tool, string Server)>? tools;
    private readonly List<McpClient> clients = [];

    public async Task<JsonArray> DefinitionsAsync(CancellationToken cancel)
    {
        var all = await ToolsAsync(cancel);
        return [.. all.Values.Select(t => (JsonNode)Definition(t.Tool.ProtocolTool))];
    }

    public async Task<IReadOnlyList<ToolInfo>> DescribeAsync(CancellationToken cancel)
    {
        var all = await ToolsAsync(cancel);
        return [.. all.Values.Select(t => new ToolInfo(t.Tool.Name, t.Tool.Description, t.Server))];
    }

    public async Task<string> CallAsync(JsonObject toolCall, CancellationToken cancel)
    {
        var name = toolCall["function"]?["name"]?.GetValue<string>() ?? "";
        var all = await ToolsAsync(cancel);
        if (!all.TryGetValue(name, out var target))
            return $"Error: unknown tool '{name}'. Available tools: {string.Join(", ", all.Keys)}.";

        var schema = target.Tool.ProtocolTool.InputSchema;
        if (!TryParse(toolCall["function"]?["arguments"]?.GetValue<string>(), out var arguments))
            return $"Error: arguments for '{name}' were not valid JSON. Expected {ToolArguments.Expected(schema)}.";
        arguments = ToolArguments.Coerce(schema, arguments);
        var problems = ToolArguments.Problems(schema, arguments);
        if (problems.Count > 0)
            return $"Error: invalid arguments for '{name}': {string.Join("; ", problems)}. Expected {ToolArguments.Expected(schema)}.";

        var values = arguments.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        var result = await target.Client.CallToolAsync(name, values, cancellationToken: cancel);
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        return result.IsError == true ? $"Error: {text}" : text;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in clients)
            await client.DisposeAsync();
    }

    private async Task<IReadOnlyDictionary<string, (McpClient Client, McpClientTool Tool, string Server)>> ToolsAsync(CancellationToken cancel)
    {
        if (tools is not null)
            return tools;

        await gate.WaitAsync(cancel);
        try
        {
            if (tools is not null)
                return tools;

            var found = new Dictionary<string, (McpClient, McpClientTool, string)>();
            foreach (var server in servers)
            {
                var client = await McpClient.CreateAsync(Transport(server), loggerFactory: logs, cancellationToken: cancel);
                clients.Add(client);
                foreach (var tool in await client.ListToolsAsync(cancellationToken: cancel))
                    found.TryAdd(tool.Name, (client, tool, server.Name));
            }
            return tools = found;
        }
        finally
        {
            gate.Release();
        }
    }

    private IClientTransport Transport(ToolServer server) => server.Command is { Length: > 0 } command
        ? new StdioClientTransport(new StdioClientTransportOptions { Command = command, Arguments = server.Args, Name = server.Name }, logs)
        : new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(server.Url ?? throw new InvalidOperationException($"Tool server '{server.Name}' needs a Url or a Command.")),
                Name = server.Name,
            },
            logs);

    private static JsonObject Definition(Tool tool) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = tool.Name,
            ["description"] = tool.Description,
            ["parameters"] = JsonNode.Parse(tool.InputSchema.GetRawText()),
        },
    };

    private static bool TryParse(string? raw, out JsonElement arguments)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            arguments = JsonDocument.Parse("{}").RootElement;
            return true;
        }
        try
        {
            arguments = JsonDocument.Parse(raw).RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            arguments = default;
            return false;
        }
    }
}
