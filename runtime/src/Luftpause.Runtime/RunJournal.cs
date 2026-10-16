using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Luftpause.Runtime;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RunSpawned), "spawned")]
[JsonDerivedType(typeof(RunTurned), "turned")]
[JsonDerivedType(typeof(RunFinished), "finished")]
public abstract record JournalRecord(string Handle);

public sealed record RunSpawned(string Handle, RunSpec Spec, DateTimeOffset StartedAt, string? Parent) : JournalRecord(Handle);

public sealed record RunTurned(string Handle, IReadOnlyList<JsonObject> NewMessages, RunWork Work) : JournalRecord(Handle);

public sealed record RunFinished(string Handle, RunOutcome Outcome, DateTimeOffset FinishedAt) : JournalRecord(Handle);

public sealed class RunJournal : IDisposable
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter(), new ReadOnlySetConverter() } };

    private readonly Lock gate = new();
    private readonly StreamWriter writer;

    public RunJournal(string path)
    {
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        stream.SetLength(CompleteLength(stream));
        stream.Seek(0, SeekOrigin.End);
        writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    }

    public static IReadOnlyList<JournalRecord> Replay(string path)
    {
        if (!File.Exists(path))
            return [];
        var lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToList();
        return [.. lines.Select((line, i) => Parse(line, i == lines.Count - 1)).OfType<JournalRecord>()];
    }

    public void Append(JournalRecord record)
    {
        var line = JsonSerializer.Serialize(record, Options);
        lock (gate)
            writer.WriteLine(line);
    }

    public void Dispose() => writer.Dispose();

    private static JournalRecord? Parse(string line, bool last)
    {
        try
        {
            return JsonSerializer.Deserialize<JournalRecord>(line, Options)
                ?? throw new JsonException($"journal line is null: {line}");
        }
        catch (JsonException) when (last)
        {
            return null;
        }
    }

    private static long CompleteLength(FileStream stream)
    {
        for (var position = stream.Length - 1; position >= 0; position--)
        {
            stream.Seek(position, SeekOrigin.Begin);
            if (stream.ReadByte() == '\n')
                return position + 1;
        }
        return 0;
    }
}

internal sealed class ReadOnlySetConverter : JsonConverter<IReadOnlySet<string>>
{
    public override IReadOnlySet<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        (JsonSerializer.Deserialize<string[]>(ref reader, options) ?? throw new JsonException("tools must be an array"))
            .ToHashSet(StringComparer.Ordinal);

    public override void Write(Utf8JsonWriter writer, IReadOnlySet<string> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.ToArray(), options);
}
