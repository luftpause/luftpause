namespace Luftpause.LoadGen;

public sealed record AgentShape(
    string Model, bool SendRunHeader, int ContextChars, int ToolResultChars, int ToolMsMin, int ToolMsMax, string Service,
    Uri? ToolsMcp = null, int ThinkMs = 0, int MaxToolCalls = int.MaxValue, string Caller = "")
{
    public TimeSpan NextToolPause() =>
        TimeSpan.FromMilliseconds(Random.Shared.Next(ToolMsMin, ToolMsMax + 1));
}
