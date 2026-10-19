using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

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

    private RunRegistry Registry(params JournalRecord[] history) => Registry(TimeProvider.System, history);

    private RunRegistry Registry(TimeProvider clock, params JournalRecord[] history)
    {
        journal = new RunJournal(path);
        return new RunRegistry(loop, journal, history, new LlmClient(new HttpClient(new Ok()) { BaseAddress = new Uri("http://dataplane/") }),
            clock, NullLogger<RunRegistry>.Instance);
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
    public void A_child_run_span_is_a_child_of_its_parent_run_span()
    {
        var started = new ConcurrentDictionary<string, Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == Tracing.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = a =>
            {
                if (a.GetTagItem("gen_ai.agent.id") is string handle)
                    started[handle] = a;
            },
        };
        ActivitySource.AddActivityListener(listener);
        var registry = Registry();

        var parent = registry.Spawn(Spec);
        var child = registry.Spawn(Spec, parent.Handle);

        Assert.Equal(started[parent.Handle].TraceId, started[child.Handle].TraceId);
        Assert.Equal(started[parent.Handle].SpanId, started[child.Handle].ParentSpanId);
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

    private sealed class Watcher : IProgress<RunView>
    {
        private readonly System.Threading.Channels.Channel<RunView> seen = System.Threading.Channels.Channel.CreateUnbounded<RunView>();

        public void Report(RunView value) => seen.Writer.TryWrite(value);

        public async Task<(RunStatus Status, int Turns, int ToolCalls)> NextAsync()
        {
            var view = await seen.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            return (view.Status, view.Turns, view.ToolCalls);
        }

        public bool Idle => !seen.Reader.TryPeek(out _);
    }

    [Fact]
    public async Task Await_reports_where_each_run_stands_then_every_change_and_returns_the_final_state()
    {
        var step = new SemaphoreSlim(0);
        loop.Run = async (_, report, _) =>
        {
            await step.WaitAsync();
            report(new RunProgress(RunWork.None with { Turns = 1 }, []));
            await step.WaitAsync();
            report(new RunProgress(RunWork.None with { Turns = 1, ToolCalls = 1 }, [ToolCalls(), ToolResult()]));
            await step.WaitAsync();
            return new RunOutcome(RunStatus.Completed, "answer", null);
        };
        var registry = Registry();
        var record = registry.Spawn(Spec);
        var watcher = new Watcher();

        var waiting = registry.AwaitAsync([record.Handle], true, TimeSpan.FromSeconds(30), watcher, null, CancellationToken.None);
        Assert.Equal((RunStatus.Running, 0, 0), await watcher.NextAsync());
        step.Release();
        Assert.Equal((RunStatus.Running, 1, 0), await watcher.NextAsync());
        step.Release();
        Assert.Equal((RunStatus.Running, 1, 1), await watcher.NextAsync());
        step.Release();
        Assert.Equal((RunStatus.Completed, 1, 1), await watcher.NextAsync());
        var views = await waiting;

        Assert.Equal(("answer", 1, 1), (views[0].Answer, views[0].Turns, views[0].ToolCalls));
        Assert.True(watcher.Idle);
    }

    [Fact]
    public async Task Await_on_finished_runs_reports_nothing()
    {
        var registry = Registry(
            new RunSpawned("run-1", Spec, DateTimeOffset.UnixEpoch, null),
            new RunTurned("run-1", [ToolCalls(), ToolResult()], RunWork.None with { Turns = 1, ToolCalls = 1 }),
            new RunFinished("run-1", new RunOutcome(RunStatus.Completed, "answer", null), DateTimeOffset.UnixEpoch));
        registry.ResumeAll();
        var watcher = new Watcher();

        var views = await registry.AwaitAsync(["run-1"], true, TimeSpan.FromSeconds(30), watcher, null, CancellationToken.None);

        Assert.Equal("answer", views[0].Answer);
        Assert.True(watcher.Idle);
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

    private void SpendTokensThenWait(long promptTokens) =>
        loop.Run = async (start, report, cancel) =>
        {
            if (start.Spec.Task == "spends")
                report(new RunProgress(RunWork.None with { PromptTokens = promptTokens, CompletionTokens = 10 }, []));
            await Task.Delay(Timeout.Infinite, cancel);
            throw new UnreachableException();
        };

    private static Task<RunOutcome> Finished(RunRecord record) => record.Completion.WaitAsync(TimeSpan.FromSeconds(5));

    [Fact]
    public async Task Children_spending_past_the_parents_token_budget_cancel_the_parent_and_its_running_children()
    {
        SpendTokensThenWait(50);
        var registry = Registry();
        var parent = registry.Spawn(Spec with { Budget = new BudgetLimits(100, null, null) });
        var idle = registry.Spawn(Spec, parent.Handle);
        var first = registry.Spawn(Spec with { Task = "spends" }, parent.Handle);
        var second = registry.Spawn(Spec with { Task = "spends" }, first.Handle);

        var outcomes = await Task.WhenAll(new[] { parent, idle, first, second }.Select(Finished));

        Assert.All(outcomes, o => Assert.Equal((RunStatus.Cancelled, "budget exceeded: tokens 120/100"), (o.Status, o.Error)));
        var budget = registry.View(parent, DateTimeOffset.UtcNow).Budget!;
        Assert.Equal(new BudgetOverrun("tokens", 120, 100), budget.Exceeded);
        Assert.Equal((120L, 0), (budget.Used.Tokens, budget.Used.ToolCalls));
    }

    [Fact]
    public async Task A_childs_budget_cancels_only_its_own_subtree()
    {
        SpendTokensThenWait(50);
        var registry = Registry();
        var parent = registry.Spawn(Spec);
        var sibling = registry.Spawn(Spec, parent.Handle);
        var child = registry.Spawn(Spec with { Budget = new BudgetLimits(40, null, null) }, parent.Handle);
        var grandchild = registry.Spawn(Spec with { Task = "spends" }, child.Handle);

        var outcomes = await Task.WhenAll(Finished(child), Finished(grandchild));

        Assert.All(outcomes, o => Assert.Equal("budget exceeded: tokens 60/40", o.Error));
        Assert.Equal(new BudgetOverrun("tokens", 60, 40), registry.View(child, DateTimeOffset.UtcNow).Budget!.Exceeded);
        Assert.False(parent.Cancel.IsCancellationRequested);
        Assert.False(sibling.Cancel.IsCancellationRequested);
        Assert.Null(registry.View(parent, DateTimeOffset.UtcNow).Budget);
    }

    [Fact]
    public async Task A_seconds_budget_trips_an_idle_subtree()
    {
        SpendTokensThenWait(0);
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var registry = Registry(clock);
        var parent = registry.Spawn(Spec with { Budget = new BudgetLimits(null, null, 30) });
        var child = registry.Spawn(Spec, parent.Handle);

        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(parent.Cancel.IsCancellationRequested);
        clock.Advance(TimeSpan.FromSeconds(1));

        var outcomes = await Task.WhenAll(Finished(parent), Finished(child));
        Assert.All(outcomes, o => Assert.Equal((RunStatus.Cancelled, "budget exceeded: seconds 30/30"), (o.Status, o.Error)));
        Assert.Equal(new BudgetOverrun("seconds", 30, 30), registry.View(parent, clock.GetUtcNow()).Budget!.Exceeded);
    }

    [Fact]
    public async Task A_budget_overrun_survives_a_restart()
    {
        SpendTokensThenWait(50);
        var parent = Registry().Spawn(Spec with { Task = "spends", Budget = new BudgetLimits(10, null, null) });
        await Finished(parent);
        journal!.Dispose();

        var resumed = Registry([.. RunJournal.Replay(path)]);
        resumed.ResumeAll();

        Assert.Equal(new BudgetOverrun("tokens", 60, 10), resumed.View(resumed.Find(parent.Handle)!, DateTimeOffset.UtcNow).Budget!.Exceeded);
    }

    private RunStart SingleStart()
    {
        SpinWait.SpinUntil(() => { lock (loop.Starts) return loop.Starts.Count > 0; }, TimeSpan.FromSeconds(5));
        lock (loop.Starts)
            return Assert.Single(loop.Starts);
    }
}
