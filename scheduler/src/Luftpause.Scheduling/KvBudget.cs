namespace Luftpause.Scheduling;

public static class KvBudget
{
    public const double DefaultTokensPerByte = 0.25;

    public static Turn Turn(SchedulerState state, TurnRequested request, Replica replica) =>
        new(request.TurnId, request.RunId, replica.Id, request.AtUnixMs, Need(state, replica.Id, request.RequestBytes), request.RequestBytes, request.Caller);

    public static bool Fits(SchedulerState state, Replica replica, TurnRequested request) =>
        state.InFlightOn(replica.Id) == 0
        || InFlightTokens(state, replica.Id) + Need(state, replica.Id, request.RequestBytes) <= state.KvPool[replica.Id];

    public static int InFlightTokens(SchedulerState state, string replicaId) =>
        state.Turns.Values.Where(t => t.ReplicaId == replicaId).Sum(t => t.KvTokens);

    public static SchedulerState Record(SchedulerState state, Turn turn, TokenUsage usage)
    {
        var seen = state.Usage.GetValueOrDefault(turn.ReplicaId, ReplicaUsage.None);
        return state with
        {
            Usage = state.Usage.SetItem(turn.ReplicaId, new ReplicaUsage(
                seen.PromptTokens + usage.Prompt,
                seen.RequestBytes + turn.RequestBytes,
                seen.CompletionTokens + usage.Completion,
                seen.Turns + 1)),
        };
    }

    private static int Need(SchedulerState state, string replicaId, int requestBytes)
    {
        var seen = state.Usage.GetValueOrDefault(replicaId, ReplicaUsage.None);
        var tokensPerByte = seen.RequestBytes > 0 ? (double)seen.PromptTokens / seen.RequestBytes : DefaultTokensPerByte;
        var completion = seen.Turns > 0 ? seen.CompletionTokens / seen.Turns : 0;
        return (int)Math.Ceiling(requestBytes * tokensPerByte) + (int)completion;
    }
}
