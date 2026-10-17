using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace Luftpause.Runtime.Tests;

public sealed class McpEndToEndTests : IAsyncLifetime
{
    private readonly GatedLoop loop = new();
    private readonly string path = Path.Combine(Path.GetTempPath(), $"runs-{Guid.NewGuid():N}.jsonl");
    private WebApplication? app;
    private McpClient? client;

    private sealed class Ok : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private sealed class GatedLoop : IRunLoop
    {
        public SemaphoreSlim Step { get; } = new(0);

        public TaskCompletionSource<RunStart> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<RunOutcome> RunAsync(RunStart start, Action<RunProgress> report, CancellationToken cancel)
        {
            Started.TrySetResult(start);
            await Step.WaitAsync(cancel);
            report(new RunProgress(RunWork.None with { Turns = 1 }, []));
            await Step.WaitAsync(cancel);
            return new RunOutcome(RunStatus.Completed, "answer", null);
        }
    }

    private sealed class Notes : IProgress<ProgressNotificationValue>
    {
        private readonly System.Threading.Channels.Channel<ProgressNotificationValue> seen =
            System.Threading.Channels.Channel.CreateUnbounded<ProgressNotificationValue>();

        public void Report(ProgressNotificationValue value) => seen.Writer.TryWrite(value);

        public Task<ProgressNotificationValue> NextAsync() => seen.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(new RuntimeDefaults { Model = "fake" });
        builder.Services.AddSingleton(new LlmClient(new HttpClient(new Ok()) { BaseAddress = new Uri("http://dataplane/") }));
        builder.Services.AddSingleton(new ToolCatalog([], NullLoggerFactory.Instance));
        builder.Services.AddSingleton<IRunLoop>(loop);
        builder.Services.AddSingleton(_ => new RunJournal(path));
        builder.Services.AddSingleton<IReadOnlyList<JournalRecord>>([]);
        builder.Services.AddSingleton<RunRegistry>();
        builder.Services.AddLuftpauseMcp();
        app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
    }

    private async Task ConnectAsync(string protocolVersion) =>
        client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri($"{app!.Urls.Single()}/mcp") }),
            new McpClientOptions { ProtocolVersion = protocolVersion });

    public async Task DisposeAsync()
    {
        if (client is not null)
            await client.DisposeAsync();
        await app!.StopAsync();
        await app.DisposeAsync();
        File.Delete(path);
    }

    private async Task<string> SpawnAsync()
    {
        var result = await client!.CallToolAsync("spawn", new Dictionary<string, object?> { ["task"] = "t" });
        return JsonDocument.Parse(Text(result)).RootElement.GetProperty("handle").GetString()!;
    }

    private static string Text(ModelContextProtocol.Protocol.CallToolResult result) =>
        Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(result.Content)).Text;

    [Fact]
    public async Task A_spawned_run_is_tagged_with_the_session_that_spawned_it()
    {
        await ConnectAsync("2025-11-25");
        await SpawnAsync();

        var start = await loop.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(client!.SessionId);
        Assert.Equal(client.SessionId, start.Spec.Caller);
    }

    [Fact]
    public async Task A_client_on_the_sessionless_revision_spawns_without_a_caller()
    {
        await ConnectAsync("2026-07-28");
        await SpawnAsync();

        var start = await loop.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(start.Spec.Caller);
    }

    [Theory]
    [InlineData("2025-11-25")]
    [InlineData("2026-07-28")]
    public async Task Await_sends_progress_while_waiting_and_returns_the_final_state(string protocolVersion)
    {
        await ConnectAsync(protocolVersion);
        var handle = await SpawnAsync();
        var notes = new Notes();

        var waiting = client!.CallToolAsync("await", new Dictionary<string, object?> { ["handles"] = new[] { handle } }, notes);
        var first = await notes.NextAsync();
        loop.Step.Release();
        var turned = await notes.NextAsync();
        loop.Step.Release();
        var result = await waiting;

        Assert.Equal(handle, JsonDocument.Parse(first.Message!).RootElement.GetProperty("handle").GetString());
        Assert.Equal(1, JsonDocument.Parse(turned.Message!).RootElement.GetProperty("turns").GetInt32());
        Assert.Contains("answer", Text(result));
    }
}
