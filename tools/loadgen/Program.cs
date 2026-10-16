using System.Diagnostics;
using Luftpause.LoadGen;

var options = Options.Parse(args);
if (args.Contains("--spawn"))
{
    Console.WriteLine(await Orchestrator.RunAsync(options.Spawn, CancellationToken.None));
    return;
}
using var http = new HttpClient { BaseAddress = new Uri(options.BaseUrl), Timeout = TimeSpan.FromMinutes(5) };
Func<int, Task<IReadOnlyList<TurnSample>>> run = options.DecideCalls > 0
    ? i => new DecisionAgent(http, options.Shape, options.DecideCalls).RunAsync(i, CancellationToken.None)
    : i => new Agent(http, options.Shape).RunAsync(i, CancellationToken.None);

var wall = Stopwatch.StartNew();
var runs = await Task.WhenAll(Enumerable.Range(0, options.Agents).Select(run));
Console.WriteLine(Report.Summarise(runs, wall.Elapsed));

internal sealed record Options(int Agents, string BaseUrl, AgentShape Shape, int DecideCalls, SpawnOptions Spawn)
{
    public static Options Parse(string[] args)
    {
        string Value(string name, string fallback)
        {
            var at = Array.IndexOf(args, name);
            return at >= 0 && at + 1 < args.Length ? args[at + 1] : fallback;
        }
        var toolMs = Value("--tool-ms", "200").Split('-');
        return new Options(
            int.Parse(Value("--agents", "10")),
            Value("--base", "http://localhost:8080/"),
            new AgentShape(
                Value("--model", "fake"),
                !args.Contains("--no-run-header"),
                int.Parse(Value("--context-chars", "400")),
                int.Parse(Value("--tool-result-chars", "200")),
                int.Parse(toolMs[0]),
                int.Parse(toolMs[^1]),
                Value("--class", "interactive"),
                args.Contains("--tools-mcp") ? new Uri(Value("--tools-mcp", "")) : null,
                int.Parse(Value("--think-ms", "0")),
                args.Contains("--max-tool-calls") ? int.Parse(Value("--max-tool-calls", "3")) : int.MaxValue,
                Value("--caller", "")),
            int.Parse(Value("--decide-calls", "0")),
            new SpawnOptions(
                new Uri(Value("--mcp", "http://localhost:7000/mcp")),
                int.Parse(Value("--spawn", "10")),
                Value("--class", "interactive"),
                args.Contains("--model") ? Value("--model", "") : null,
                int.Parse(Value("--duration-s", "0")),
                int.Parse(Value("--context-chars", "0")),
                int.Parse(Value("--max-tool-calls", "3")),
                double.Parse(Value("--deep-share", "0"), System.Globalization.CultureInfo.InvariantCulture),
                args.Contains("--sequential"),
                args.Contains("--max-tokens") ? int.Parse(Value("--max-tokens", "0")) : null,
                args.Contains("--records") ? Value("--records", "") : null,
                args.Contains("--tasks") ? int.Parse(Value("--tasks", "0")) : null));
    }
}
