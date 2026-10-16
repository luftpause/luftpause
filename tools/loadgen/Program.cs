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

if (options.Spawn.DurationSeconds > 0)
{
    var deadline = TimeSpan.FromSeconds(options.Spawn.DurationSeconds);
    var clock = Stopwatch.StartNew();
    var perWorker = await Task.WhenAll(Enumerable.Range(0, options.Agents).Select(async worker =>
    {
        var times = new List<double>();
        var failed = 0;
        for (var n = 0; clock.Elapsed < deadline; n++)
        {
            var started = clock.Elapsed;
            var samples = await run(worker * 100_000 + n);
            var finished = clock.Elapsed;
            if (finished > deadline)
                break;
            if (samples.All(s => s.Status == 200))
                times.Add((finished - started).TotalSeconds);
            else
                failed++;
        }
        return (times, failed);
    }));
    var all = perWorker.SelectMany(w => w.times).Order().ToArray();
    Console.WriteLine($"completed {all.Length}  failed {perWorker.Sum(w => w.failed)}  window {deadline.TotalSeconds:F1}s");
    Console.WriteLine($"throughput {all.Length / deadline.TotalMinutes:F2} runs/min");
    Console.WriteLine(all.Length == 0 ? "run time n=0" : $"run time p50 {all[all.Length / 2]:F1}s  p90 {all[(int)(all.Length * 0.9)]:F1}s");
    return;
}

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
