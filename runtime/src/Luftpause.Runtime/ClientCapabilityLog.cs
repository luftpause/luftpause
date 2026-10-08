using System.Text.Json.Nodes;

namespace Luftpause.Runtime;

public static class ClientCapabilityLog
{
    private const string MetaCapabilities = "io.modelcontextprotocol/clientCapabilities";

    public static IApplicationBuilder UseClientCapabilityLog(this IApplicationBuilder app, ILogger log) =>
        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsPost(context.Request.Method))
            {
                context.Request.EnableBuffering();
                var body = await new StreamReader(context.Request.Body, leaveOpen: true).ReadToEndAsync(context.RequestAborted);
                context.Request.Body.Position = 0;
                Describe(body, log);
            }
            await next();
        });

    private static void Describe(string body, ILogger log)
    {
        JsonNode? message;
        try
        {
            message = JsonNode.Parse(body);
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }

        var method = message?["method"]?.GetValue<string>();
        if (method == "initialize")
            log.LogInformation(
                "mcp_client {Client} protocol={Protocol} capabilities={Capabilities}",
                message?["params"]?["clientInfo"]?.ToJsonString(),
                message?["params"]?["protocolVersion"]?.GetValue<string>(),
                message?["params"]?["capabilities"]?.ToJsonString());
        else if (message?["params"]?["_meta"]?[MetaCapabilities] is { } perRequest)
            log.LogInformation("mcp_request {Method} client_capabilities={Capabilities}", method, perRequest.ToJsonString());
    }
}
