using System.Text.Json.Serialization;
using Luftpause.Scheduler;
using Luftpause.Scheduling;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.Http2.MaxStreamsPerConnection = int.MaxValue);

var topology = builder.Configuration.GetSection("Engines").Get<EngineTopology>() ?? new EngineTopology();

builder.Services.AddGrpc();
builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(topology);
builder.Services.AddSingleton(topology.Credentials(Environment.GetEnvironmentVariable));
builder.Services.AddSingleton(new StateStore(
    new Decider(Routing(builder.Configuration["Experiment:Routing"])),
    SchedulerState.Initial(topology.Replicas())));
builder.Services.AddSingleton<AdmissionWaiters>();
builder.Services.AddHttpClient(nameof(HealthProbe), http => http.Timeout = Probing.Period);
builder.Services.AddHttpClient(nameof(LoadProbe), http => http.Timeout = Probing.Period);
builder.Services.AddSingleton<Dispatcher>();
builder.Services.AddHostedService<Clock>();
builder.Services.AddHostedService<HealthProbe>();
builder.Services.AddHostedService<LoadProbe>();

var app = builder.Build();

app.MapGrpcService<SchedulerService>();
app.MapGet("/status", (StateStore store, TimeProvider time) =>
    StatusView.From(store.Current, time.GetUtcNow().ToUnixTimeMilliseconds()));

app.Run();

static IRoutingPolicy Routing(string? name) => name?.ToLowerInvariant() switch
{
    "roundrobin" => new RoundRobinRouting(),
    "leastloaded" => new LeastLoadedRouting(),
    _ => new AffinityRouting(new LeastLoadedRouting()),
};
