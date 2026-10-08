using Luftpause.Scheduling;

namespace Luftpause.Scheduler;

public sealed class HealthProbe(
    EngineTopology topology,
    EngineCredentials credentials,
    Dispatcher dispatcher,
    IHttpClientFactory clients,
    TimeProvider time,
    ILogger<HealthProbe> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var endpoints = topology.Endpoints().ToList();
        var healthy = endpoints.ToDictionary(e => e.Id, _ => true);
        using var http = clients.CreateClient(nameof(HealthProbe));
        using var timer = new PeriodicTimer(Probing.Period, time);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var results = await Task.WhenAll(endpoints.Select(async e => (e.Id, Up: await IsUpAsync(http, e, stoppingToken))));
            foreach (var (id, up) in results.Where(r => healthy[r.Id] != r.Up))
            {
                healthy[id] = up;
                log.LogWarning("Replica {Replica} is now {State}", id, up ? "healthy" : "unhealthy");
                dispatcher.Apply(new ReplicaHealthChanged(id, up, time.GetUtcNow().ToUnixTimeMilliseconds()));
            }
        }
    }

    private async Task<bool> IsUpAsync(HttpClient http, EngineEndpoint endpoint, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.BaseUrl.TrimEnd('/')}{endpoint.HealthPath}");
        if (credentials.AuthorizationFor(endpoint.Id) is { } authorization)
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        try
        {
            using var response = await http.SendAsync(request, cancel);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}
