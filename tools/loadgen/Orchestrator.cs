using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Luftpause.LoadGen;

public sealed record SpawnOptions(
    Uri Endpoint, int Concurrency, string ServiceClass, string? Model, int DurationSeconds, int ContextChars, int MaxToolCalls,
    double DeepShare = 0,
    bool Sequential = false,
    int? MaxTokens = null);

public static class Orchestrator
{
    private const string Filler = "Background: the river basin survey covers banks, floods, silt, fish and the mills along the channel. ";

    public static async Task<string> RunAsync(SpawnOptions options, CancellationToken cancel)
    {
        await using var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions { Endpoint = options.Endpoint, Name = "luftpause" }),
            cancellationToken: cancel);
        if (options.Sequential)
            return await RunSequentialAsync(client, options, cancel);

        var wall = Stopwatch.StartNew();
        var deadline = TimeSpan.FromSeconds(options.DurationSeconds);
        var spawned = 0;
        var inFlight = new HashSet<string>();
        var groups = new Dictionary<string, string>();
        var finished = new List<JsonObject>();

        async Task Spawn()
        {
            var index = spawned++;
            var handle = await SpawnAsync(client, index, options, cancel);
            groups[handle] = Group(index, options.DeepShare);
            inFlight.Add(handle);
        }

        var first = await Task.WhenAll(Enumerable.Range(0, options.Concurrency).Select(i => SpawnAsync(client, i, options, cancel)));
        for (var i = 0; i < first.Length; i++)
            groups[first[i]] = Group(i, options.DeepShare);
        inFlight.UnionWith(first);
        spawned = first.Length;
        while (inFlight.Count > 0)
        {
            var remaining = options.DurationSeconds > 0 ? deadline - wall.Elapsed : TimeSpan.FromMinutes(10);
            if (remaining <= TimeSpan.Zero)
                break;

            var done = (await AwaitAsync(client, [.. inFlight], options.DurationSeconds > 0 ? "any" : "all", remaining, cancel))
                .Where(v => v["status"]?.ToString() != "Running")
                .ToList();
            foreach (var run in done)
            {
                inFlight.Remove(run["handle"]!.ToString());
                finished.Add(run);
                if (options.DurationSeconds > 0 && wall.Elapsed < deadline)
                    await Spawn();
            }
        }
        var window = wall.Elapsed;
        foreach (var handle in inFlight)
            await client.CallToolAsync("cancel", new Dictionary<string, object?> { ["handle"] = handle }, cancellationToken: cancel);

        var report = Summarise(finished, inFlight.Count, window);
        if (options.DeepShare <= 0)
            return report;
        var byGroup = finished
            .GroupBy(v => groups.GetValueOrDefault(v["handle"]!.ToString(), "?"))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var lines = Summarise(g.ToList(), 0, window).Split(Environment.NewLine);
                return $"[{g.Key}] {lines[1]}  {lines[2]}";
            });
        return string.Join(Environment.NewLine, [report, .. byGroup]);
    }

    private static async Task<string> RunSequentialAsync(McpClient client, SpawnOptions options, CancellationToken cancel)
    {
        var wall = Stopwatch.StartNew();
        var finished = new List<JsonObject>();
        for (var index = 0; index < options.Concurrency; index++)
        {
            var handle = await SpawnAsync(client, index, options, cancel);
            finished.AddRange(await AwaitAsync(client, [handle], "all", TimeSpan.FromMinutes(10), cancel));
        }
        return Summarise(finished, 0, wall.Elapsed);
    }

    private static async Task<string> SpawnAsync(McpClient client, int index, SpawnOptions options, CancellationToken cancel)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["task"] = $"Task {index}: find three facts about rivers, using the {Tool(index, options.DeepShare)} tool once per fact.{Padding(options.ContextChars)}",
            ["serviceClass"] = options.ServiceClass,
            ["maxToolCalls"] = options.MaxToolCalls,
            ["tools"] = new[] { Tool(index, options.DeepShare) },
        };
        if (options.Model is not null)
            arguments["model"] = options.Model;
        if (options.MaxTokens is not null)
            arguments["maxTokens"] = options.MaxTokens;
        var result = await client.CallToolAsync("spawn", arguments, cancellationToken: cancel);
        return Parse(result)?["handle"]?.GetValue<string>() ?? throw new InvalidOperationException($"spawn failed: {Text(result)}");
    }

    private static async Task<List<JsonObject>> AwaitAsync(
        McpClient client, string[] handles, string mode, TimeSpan timeout, CancellationToken cancel)
    {
        var result = await client.CallToolAsync(
            "await",
            new Dictionary<string, object?>
            {
                ["handles"] = handles,
                ["mode"] = mode,
                ["timeoutSeconds"] = (int)Math.Ceiling(Math.Min(timeout.TotalSeconds, 600)),
            },
            cancellationToken: cancel);
        return (Parse(result) as JsonArray ?? throw new InvalidOperationException($"await failed: {Text(result)}"))
            .OfType<JsonObject>().ToList();
    }

    private static string Summarise(List<JsonObject> finished, int unfinished, TimeSpan window)
    {
        var completed = finished.Where(v => v["status"]?.ToString() == "Completed").ToList();
        var seconds = completed.Select(v => v["elapsedSeconds"]?.GetValue<double>() ?? 0).Order().ToArray();
        var turns = completed.Sum(v => v["turns"]?.GetValue<int>() ?? 0);
        return string.Join(Environment.NewLine,
        [
            $"completed {completed.Count}  failed {finished.Count - completed.Count}  cut at deadline {unfinished}  window {window.TotalSeconds:F1}s",
            $"throughput {completed.Count / window.TotalMinutes:F2} runs/min  {turns / window.TotalMinutes:F1} turns/min",
            seconds.Length == 0 ? "run time n=0" : $"run time p50 {seconds[seconds.Length / 2]:F1}s  p90 {seconds[(int)(seconds.Length * 0.9)]:F1}s",
            .. finished.Where(v => !completed.Contains(v)).Take(3).Select(v => $"  {v["handle"]}: {v["status"]} {v["error"]}"),
        ]);
    }

    private static string Group(int index, double deepShare) =>
        Math.Floor((index + 1) * deepShare) > Math.Floor(index * deepShare) ? "deep" : "quick";

    private static string Tool(int index, double deepShare) => Group(index, deepShare) == "deep" ? "deep_search" : "lookup";

    private static string Padding(int chars)
    {
        if (chars <= 0)
            return "";
        var text = new StringBuilder("\n\n", chars + Filler.Length);
        while (text.Length < chars)
            text.Append(Filler);
        return text.ToString(0, chars);
    }

    private static JsonNode? Parse(CallToolResult result)
    {
        if (result.StructuredContent is { } structured)
        {
            var node = JsonNode.Parse(structured.GetRawText());
            return node is JsonObject { Count: 1 } wrapper && wrapper["result"] is { } inner ? inner : node;
        }
        try
        {
            return JsonNode.Parse(Text(result));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Text(CallToolResult result) =>
        string.Join("", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
}
