namespace Luftpause.Scheduling.Tests.Properties;

public static class Shrinker
{
    public static IEnumerable<Scenario> Shrink(Scenario scenario) =>
        Halves(scenario)
            .Concat(Removals(scenario))
            .Concat(FewerReplicas(scenario))
            .Concat(Simplifications(scenario));

    private static IEnumerable<Scenario> Halves(Scenario scenario)
    {
        var commands = scenario.Commands;
        if (commands.Length < 4)
            return [];
        var half = commands.Length / 2;
        return [scenario with { Commands = commands[..half] }, scenario with { Commands = commands[half..] }];
    }

    private static IEnumerable<Scenario> Removals(Scenario scenario) =>
        Enumerable.Range(0, scenario.Commands.Length)
            .Reverse()
            .Select(i => scenario with { Commands = [.. scenario.Commands[..i], .. scenario.Commands[(i + 1)..]] });

    private static IEnumerable<Scenario> FewerReplicas(Scenario scenario) =>
        scenario.Replicas.Length <= 1
            ? []
            : [scenario with
            {
                Replicas = scenario.Replicas[..^1],
                Capacity = scenario.Capacity?[..^1],
                KvPool = scenario.KvPool?[..^1],
            }];

    private static IEnumerable<Scenario> Simplifications(Scenario scenario) =>
        Enumerable.Range(0, scenario.Commands.Length).SelectMany(i =>
            Simpler(scenario.Commands[i]).Select(simpler => scenario with
            {
                Commands = [.. scenario.Commands[..i], simpler, .. scenario.Commands[(i + 1)..]],
            }));

    private static IEnumerable<Command> Simpler(Command command)
    {
        if (command.AdvanceMs != 0)
            yield return command with { AdvanceMs = 0 };

        switch (command)
        {
            case RequestTurn { Resumed: true } r:
                yield return r with { Resumed = false };
                break;
            case CompleteTurn c when c.Pick != 0:
                yield return c with { Pick = 0 };
                break;
            case AbandonTurn a when a.Pick != 0:
                yield return a with { Pick = 0 };
                break;
            case ReportLoad { Running: > 0 } l:
                yield return l with { Running = 0 };
                break;
        }

        foreach (var simpler in SmallerNumbers(command))
            yield return simpler;
    }

    private static IEnumerable<Command> SmallerNumbers(Command command) => command switch
    {
        RequestTurn r when r.RequestBytes > 0 => [r with { RequestBytes = 0 }, r with { RequestBytes = r.RequestBytes / 2 }],
        CompleteTurn c when c.PromptTokens > 0 || c.CompletionTokens > 0 =>
            [c with { PromptTokens = 0, CompletionTokens = 0 }, c with { PromptTokens = c.PromptTokens / 2, CompletionTokens = c.CompletionTokens / 2 }],
        ReportLoad { Waiting: > 0 } l => [l with { Waiting = 0 }],
        _ => [],
    };
}
