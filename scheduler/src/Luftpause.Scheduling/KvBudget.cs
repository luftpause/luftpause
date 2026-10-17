namespace Luftpause.Scheduling;

public static class KvBudget
{
    public const double DefaultTokensPerByte = 0.25;

    public static Turn Turn(SchedulerState state, TurnRequested request, Replica replica) =>
        new(request.TurnId, request.RunId, replica.Id, request.AtUnixMs, Need(state, replica.Id, request), request.RequestBytes, request.Caller, request.Model);

    public static bool Fits(SchedulerState state, Replica replica, TurnRequested request) =>
        state.InFlightOn(replica.Id) == 0
        || InFlightTokens(state, replica.Id) + Need(state, replica.Id, request) <= state.KvPool[replica.Id];

    public static int InFlightTokens(SchedulerState state, string replicaId) =>
        state.Turns.Values.Where(t => t.ReplicaId == replicaId).Sum(t => t.KvTokens);

    public static SchedulerState Record(SchedulerState state, Turn turn, TokenUsage usage)
    {
        var key = new UsageKey(turn.ReplicaId, turn.Model);
        var seen = state.Usage.GetValueOrDefault(key, ReplicaUsage.None);
        return state with
        {
            Usage = state.Usage.SetItem(key, seen.Plus(new ReplicaUsage(usage.Prompt, turn.RequestBytes, usage.Completion, 1))),
        };
    }

    private static int Need(SchedulerState state, string replicaId, TurnRequested request)
    {
        var seen = Observed(state, replicaId, request.Model);
        var tokensPerByte = seen.RequestBytes > 0 ? (double)seen.PromptTokens / seen.RequestBytes : DefaultTokensPerByte;
        var completion = seen.Turns > 0 ? seen.CompletionTokens / seen.Turns : 0;
        return (int)Math.Ceiling(request.RequestBytes * tokensPerByte) + (int)completion;
    }

    private static ReplicaUsage Observed(SchedulerState state, string replicaId, string model) =>
        state.Usage.TryGetValue(new UsageKey(replicaId, model), out var pair) ? pair : AcrossModels(state, replicaId);

    private static ReplicaUsage AcrossModels(SchedulerState state, string replicaId) =>
        state.Usage.Where(u => u.Key.ReplicaId == replicaId).Aggregate(ReplicaUsage.None, (sum, u) => sum.Plus(u.Value));
}
