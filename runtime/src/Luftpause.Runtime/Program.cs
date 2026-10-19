using System.Text.Json.Serialization;
using Luftpause.Runtime;

var builder = WebApplication.CreateBuilder(args);

var dataPlane = builder.Configuration["DataPlane:Url"] ?? "http://localhost:8080/";
var servers = builder.Configuration.GetSection("Tools:Servers").Get<ToolServer[]>() ?? [];
var defaults = builder.Configuration.GetSection("Runtime").Get<RuntimeDefaults>()
    ?? throw new InvalidOperationException("Runtime:Model is required.");
var stateDir = builder.Configuration["Runtime:StateDir"] ?? "state";
Directory.CreateDirectory(stateDir);
var journalPath = Path.Combine(stateDir, "runs.jsonl");
var token = BearerAuth.Token(builder.Configuration);

builder.Services.AddLuftpauseTracing(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(defaults);
builder.Services.AddHttpClient<LlmClient>(http =>
{
    http.BaseAddress = new Uri(dataPlane.EndsWith('/') ? dataPlane : dataPlane + "/");
    http.Timeout = TimeSpan.FromMinutes(10);
    if (token is not null)
        BearerAuth.Authorize(http, token);
});
builder.Services.AddSingleton(sp => new ToolCatalog(servers, sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<IRunLoop, AgentLoop>();
builder.Services.AddSingleton(RunJournal.Replay(journalPath));
builder.Services.AddSingleton(_ => new RunJournal(journalPath));
builder.Services.AddSingleton<RunRegistry>();
builder.Services.AddLuftpauseMcp(new TaskJournal(Path.Combine(stateDir, "tasks.jsonl"), TimeProvider.System));
builder.Services.AddCors();
builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();
app.Services.GetRequiredService<RunRegistry>().ResumeAll();

app.UseCors();
app.UseBearerAuth(token, app.Logger);
app.UseClientCapabilityLog(app.Logger);
app.MapMcp("/mcp");
app.MapGet("/status", (RunRegistry runs, TimeProvider time) =>
    {
        var now = time.GetUtcNow();
        return new { AtUnixMs = now.ToUnixTimeMilliseconds(), Roots = RunTree.Build(runs.All, now) };
    })
    .RequireCors(cors => cors.AllowAnyOrigin().WithMethods("GET"));

app.Run();
