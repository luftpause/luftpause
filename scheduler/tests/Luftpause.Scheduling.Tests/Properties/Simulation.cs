using System.Collections.Immutable;

namespace Luftpause.Scheduling.Tests.Properties;

public sealed record Step(int Index, SchedulerState Before, SchedulerEvent Event, Decision After)
{
    public IEnumerable<RouteTurn> Routes => After.Actions.OfType<RouteTurn>();

    public TurnRequested Requested(string turnId) =>
        Event is TurnRequested r && r.TurnId == turnId ? r : Before.Waiting.First(w => w.TurnId == turnId);
}

public sealed record Trace(Scenario Scenario, ImmutableList<Step> Steps)
{
    public string Describe(int throughStep) =>
        string.Join(Environment.NewLine, Steps.TakeWhile(s => s.Index <= throughStep).Select(s => $"  {s.Index}: {Format(s.Event)}{Outcome(s)}"));

    private static string Outcome(Step step) =>
        step.After.Actions.IsEmpty ? "" : " => " + string.Join(", ", step.After.Actions.Select(Format));

    private static string Format(SchedulerAction action) => action switch
    {
        RouteTurn r => $"route {r.TurnId}->{r.Replica.Id}",
        RefuseTurn r => $"refuse {r.TurnId}",
        _ => action.ToString(),
    };

    private static string Format(SchedulerEvent e) => e switch
    {
        TurnRequested r => $"@{r.AtUnixMs} TurnRequested {r.TurnId} {r.RunId} {r.Class} {r.Service} {r.Model} {r.Caller}{(r.Resumed ? " resumed" : "")}",
        TurnCompleted c => $"@{c.AtUnixMs} TurnCompleted {c.TurnId} {c.RunId} {c.FinishReason} prompt={c.Usage.Prompt} completion={c.Usage.Completion}",
        TurnAbandoned a => $"@{a.AtUnixMs} TurnAbandoned {a.TurnId} {a.RunId}",
        ReplicaLoadReported l => $"@{l.AtUnixMs} Load {l.ReplicaId} running={l.Running} waiting={l.Waiting}",
        ReplicaHealthChanged h => $"@{h.AtUnixMs} Health {h.ReplicaId} {(h.Healthy ? "healthy" : "unhealthy")}",
        RunEnded r => $"@{r.AtUnixMs} RunEnded {r.RunId}",
        Tick t => $"@{t.AtUnixMs} Tick",
        StateResynced r => $"@{r.AtUnixMs} Resync runs=[{string.Join("; ", r.Snapshot.Runs)}] turns=[{string.Join("; ", r.Snapshot.Turns)}]",
        _ => e.ToString(),
    };
}

public static class Simulation
{
    private static readonly Decider Decider = new(new AffinityRouting(new LeastLoadedRouting()));

    private sealed record Progress(SchedulerState State, long Now, int NextTurn, ImmutableList<Step> Steps);

    public static Trace Run(Scenario scenario)
    {
        var start = new Progress(Initial(scenario), 0, 0, ImmutableList<Step>.Empty);
        var end = scenario.Commands.Select((command, index) => (command, index)).Aggregate(start, (progress, c) => Apply(scenario, progress, c.command, c.index));
        return new Trace(scenario, end.Steps);
    }

    public static SchedulerState Initial(Scenario scenario) =>
        SchedulerState.Initial(scenario.Replicas) with { Capacity = Seed(scenario.Replicas, scenario.Capacity) };

    private static ImmutableDictionary<string, int> Seed(Replica[] replicas, int[]? values) =>
        values is null
            ? ImmutableDictionary<string, int>.Empty
            : replicas.Zip(values).ToImmutableDictionary(p => p.First.Id, p => p.Second);

    private static Progress Apply(Scenario scenario, Progress progress, Command command, int index)
    {
        var now = progress.Now + command.AdvanceMs;
        var resolved = Resolve(scenario, progress.State, command, now, progress.NextTurn);
        if (resolved is not { } e)
            return progress with { Now = now };

        var decision = Decider.Decide(progress.State, e);
        return new Progress(
            decision.State,
            now,
            e is TurnRequested ? progress.NextTurn + 1 : progress.NextTurn,
            progress.Steps.Add(new Step(index, progress.State, e, decision)));
    }

