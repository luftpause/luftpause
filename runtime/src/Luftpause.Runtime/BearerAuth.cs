using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Luftpause.Runtime;

public static class BearerAuth
{
    public const string Variable = "LUFTPAUSE_TOKEN";

    public static string? Token(IConfiguration configuration) =>
        configuration[Variable] is { Length: > 0 } token ? token : null;

    public static IApplicationBuilder UseBearerAuth(this IApplicationBuilder app, string? token, ILogger log)
    {
        if (token is null)
        {
            log.LogWarning("{Variable} is not set, the runtime is open to anyone who can reach it", Variable);
            return app;
        }

        var expected = Encoding.UTF8.GetBytes($"Bearer {token}");
        return app.Use(async (context, next) =>
        {
            var presented = Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString());
            if (CryptographicOperations.FixedTimeEquals(presented, expected))
            {
                await next();
                return;
            }
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await context.Response.WriteAsJsonAsync(new { error = new { message = "missing or invalid bearer token" } });
        });
    }

    public static void Authorize(HttpClient http, string token) =>
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
}
