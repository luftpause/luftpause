using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Luftpause.Runtime.Tests;

public sealed class RunRegistryTests : IDisposable
{
    private static readonly RunSpec Spec = new("task", "model", "interactive", null, 4);

    private readonly string path = Path.Combine(Path.GetTempPath(), $"runs-{Guid.NewGuid():N}.jsonl");
    private readonly FakeLoop loop = new();
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

    private sealed class FakeLoop : IRunLoop
    {
        public List<RunStart> Starts { get; } = [];

        public Func<RunStart, Action<RunProgress>, CancellationToken, Task<RunOutcome>> Run { get; set; } =
            (_, _, cancel) => Task.Delay(Timeout.Infinite, cancel).ContinueWith(_ => new RunOutcome(RunStatus.Cancelled, null, null));

        public Task<RunOutcome> RunAsync(RunStart start, Action<RunProgress> report, CancellationToken cancel)
        {
            lock (Starts)
                Starts.Add(start);
            return Run(start, report, cancel);
        }
    }

    private RunRegistry Registry(params JournalRecord[] history)
    {
        journal = new RunJournal(path);
        return new RunRegistry(loop, journal, history, new LlmClient(new HttpClient(new Ok()) { BaseAddress = new Uri("http://dataplane/") }),
            TimeProvider.System, NullLogger<RunRegistry>.Instance);
    }

    private static JsonObject Message(string role) => new() { ["role"] = role, ["content"] = role };

    private static JsonObject ToolCalls() => new()
    {
        ["role"] = "assistant",
        ["content"] = null,
        ["tool_calls"] = new JsonArray(new JsonObject { ["id"] = "c1" }),
    };

    private static JsonObject ToolResult() => new() { ["role"] = "tool", ["tool_call_id"] = "c1", ["content"] = "r" };

    [Fact]
    public async Task Spawn_journals_spawned_turned_and_finished()
    {
        loop.Run = (_, report, _) =>
        {
            report(new RunProgress(RunWork.None with { Turns = 1 }, []));
            report(new RunProgress(RunWork.None with { Turns = 1, ToolCalls = 1 }, [ToolCalls(), ToolResult()]));
            return Task.FromResult(new RunOutcome(RunStatus.Completed, "answer", null));
        };
        var registry = Registry();

        await registry.Spawn(Spec).Completion;
        journal!.Dispose();

        Assert.Equal([typeof(RunSpawned), typeof(RunTurned), typeof(RunFinished)], RunJournal.Replay(path).Select(r => r.GetType()));
    }

    [Fact]
    public void Resume_restarts_an_unfinished_run_from_its_journaled_messages()
    {
        var registry = Registry(
            new RunSpawned("run-1", Spec, DateTimeOffset.UnixEpoch, null),
            new RunTurned("run-1", [ToolCalls(), ToolResult()], RunWork.None with { Turns = 1 }));

        registry.ResumeAll();

        var start = SingleStart();
        Assert.Equal("run-1", start.RunId);
        Assert.Equal(AgentLoop.InitialMessages(Spec).Count + 2, start.Messages.Count);
        Assert.Equal(1, start.Work.Turns);
        Assert.Equal(RunStatus.Running, registry.Find("run-1")!.View(DateTimeOffset.UtcNow, []).Status);
    }

    [Fact]
    public void Resume_rebuilds_a_finished_run_without_running_it()
    {
        var registry = Registry(
            new RunSpawned("run-1", Spec, DateTimeOffset.UnixEpoch, null),
            new RunFinished("run-1", new RunOutcome(RunStatus.Completed, "answer", null), DateTimeOffset.UnixEpoch.AddSeconds(2)));

        registry.ResumeAll();

        var view = registry.Find("run-1")!.View(DateTimeOffset.UtcNow, []);
        Assert.Equal(("answer", 2.0), (view.Answer, view.ElapsedSeconds));
        Assert.Empty(loop.Starts);
    }

    [Fact]
    public void Resume_after_a_turn_with_no_journaled_tool_results_ends_at_the_last_complete_round()
    {
        var registry = Registry(
            new RunSpawned("run-1", Spec, DateTimeOffset.UnixEpoch, null),
            new RunTurned("run-1", [ToolCalls(), ToolResult()], RunWork.None with { Turns = 2, ToolCalls = 1 }));

        registry.ResumeAll();

        Assert.Equal("tool", SingleStart().Messages[^1]["role"]!.GetValue<string>());
    }

    [Fact]
    public void Spawn_with_an_unknown_parent_throws()
    {
        var registry = Registry();

        var error = Assert.Throws<InvalidOperationException>(() => registry.Spawn(Spec, "run-missing"));

        Assert.Equal("unknown parent handle 'run-missing'", error.Message);
        Assert.Empty(loop.Starts);
    }

    [Fact]
    public void Children_are_listed_in_spawn_order()
    {
        var registry = Registry();
        var parent = registry.Spawn(Spec);

        var first = registry.Spawn(Spec, parent.Handle);
        var second = registry.Spawn(Spec, parent.Handle);

        Assert.Equal([first.Handle, second.Handle], registry.Children(parent.Handle));
        Assert.Empty(registry.Children(first.Handle));
    }

    [Fact]
    public void Resumed_runs_keep_their_children()
    {
        var registry = Registry(
            new RunSpawned("run-1", Spec, DateTimeOffset.UnixEpoch, null),
            new RunSpawned("run-2", Spec, DateTimeOffset.UnixEpoch, "run-1"),
            new RunSpawned("run-3", Spec, DateTimeOffset.UnixEpoch, "run-1"));

        registry.ResumeAll();

        Assert.Equal(["run-2", "run-3"], registry.Children("run-1"));
        Assert.Equal("run-1", registry.Find("run-2")!.View(DateTimeOffset.UtcNow, []).Parent);
    }

    [Fact]
    public async Task Cancelling_a_parent_cancels_running_children_and_leaves_finished_ones()
    {
        var finishes = Spec with { Task = "finishes" };
        var defaultRun = loop.Run;
        loop.Run = (start, report, cancel) => start.Spec.Task == "finishes"
            ? Task.FromResult(new RunOutcome(RunStatus.Completed, "done", null))
            : defaultRun(start, report, cancel);
        var registry = Registry();
        var parent = registry.Spawn(Spec);
        var finished = registry.Spawn(finishes, parent.Handle);
        var running = registry.Spawn(Spec, parent.Handle);
        await finished.Completion;

        Assert.True(registry.Cancel(parent.Handle));

        Assert.True(running.Cancel.IsCancellationRequested);
        Assert.True(parent.Cancel.IsCancellationRequested);
        Assert.False(finished.Cancel.IsCancellationRequested);
        Assert.Equal("done", (await finished.Completion).Answer);
    }

    [Fact]
    public async Task Cancelling_a_finished_parent_with_no_running_children_returns_false()
    {
        loop.Run = (_, _, _) => Task.FromResult(new RunOutcome(RunStatus.Completed, "done", null));
        var registry = Registry();
        var parent = registry.Spawn(Spec);
        var child = registry.Spawn(Spec, parent.Handle);
        await Task.WhenAll(parent.Completion, child.Completion);

        Assert.False(registry.Cancel(parent.Handle));
    }

    private RunStart SingleStart()
    {
        SpinWait.SpinUntil(() => { lock (loop.Starts) return loop.Starts.Count > 0; }, TimeSpan.FromSeconds(5));
        lock (loop.Starts)
            return Assert.Single(loop.Starts);
    }
}
