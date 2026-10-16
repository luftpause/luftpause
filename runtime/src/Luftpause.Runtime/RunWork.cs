namespace Luftpause.Runtime;

public sealed record TokenUsage(int Prompt, int? Cached, int Completion);

public sealed record RunWork(
    int Turns, int ToolCalls, int FailedToolCalls, long PromptTokens, long? CachedTokens, long CompletionTokens, double TurnSeconds, double ToolSeconds)
{
    public static readonly RunWork None = new(0, 0, 0, 0, 0, 0, 0, 0);

    public RunWork AfterTurn(TokenUsage usage, TimeSpan took) => this with
    {
        Turns = Turns + 1,
        PromptTokens = PromptTokens + usage.Prompt,
        CachedTokens = CachedTokens + usage.Cached,
        CompletionTokens = CompletionTokens + usage.Completion,
        TurnSeconds = TurnSeconds + took.TotalSeconds,
    };

    public RunWork AfterTools(int calls, int failed, TimeSpan took) => this with
    {
        ToolCalls = ToolCalls + calls,
        FailedToolCalls = FailedToolCalls + failed,
        ToolSeconds = ToolSeconds + took.TotalSeconds,
    };
}
