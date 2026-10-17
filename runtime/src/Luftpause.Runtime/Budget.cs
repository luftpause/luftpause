using System.Globalization;

namespace Luftpause.Runtime;

public sealed record BudgetLimits(long? Tokens, int? ToolCalls, double? Seconds)
{
    public static BudgetLimits? Of(long? tokens, int? toolCalls, double? seconds)
    {
        if (tokens is <= 0)
            throw new ArgumentOutOfRangeException(nameof(tokens), tokens, "budgetTokens must be positive");
        if (toolCalls is <= 0)
            throw new ArgumentOutOfRangeException(nameof(toolCalls), toolCalls, "budgetToolCalls must be positive");
        if (seconds is not null && !(seconds > 0 && double.IsFinite(seconds.Value)))
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "budgetSeconds must be positive");
        return tokens is null && toolCalls is null && seconds is null ? null : new BudgetLimits(tokens, toolCalls, seconds);
    }
}

public sealed record BudgetUsed(long Tokens, int ToolCalls, double Seconds)
{
    public static BudgetUsed Of(WorkTotals subtree, double elapsedSeconds) =>
        new(subtree.PromptTokens + subtree.CompletionTokens, subtree.ToolCalls, elapsedSeconds);
}

public sealed record BudgetOverrun(string Which, double Used, double Limit)
{
    public string Describe() =>
        string.Create(CultureInfo.InvariantCulture, $"budget exceeded: {Which} {Used:0.###}/{Limit:0.###}");
}

public sealed record BudgetView(BudgetLimits Limits, BudgetUsed Used, BudgetOverrun? Exceeded);

public static class Budget
{
    public static BudgetOverrun? Check(BudgetLimits limits, WorkTotals subtree, double elapsedSeconds)
    {
        var used = BudgetUsed.Of(subtree, elapsedSeconds);
        return Over("tokens", used.Tokens, limits.Tokens)
            ?? Over("toolCalls", used.ToolCalls, limits.ToolCalls)
            ?? OutOfTime(used.Seconds, limits.Seconds);
    }

    private static BudgetOverrun? Over(string which, long used, long? limit) =>
        used > limit ? new BudgetOverrun(which, used, limit.Value) : null;

    private static BudgetOverrun? OutOfTime(double elapsed, double? limit) =>
        elapsed >= limit ? new BudgetOverrun("seconds", elapsed, limit.Value) : null;
}
