using System.Net;
using System.Text.Json.Nodes;

namespace Luftpause.Runtime.Tests;

public class LlmClientTests
{
    private sealed class Scripted(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private int sent;

        public string Reply { get; init; } = """{"choices":[{"message":{"content":"done"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":1}}""";

        public int Sent => sent;

        public JsonNode? LastBody { get; private set; }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancel));
            var status = statuses[Math.Min(sent++, statuses.Length - 1)];
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(Reply),
            };
            if (status == HttpStatusCode.TooManyRequests)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return response;
        }
    }

    private static (LlmClient Client, Scripted Engine) Client(params HttpStatusCode[] statuses)
    {
        var engine = new Scripted(statuses);
        return (new LlmClient(new HttpClient(engine) { BaseAddress = new Uri("http://dataplane/") }), engine);
    }

    private static readonly RunSpec Spec = new("task", "fake", "interactive", null, 8, null);

    [Fact]
    public async Task A_turn_rejected_with_429_is_sent_again_until_admitted()
    {
        var (client, engine) = Client(HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests, HttpStatusCode.OK);

        var reply = await client.ChatAsync("run-1", Spec, [], [], CancellationToken.None);

        Assert.Equal("done", reply.Content);
        Assert.Equal(3, engine.Sent);
    }

    private static readonly JsonArray Tools = [new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = "lookup" } }];

    [Fact]
    public async Task One_tool_call_per_reply_unless_the_run_allows_parallel_calls()
    {
        var (client, engine) = Client(HttpStatusCode.OK);

        await client.ChatAsync("run-1", Spec, [], Tools, CancellationToken.None);
        Assert.False(engine.LastBody?["parallel_tool_calls"]?.GetValue<bool>());

        await client.ChatAsync("run-1", Spec with { ParallelToolCalls = true }, [], Tools, CancellationToken.None);
        Assert.Null(engine.LastBody?["parallel_tool_calls"]);
    }

    [Fact]
    public async Task No_reply_length_is_sent_unless_the_run_sets_one()
    {
        var (client, engine) = Client(HttpStatusCode.OK);

        await client.ChatAsync("run-1", Spec, [], [], CancellationToken.None);
        Assert.Null(engine.LastBody?["max_tokens"]);

        await client.ChatAsync("run-1", Spec with { MaxTokens = 64 }, [], [], CancellationToken.None);
        Assert.Equal(64, engine.LastBody?["max_tokens"]?.GetValue<int>());
    }

    [Fact]
    public async Task A_reply_carries_the_engines_token_usage_with_cached_prompt_tokens()
    {
        var engine = new Scripted(HttpStatusCode.OK)
        {
            Reply = """{"choices":[{"message":{"content":"done"},"finish_reason":"stop"}],"usage":{"prompt_tokens":120,"completion_tokens":9,"prompt_tokens_details":{"cached_tokens":100}}}""",
        };
        var client = new LlmClient(new HttpClient(engine) { BaseAddress = new Uri("http://dataplane/") });

        var reply = await client.ChatAsync("run-1", Spec, [], [], CancellationToken.None);

        Assert.Equal(new TokenUsage(120, 100, 9), reply.Usage);
    }

    [Fact]
    public async Task A_reply_without_cached_token_details_leaves_cached_tokens_unknown()
    {
        var (client, _) = Client(HttpStatusCode.OK);

        var reply = await client.ChatAsync("run-1", Spec, [], [], CancellationToken.None);

        Assert.Equal(new TokenUsage(3, null, 1), reply.Usage);
    }

    [Fact]
    public async Task A_reply_without_usage_fails_the_turn()
    {
        var engine = new Scripted(HttpStatusCode.OK) { Reply = """{"choices":[{"message":{"content":"done"},"finish_reason":"stop"}]}""" };
        var client = new LlmClient(new HttpClient(engine) { BaseAddress = new Uri("http://dataplane/") });

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ChatAsync("run-1", Spec, [], [], CancellationToken.None));
    }

    [Fact]
    public async Task Ending_a_run_tells_the_data_plane_to_forget_it()
    {
        var (client, engine) = Client(HttpStatusCode.NoContent);

        await client.EndRunAsync("run-1", CancellationToken.None);

        Assert.Equal(HttpMethod.Delete, engine.LastRequest?.Method);
        Assert.Equal("/v1/runs/run-1", engine.LastRequest?.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task Other_failures_are_not_retried()
    {
        var (client, engine) = Client(HttpStatusCode.NotFound);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.ChatAsync("run-1", Spec, [], [], CancellationToken.None));
        Assert.Equal(1, engine.Sent);
    }
}
