using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Luftpause.Runtime.Tests;

public class BearerAuthTests
{
    private static async Task<(WebApplication App, HttpClient Http)> ServeAsync(string? token)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.UseBearerAuth(token, NullLogger.Instance);
        app.MapGet("/status", () => "status");
        app.MapPost("/mcp", () => "mcp");
        await app.StartAsync();
        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.Single()) });
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? authorization)
    {
        var request = new HttpRequestMessage(method, path);
        if (authorization is not null)
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return request;
    }

    [Theory]
    [InlineData("GET", "/status")]
    [InlineData("POST", "/mcp")]
    public async Task A_request_without_the_token_is_a_401_with_a_json_error(string method, string path)
    {
        var (app, http) = await ServeAsync("s3cret");
        await using var _ = app;

        using var response = await http.SendAsync(Request(new HttpMethod(method), path, null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("missing or invalid bearer token", body?["error"]?["message"]?.GetValue<string>());
    }

    [Theory]
    [InlineData("Bearer s3crex")]
    [InlineData("Bearer s3cret2")]
    [InlineData("s3cret")]
    [InlineData("Basic s3cret")]
    public async Task A_request_with_a_wrong_token_is_a_401(string authorization)
    {
        var (app, http) = await ServeAsync("s3cret");
        await using var _ = app;

        using var response = await http.SendAsync(Request(HttpMethod.Get, "/status", authorization));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/status", "status")]
    [InlineData("POST", "/mcp", "mcp")]
    public async Task A_request_with_the_token_is_served(string method, string path, string expected)
    {
        var (app, http) = await ServeAsync("s3cret");
        await using var _ = app;

        using var response = await http.SendAsync(Request(new HttpMethod(method), path, "Bearer s3cret"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Without_a_token_everything_is_served()
    {
        var (app, http) = await ServeAsync(null);
        await using var _ = app;

        using var response = await http.SendAsync(Request(HttpMethod.Get, "/status", null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("s3cret", "s3cret")]
    public void The_token_comes_from_LUFTPAUSE_TOKEN_and_empty_means_unset(string? value, string? expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["LUFTPAUSE_TOKEN"] = value })
            .Build();

        Assert.Equal(expected, BearerAuth.Token(configuration));
    }
}
