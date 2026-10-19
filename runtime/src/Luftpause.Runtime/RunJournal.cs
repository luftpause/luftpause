using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Luftpause.Runtime;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RunSpawned), "spawned")]
[JsonDerivedType(typeof(RunTurned), "turned")]
[JsonDerivedType(typeof(RunFinished), "finished")]
[JsonDerivedType(typeof(RunApprovalDecided), "approval")]
public abstract record JournalRecord(string Handle);

public sealed record RunSpawned(string Handle, RunSpec Spec, DateTimeOffset StartedAt, string? Parent) : JournalRecord(Handle);

public sealed record RunTurned(string Handle, IReadOnlyList<JsonObject> NewMessages, RunWork Work) : JournalRecord(Handle);

public sealed record RunFinished(string Handle, RunOutcome Outcome, DateTimeOffset FinishedAt, BudgetOverrun? Exceeded = null) : JournalRecord(Handle);

public sealed record RunApprovalDecided(string Handle, string Tool, JsonNode Arguments, bool Allow) : JournalRecord(Handle);

public sealed record ReplayedRun(RunSpawned Spawned, IReadOnlyList<RunTurned> Turns, RunFinished? Finished, IReadOnlyList<RunApprovalDecided> OpenDecisions)
{
    public static IReadOnlyList<ReplayedRun> From(IReadOnlyList<JournalRecord> history)
    {
        var spawned = history.OfType<RunSpawned>().ToList();
        var known = spawned.Select(s => s.Handle).ToHashSet(StringComparer.Ordinal);
        if (history.FirstOrDefault(r => !known.Contains(r.Handle)) is { } orphan)
            throw new InvalidOperationException($"journal has a {orphan.GetType().Name} for unspawned run '{orphan.Handle}'");
        var turns = history.OfType<RunTurned>().ToLookup(t => t.Handle, StringComparer.Ordinal);
        var finished = history.OfType<RunFinished>().ToDictionary(f => f.Handle, StringComparer.Ordinal);
        var open = Unconsumed(history);
        return [.. spawned.Select(s => new ReplayedRun(s, [.. turns[s.Handle]], finished.GetValueOrDefault(s.Handle), open.GetValueOrDefault(s.Handle, [])))];
    }

    private static ImmutableDictionary<string, ImmutableList<RunApprovalDecided>> Unconsumed(IReadOnlyList<JournalRecord> history) =>
        history.Aggregate(
            ImmutableDictionary.Create<string, ImmutableList<RunApprovalDecided>>(StringComparer.Ordinal),
            (open, record) => record switch
            {
                RunApprovalDecided decided => open.SetItem(decided.Handle, open.GetValueOrDefault(decided.Handle, []).Add(decided)),
                RunTurned turned => open.Remove(turned.Handle),
                _ => open,
            });

    public RunRecord Record()
    {
        var record = new RunRecord(Spawned.Handle, Spawned.Spec, Spawned.StartedAt, new CancellationTokenSource(), Spawned.Parent)
        {
            Messages = [.. AgentLoop.InitialMessages(Spawned.Spec), .. Turns.SelectMany(t => t.NewMessages)],
            Work = Turns.Count > 0 ? Turns[^1].Work : RunWork.None,
            Exceeded = Finished?.Exceeded,
            Decisions = OpenDecisions,
        };
        if (Finished is not null)
        {
            record.Completion = Task.FromResult(Finished.Outcome);
            record.Finished = Finished.FinishedAt;
        }
        return record;
    }
}

public sealed class RunJournal(string path) : IDisposable
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter(), new ReadOnlySetConverter() } };

    private readonly JsonLines<JournalRecord> lines = new(path, Options);

    public static IReadOnlyList<JournalRecord> Replay(string path) => JsonLines<JournalRecord>.Read(path, Options);

    public void Append(JournalRecord record) => lines.Append(record);

    public void Dispose() => lines.Dispose();
}

internal sealed class ReadOnlySetConverter : JsonConverter<IReadOnlySet<string>>
{
    public override IReadOnlySet<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        (JsonSerializer.Deserialize<string[]>(ref reader, options) ?? throw new JsonException("tools must be an array"))
            .ToHashSet(StringComparer.Ordinal);

    public override void Write(Utf8JsonWriter writer, IReadOnlySet<string> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.ToArray(), options);
}
