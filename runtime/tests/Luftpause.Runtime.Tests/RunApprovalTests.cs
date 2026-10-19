using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Luftpause.Runtime.Tests;

public sealed class RunApprovalTests : IDisposable
{
    private static readonly RunSpec Spec = new("task", "model", "interactive", null, 4);
    private static readonly ApprovalRequest Drop = new("drop", JsonNode.Parse("""{"table":"users"}"""));

    private readonly string path = Path.Combine(Path.GetTempPath(), $"runs-{Guid.NewGuid():N}.jsonl");
    private RunJournal? journal;

    public void Dispose()
    {
        journal?.Dispose();
        File.Delete(path);
    }

    private sealed class Ok : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private sealed class ApprovingLoop : IRunLoop
    {
        public async Task<RunOutcome> RunAsync(RunStart start, Action<RunProgress> report, CancellationToken cancel)
        {
            var allowed = await start.Approve(Drop, cancel);
            return new RunOutcome(RunStatus.Completed, allowed ? "dropped" : "kept", null);
        }
    }

    private sealed class Asker(bool allow) : IApprovalAsker
    {
        public List<(string Handle, ApprovalRequest Request)> Asked { get; } = [];

        public Task<bool> AskAsync(string handle, PendingApproval approval, CancellationToken cancel)
        {
            Asked.Add((handle, approval.Request));
            return Task.FromResult(allow);
        }
    }

    private sealed class Silent : IProgress<RunView>
    {
        public void Report(RunView value) { }
    }

    private RunRegistry Registry(params JournalRecord[] history)
    {
        journal = new RunJournal(path);
        return new RunRegistry(new ApprovingLoop(), journal, history,
            new LlmClient(new HttpClient(new Ok()) { BaseAddress = new Uri("http://dataplane/") }), TimeProvider.System, NullLogger<RunRegistry>.Instance);
    }

    private static async Task<RunView> PausedAsync(RunRegistry registry, RunRecord record)
    {
        await Task.Run(() => SpinWait.SpinUntil(() => record.Pending is not null, TimeSpan.FromSeconds(5)));
        return registry.View(record, DateTimeOffset.UtcNow);
    }

    private static Task<RunOutcome> Finished(RunRecord record) => record.Completion.WaitAsync(TimeSpan.FromSeconds(5));

    [Fact]
    public async Task A_run_asking_for_approval_waits_in_awaiting_approval_with_the_tool_and_its_arguments()
    {
        var registry = Registry();
        var record = registry.Spawn(Spec);

        var view = await PausedAsync(registry, record);

        Assert.Equal(RunStatus.AwaitingApproval, view.Status);
        Assert.Equal("drop", view.PendingApproval!.Tool);
        Assert.Equal("""{"table":"users"}""", view.PendingApproval.Arguments!.ToJsonString());
        Assert.False(record.Completion.IsCompleted);
    }

    [Theory]
    [InlineData(true, "dropped")]
    [InlineData(false, "kept")]
    public async Task Deciding_resumes_the_run_with_the_answer(bool allow, string answer)
    {
        var registry = Registry();
        var record = registry.Spawn(Spec);
        await PausedAsync(registry, record);

        Assert.True(registry.Decide(record.Handle, allow));

        Assert.Equal(answer, (await Finished(record)).Answer);
        Assert.Null(registry.View(record, DateTimeOffset.UtcNow).PendingApproval);
    }

    [Fact]
    public async Task Deciding_a_run_with_nothing_pending_returns_false()
    {
        var registry = Registry();
        var record = registry.Spawn(Spec);
        await PausedAsync(registry, record);
        registry.Decide(record.Handle, true);
        await Finished(record);

        Assert.False(registry.Decide(record.Handle, true));
        Assert.False(registry.Decide("run-missing", true));
    }

    [Fact]
    public async Task The_decision_is_journaled()
    {
        var registry = Registry();
        var record = registry.Spawn(Spec);
        await PausedAsync(registry, record);
        registry.Decide(record.Handle, false);
        await Finished(record);
        journal!.Dispose();

        var decided = Assert.Single(RunJournal.Replay(path).OfType<RunApprovalDecided>());
        Assert.Equal((record.Handle, "drop", false), (decided.Handle, decided.Tool, decided.Allow));
        Assert.Equal("""{"table":"users"}""", decided.Arguments!.ToJsonString());
    }

    [Fact]
    public async Task Await_that_cannot_ask_returns_as_soon_as_a_run_awaits_approval()
    {
        var registry = Registry();
        var record = registry.Spawn(Spec);

        var views = await registry.AwaitAsync([record.Handle], true, TimeSpan.FromMinutes(5), new Silent(), null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RunStatus.AwaitingApproval, Assert.Single(views).Status);
    }

    [Theory]
    [InlineData(true, "dropped")]
    [InlineData(false, "kept")]
    public async Task Await_that_can_ask_asks_once_and_waits_for_the_run_to_finish(bool allow, string answer)
    {
        var registry = Registry();
        var record = registry.Spawn(Spec);
        var asker = new Asker(allow);

        var views = await registry.AwaitAsync([record.Handle], true, TimeSpan.FromMinutes(5), new Silent(), asker, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal((RunStatus.Completed, answer), (Assert.Single(views).Status, views[0].Answer));
        Assert.Equal((record.Handle, Drop), Assert.Single(asker.Asked));
    }

    [Fact]
    public async Task A_decision_made_before_a_restart_is_not_asked_again()
    {
        var registry = Registry(
            new RunSpawned("run-1", Spec, DateTimeOffset.UnixEpoch, null),
            new RunApprovalDecided("run-1", "drop", JsonNode.Parse("""{"table":"users"}"""), true));

        registry.ResumeAll();

        Assert.Equal("dropped", (await Finished(registry.Find("run-1")!)).Answer);
    }

    [Fact]
    public async Task An_unanswered_approval_is_asked_again_after_a_restart()
    {
        var registry = Registry(new RunSpawned("run-1", Spec, DateTimeOffset.UnixEpoch, null));

        registry.ResumeAll();

        var record = registry.Find("run-1")!;
        Assert.Equal(RunStatus.AwaitingApproval, (await PausedAsync(registry, record)).Status);
    }

    [Fact]
    public async Task A_decision_already_followed_by_a_tool_round_is_not_reused()
    {
        var registry = Registry(
            new RunSpawned("run-1", Spec, DateTimeOffset.UnixEpoch, null),
            new RunApprovalDecided("run-1", "drop", JsonNode.Parse("""{"table":"users"}"""), true),
            new RunTurned("run-1", [], RunWork.None with { Turns = 1, ToolCalls = 1 }));

        registry.ResumeAll();

        var record = registry.Find("run-1")!;
        Assert.Equal(RunStatus.AwaitingApproval, (await PausedAsync(registry, record)).Status);
    }

    [Fact]
    public async Task A_decision_for_other_arguments_is_not_reused()
    {
        var registry = Registry(
            new RunSpawned("run-1", Spec, DateTimeOffset.UnixEpoch, null),
            new RunApprovalDecided("run-1", "drop", JsonNode.Parse("""{"table":"orders"}"""), true));

        registry.ResumeAll();

        var record = registry.Find("run-1")!;
        Assert.Equal(RunStatus.AwaitingApproval, (await PausedAsync(registry, record)).Status);
    }
}
