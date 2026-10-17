using System.Text.Json.Nodes;

namespace Luftpause.Runtime;

public static class GenAiTags
{
    public static IReadOnlyList<KeyValuePair<string, object?>> Run(string handle, RunSpec spec) =>
    [
        new("gen_ai.operation.name", "invoke_agent"),
        new("gen_ai.agent.id", handle),
        new("gen_ai.request.model", spec.Model),
        new("luftpause.service_class", spec.ServiceClass),
    ];

    public static IReadOnlyList<KeyValuePair<string, object?>> RunOutcome(RunOutcome outcome) =>
        outcome.Status == RunStatus.Failed
            ? [new("luftpause.run.status", outcome.Status.ToString()), new("error.type", "run_failed")]
            : [new("luftpause.run.status", outcome.Status.ToString())];

    public static IReadOnlyList<KeyValuePair<string, object?>> ChatRequest(RunSpec spec) =>
    [
        new("gen_ai.operation.name", "chat"),
        new("gen_ai.request.model", spec.Model),
        .. Present("gen_ai.request.max_tokens", spec.MaxTokens),
    ];

    public static IReadOnlyList<KeyValuePair<string, object?>> ChatResponse(LlmReply reply) =>
    [
        new("gen_ai.usage.input_tokens", reply.Usage.Prompt),
        new("gen_ai.usage.output_tokens", reply.Usage.Completion),
        .. Present("gen_ai.response.finish_reasons", reply.FinishReason is { } finish ? new[] { finish } : null),
    ];

    public static IReadOnlyList<KeyValuePair<string, object?>> Tool(JsonObject call) =>
    [
        new("gen_ai.operation.name", "execute_tool"),
        new("gen_ai.tool.name", call["function"]?["name"]?.GetValue<string>()),
        new("gen_ai.tool.call.id", call["id"]?.GetValue<string>()),
    ];

    private static IEnumerable<KeyValuePair<string, object?>> Present(string key, object? value) =>
        value is null ? [] : [new(key, value)];
}
