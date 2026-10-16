using Luftpause.Runtime;

var builder = WebApplication.CreateBuilder(args);

var dataPlane = builder.Configuration["DataPlane:Url"] ?? "http://localhost:8080/";
var servers = builder.Configuration.GetSection("Tools:Servers").Get<ToolServer[]>() ?? [];
var defaults = builder.Configuration.GetSection("Runtime").Get<RuntimeDefaults>()
    ?? throw new InvalidOperationException("Runtime:Model is required.");
var stateDir = builder.Configuration["Runtime:StateDir"] ?? "state";
Directory.CreateDirectory(stateDir);
var journalPath = Path.Combine(stateDir, "runs.jsonl");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(defaults);
builder.Services.AddHttpClient<LlmClient>(http =>
{
    http.BaseAddress = new Uri(dataPlane.EndsWith('/') ? dataPlane : dataPlane + "/");
    http.Timeout = TimeSpan.FromMinutes(10);
});
builder.Services.AddSingleton(sp => new ToolCatalog(servers, sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<IRunLoop, AgentLoop>();
builder.Services.AddSingleton(RunJournal.Replay(journalPath));
builder.Services.AddSingleton(_ => new RunJournal(journalPath));
builder.Services.AddSingleton<RunRegistry>();
builder.Services.AddMcpServer().WithHttpTransport().WithTools<LuftpauseTools>();

var app = builder.Build();
app.Services.GetRequiredService<RunRegistry>().ResumeAll();

app.UseClientCapabilityLog(app.Logger);
app.MapMcp("/mcp");

app.Run();
