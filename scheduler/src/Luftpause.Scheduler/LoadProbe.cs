using Luftpause.Scheduling;

namespace Luftpause.Scheduler;

public sealed class LoadProbe(
    EngineTopology topology,
    EngineCredentials credentials,
    Dispatcher dispatcher,
    IHttpClientFactory clients,
    TimeProvider time,
    ILogger<LoadProbe> log)
    : BackgroundService
{
    private readonly Dictionary<string, string> logged = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var endpoints = topology.Endpoints().ToList();
        using var http = clients.CreateClient(nameof(LoadProbe));
        using var timer = new PeriodicTimer(Probing.Period, time);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var loads = await Task.WhenAll(endpoints.Select(async e => (e.Id, Load: await LoadOfAsync(http, e, stoppingToken))));
            foreach (var (id, load) in loads.Where(l => l.Load is not null))
                Report(id, load!);
        }
    }

    private void Report(string replicaId, EngineLoadSample load)
    {
        var decision = dispatcher.Apply(new ReplicaLoadReported(replicaId, load.Running, load.Waiting, time.GetUtcNow().ToUnixTimeMilliseconds()));
        var capacity = decision.State.Capacity.TryGetValue(replicaId, out var c) ? c.ToString() : "open";
        if (logged.GetValueOrDefault(replicaId) == capacity)
            return;
        logged[replicaId] = capacity;
        log.LogInformation("replica_capacity {Replica} capacity={Capacity} running={Running} waiting={Waiting}", replicaId, capacity, load.Running, load.Waiting);
    }

    private async Task<EngineLoadSample?> LoadOfAsync(HttpClient http, EngineEndpoint endpoint, CancellationToken cancel)
    {
        var samples = await Task.WhenAll(endpoint.LoadSources().Select(source => SampleAsync(http, endpoint, source, cancel)));
        return samples.Any(s => s is null) ? null : EngineLoadSample.Sum(samples!);
    }

    private async Task<EngineLoadSample?> SampleAsync(HttpClient http, EngineEndpoint endpoint, string source, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{source.TrimEnd('/')}/metrics");
        if (credentials.AuthorizationFor(endpoint.Id) is { } authorization)
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        try
        {
            using var response = await http.SendAsync(request, cancel);
            return response.IsSuccessStatusCode ? EngineMetrics.Parse(await response.Content.ReadAsStringAsync(cancel)) : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }
}
