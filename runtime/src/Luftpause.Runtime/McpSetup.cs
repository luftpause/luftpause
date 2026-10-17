using ModelContextProtocol.AspNetCore;

namespace Luftpause.Runtime;

public static class McpSetup
{
    public static IServiceCollection AddLuftpauseMcp(this IServiceCollection services)
    {
        services.AddMcpServer()
            .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.StatefulForInitializeClients)
            .WithTools<LuftpauseTools>();
        return services;
    }
}
