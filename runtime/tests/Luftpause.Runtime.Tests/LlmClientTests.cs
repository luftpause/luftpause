using System.Net;
using System.Text.Json.Nodes;

namespace Luftpause.Runtime.Tests;

public class LlmClientTests
{
    private sealed class Scripted(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private int sent;

        public int Sent => sent;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var status = statuses[Math.Min(sent++, statuses.Length - 1)];
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"done"},"finish_reason":"stop"}]}"""),
            };
            if (status == HttpStatusCode.TooManyRequests)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return Task.FromResult(response);
        }
    }

    private static (LlmClient Client, Scripted Engine) Client(params HttpStatusCode[] statuses)
    {
        var engine = new Scripted(statuses);
        return (new LlmClient(new HttpClient(engine) { BaseAddress = new Uri("http://dataplane/") }, new EnginePriority(false)), engine);
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

    [Fact]
    public async Task Other_failures_are_not_retried()
    {
        var (client, engine) = Client(HttpStatusCode.NotFound);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.ChatAsync("run-1", Spec, [], [], CancellationToken.None));
        Assert.Equal(1, engine.Sent);
    }
}
