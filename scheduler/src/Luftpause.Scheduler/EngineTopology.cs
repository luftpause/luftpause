using Luftpause.Scheduling;

namespace Luftpause.Scheduler;

public sealed class EngineEndpoint
{
    public required string Id { get; init; }
    public required string BaseUrl { get; init; }
    public EngineKind Kind { get; init; } = EngineKind.OpenAi;
    public string[] Models { get; init; } = [];
    public string? ApiKeyEnv { get; init; }
    public string HealthPath { get; init; } = "/health";
    public string[] LoadFrom { get; init; } = [];

    public IEnumerable<string> LoadSources() => LoadFrom.Length > 0 ? LoadFrom : [BaseUrl];
}

public enum EngineKind { OpenAi, VllmClassify }

public sealed class EngineTopology
{
    public EngineEndpoint[] Generative { get; init; } = [];
    public EngineEndpoint[] SinglePass { get; init; } = [];

    public IEnumerable<EngineEndpoint> Endpoints() => Generative.Concat(SinglePass);

    public IEnumerable<Replica> Replicas() =>
        Generative.Select(e => ToReplica(e, EngineClass.Generative))
            .Concat(SinglePass.Select(e => ToReplica(e, EngineClass.SinglePass)));

    public EngineCredentials Credentials(Func<string, string?> environment) => new(
        Endpoints()
            .Select(e => (e.Id, Key: e.ApiKeyEnv is { Length: > 0 } name ? environment(name) : null))
            .Where(e => !string.IsNullOrEmpty(e.Key))
            .ToDictionary(e => e.Id, e => $"Bearer {e.Key}"));

    private static Replica ToReplica(EngineEndpoint endpoint, EngineClass engineClass) => new(
        endpoint.Id,
        endpoint.BaseUrl,
        engineClass,
        endpoint.Kind == EngineKind.VllmClassify ? "/classify" : "/v1/chat/completions",
        endpoint.Models);
}

public sealed class EngineCredentials(IReadOnlyDictionary<string, string> authorization)
{
    public string? AuthorizationFor(string replicaId) => authorization.GetValueOrDefault(replicaId);
}
