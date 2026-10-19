using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Extensions.Tasks;

namespace Luftpause.Runtime;

public static class McpSetup
{
    public static IServiceCollection AddLuftpauseMcp(this IServiceCollection services, IMcpTaskStore tasks)
    {
        services.AddMcpServer()
            .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.StatefulForInitializeClients)
            .WithTools<LuftpauseTools>()
            .WithTasks(tasks, o => o.ExecutionModeSelector = call =>
                call.Params?.Name == "run" ? McpTaskExecutionMode.Optional : McpTaskExecutionMode.Synchronous);
        return services;
    }
}
