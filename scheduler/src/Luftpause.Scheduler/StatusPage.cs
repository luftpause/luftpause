namespace Luftpause.Scheduler;

public static class StatusPage
{
    public static readonly string Html = Load();

    private static string Load()
    {
        using var stream = typeof(StatusPage).Assembly.GetManifestResourceStream("status.html")
            ?? throw new InvalidOperationException("status.html is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