    private static SchedulerEvent? Resolve(Scenario scenario, SchedulerState state, Command command, long now, int nextTurn) => command switch
    {
        RequestTurn r => new TurnRequested($"t{nextTurn}", $"run{r.Run}", r.Engine, r.Model, r.Resumed, now, r.Service, r.Caller),
        CompleteTurn c => Pick(InFlight(state), c.Pick) is { } t
            ? new TurnCompleted(t.TurnId, t.RunId, c.ToolCalls ? "tool_calls" : "stop", new TokenUsage(c.PromptTokens, c.CompletionTokens, 0), false, now)
            : null,
        AbandonTurn { Held: true } a => Pick(state.Waiting, a.Pick) is { } w ? new TurnAbandoned(w.TurnId, w.RunId, now) : null,
        AbandonTurn a => Pick(InFlight(state), a.Pick) is { } t ? new TurnAbandoned(t.TurnId, t.RunId, now) : null,
        ReportLoad l => new ReplicaLoadReported(ReplicaId(scenario, l.Replica), l.Running, l.Waiting, now),
        SetHealth h => new ReplicaHealthChanged(ReplicaId(scenario, h.Replica), h.Healthy, now),
        EndRun r => new RunEnded($"run{r.Run}", now),
        TickClock => new Tick(now),
        ResyncState r => Resynced(r, now),
        _ => throw new InvalidOperationException($"unknown command {command}"),
    };

    private static StateResynced Resynced(ResyncState resync, long now)
    {
        var turns = resync.Turns.Select((t, i) => new Turn($"s{now}-{i}", $"run{t.Run}", $"r{t.Replica}", now, t.Caller)).ToList();
        var runs = resync.Runs.DistinctBy(r => r.Run).Select(r => new Run(
            $"run{r.Run}",
            r.Phase switch
            {
                0 => new Generating(turns.FirstOrDefault(t => t.RunId == $"run{r.Run}")?.TurnId ?? "gone"),
                1 => new AwaitingTool(now),
                _ => new Idle(now),
            },
            r.Replica is { } replica ? $"r{replica}" : null,
            1,
            now,
            now,
            r.Service));
        return new StateResynced(new Snapshot([.. runs], [.. turns]), now);
    }

    private static IReadOnlyList<Turn> InFlight(SchedulerState state) =>
        state.Turns.Values.OrderBy(t => t.TurnId, StringComparer.Ordinal).ToList();

    private static T? Pick<T>(IReadOnlyList<T> from, int pick) where T : class =>
        from.Count == 0 ? null : from[pick % from.Count];

    private static string ReplicaId(Scenario scenario, int index) => scenario.Replicas[index % scenario.Replicas.Length].Id;
}

public sealed class PropertyViolation(string message) : Exception(message);

public static class Invariant
{
    public static void HoldsAtEveryStep(Scenario scenario, Func<Step, string?> violation)
    {
        var trace = Simulation.Run(scenario);
        var failure = trace.Steps.Select(s => (step: s, why: violation(s))).FirstOrDefault(f => f.why is not null);
        if (failure.why is { } why)
            throw Violation(trace, failure.step, why);
    }

    public static void HoldsAcrossSteps<TMemory>(Scenario scenario, TMemory initial, Func<TMemory, Step, (TMemory Next, string? Violation)> check)
    {
        var trace = Simulation.Run(scenario);
        trace.Steps.Aggregate(initial, (memory, step) =>
        {
            var (next, why) = check(memory, step);
            return why is null ? next : throw Violation(trace, step, why);
        });
    }

    private static PropertyViolation Violation(Trace trace, Step step, string why) =>
        new($"after step {step.Index}: {why}{Environment.NewLine}events:{Environment.NewLine}{trace.Describe(step.Index)}");
}
