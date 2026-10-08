using Luftpause.Runtime;

var builder = WebApplication.CreateBuilder(args);

var dataPlane = builder.Configuration["DataPlane:Url"] ?? "http://localhost:8080/";
var servers = builder.Configuration.GetSection("Tools:Servers").Get<ToolServer[]>() ?? [];
var defaults = builder.Configuration.GetSection("Runtime").Get<RuntimeDefaults>()
    ?? throw new InvalidOperationException("Runtime:Model is required.");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(defaults);
builder.Services.AddSingleton(new EnginePriority(builder.Configuration.GetValue("Experiment:EnginePriority", false)));
builder.Services.AddHttpClient<LlmClient>(http =>
{
    http.BaseAddress = new Uri(dataPlane.EndsWith('/') ? dataPlane : dataPlane + "/");
    http.Timeout = TimeSpan.FromMinutes(10);
});
builder.Services.AddSingleton(sp => new ToolCatalog(servers, sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<AgentLoop>();
builder.Services.AddSingleton<RunRegistry>();
builder.Services.AddMcpServer().WithHttpTransport().WithTools<LuftpauseTools>();

var app = builder.Build();

app.UseClientCapabilityLog(app.Logger);
app.MapMcp("/mcp");

app.Run();
