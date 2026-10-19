using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;

namespace Luftpause.Runtime.Tests;

[McpServerToolType]
public sealed class TableTools
{
    public static int Dropped;

    [McpServerTool(Name = "drop", Destructive = true), Description("Drop a table.")]
    public static string Drop(string table)
    {
        Interlocked.Increment(ref Dropped);
        return $"dropped {table}";
    }

    [McpServerTool(Name = "count", ReadOnly = true), Description("Count rows.")]
    public static string Count(string table) => $"{table}: 3 rows";
}

public sealed class AgentLoopApprovalTests : IAsyncLifetime
{
    private WebApplication? tools;
    private ToolCatalog? catalog;

    private sealed class ScriptedChat(params string[] toolCalls) : HttpMessageHandler
    {
        private int turn;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var index = Interlocked.Increment(ref turn) - 1;
            var message = index < toolCalls.Length
                ? new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = null,
                    ["tool_calls"] = new JsonArray(new JsonObject
                    {
                        ["id"] = $"c{index}",
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = toolCalls[index], ["arguments"] = """{"table":"users"}""" },
                    }),
                }
                : new JsonObject { ["role"] = "assistant", ["content"] = "done" };
            var body = new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject { ["message"] = message, ["finish_reason"] = "stop" }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 1, ["completion_tokens"] = 1 },
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            });
        }
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<TableTools>();
        tools = builder.Build();
        tools.MapMcp("/mcp");
        await tools.StartAsync();
        catalog = new ToolCatalog([new ToolServer { Name = "tables", Url = $"{tools.Urls.Single()}/mcp" }], NullLoggerFactory.Instance);
    }

    public async Task DisposeAsync()
    {
        await catalog!.DisposeAsync();
        await tools!.StopAsync();
        await tools.DisposeAsync();
    }

    private async Task<(RunOutcome Outcome, List<ApprovalRequest> Asked, List<RunProgress> Progress)> RunAsync(
        bool allow, RunSpec spec, params string[] toolCalls)
    {
        var llm = new LlmClient(new HttpClient(new ScriptedChat(toolCalls)) { BaseAddress = new Uri("http://dataplane/") });
        var loop = new AgentLoop(llm, catalog!, TimeProvider.System, NullLogger<AgentLoop>.Instance);
        var asked = new List<ApprovalRequest>();
        var progress = new List<RunProgress>();
        var start = new RunStart("run-1", spec, AgentLoop.InitialMessages(spec), RunWork.None, (request, _) =>
        {
            asked.Add(request);
            return Task.FromResult(allow);
        });
        var outcome = await loop.RunAsync(start, progress.Add, CancellationToken.None);
        return (outcome, asked, progress);
    }

    private static readonly RunSpec Spec = new("task", "model", "interactive", null, 4);

    private static string LastToolResult(List<RunProgress> progress) =>
        progress.SelectMany(p => p.NewMessages).Last(m => m["role"]!.GetValue<string>() == "tool")["content"]!.GetValue<string>();

    [Fact]
    public async Task An_allowed_destructive_call_is_asked_about_then_runs()
    {
        var before = TableTools.Dropped;

        var (outcome, asked, progress) = await RunAsync(true, Spec, "drop");

        var request = Assert.Single(asked);
        Assert.Equal(("drop", """{"table":"users"}"""), (request.Tool, request.Arguments!.ToJsonString()));
        Assert.Equal("dropped users", LastToolResult(progress));
        Assert.Equal(before + 1, TableTools.Dropped);
        Assert.Equal("done", outcome.Answer);
    }

    [Fact]
    public async Task A_declined_call_does_not_run_and_the_model_is_told_so()
    {
        var before = TableTools.Dropped;

        var (outcome, _, progress) = await RunAsync(false, Spec, "drop");

        Assert.Equal("Error: the user declined this tool call", LastToolResult(progress));
        Assert.Equal(before, TableTools.Dropped);
        Assert.Equal(1, progress[^1].Work.FailedToolCalls);
        Assert.Equal("done", outcome.Answer);
    }

    [Fact]
    public async Task A_read_only_call_runs_without_asking()
    {
        var (_, asked, progress) = await RunAsync(false, Spec, "count");

        Assert.Empty(asked);
        Assert.Equal("users: 3 rows", LastToolResult(progress));
    }

    [Fact]
    public async Task A_tool_named_in_approve_tools_is_asked_about()
    {
        var spec = Spec with { ApproveTools = new HashSet<string>(StringComparer.Ordinal) { "count" } };

        var (_, asked, _) = await RunAsync(true, spec, "count");

        Assert.Equal("count", Assert.Single(asked).Tool);
    }
}
