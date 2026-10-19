using System.Text;
using System.Text.Json;

namespace Luftpause.Runtime;

public sealed class JsonLines<T> : IDisposable where T : class
{
    private readonly JsonSerializerOptions options;
    private readonly Lock gate = new();
    private readonly StreamWriter writer;

    public JsonLines(string path, JsonSerializerOptions options)
    {
        this.options = options;
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        stream.SetLength(CompleteLength(stream));
        stream.Seek(0, SeekOrigin.End);
        writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    }

    public static IReadOnlyList<T> Read(string path, JsonSerializerOptions options)
    {
        if (!File.Exists(path))
            return [];
        var lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToList();
        return [.. lines.Select((line, i) => Parse(line, i == lines.Count - 1, options)).OfType<T>()];
    }

    public void Append(T record)
    {
        var line = JsonSerializer.Serialize(record, options);
        lock (gate)
            writer.WriteLine(line);
    }

    public void Dispose() => writer.Dispose();

    private static T? Parse(string line, bool last, JsonSerializerOptions options)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(line, options)
                ?? throw new JsonException($"line is null: {line}");
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
