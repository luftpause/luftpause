using System.Net;
using System.Text.Json.Nodes;

namespace Luftpause.Runtime.Tests;

public class LabelDecisionTests
{
    private sealed class Engine(string reply) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, JsonNode? Body)> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Sent.Add((request, request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancel))));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply) };
        }
    }

    private const string Reply = """
        {"choices":[{"message":{"content":"Yes"},"finish_reason":"length",
          "logprobs":{"content":[{"token":"Yes","logprob":-0.1,"top_logprobs":[
            {"token":"Yes","logprob":-0.1},{"token":" no","logprob":-2.4},{"token":"Maybe","logprob":-5.0}]}]}}],
         "usage":{"prompt_tokens":30,"completion_tokens":1}}
        """;

    private static (LlmClient Client, Engine Engine) Client(string reply = Reply)
    {
        var engine = new Engine(reply);
        return (new LlmClient(new HttpClient(engine) { BaseAddress = new Uri("http://dataplane/") }), engine);
    }

    [Fact]
    public async Task One_token_with_its_alternatives_is_asked_of_the_generative_model()
    {
        var (client, engine) = Client();

        await LabelDecision.DecideAsync(client, "llama", "Is the sky blue?", [" yes ", "no", "unsure"], CancellationToken.None);

        var (request, body) = engine.Sent[0];
        Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
        Assert.Equal(["interactive"], request.Headers.GetValues("x-luftpause-class"));
        Assert.StartsWith("run-", request.Headers.GetValues("x-luftpause-run-id").Single());
        Assert.Equal("llama", body!["model"]!.GetValue<string>());
        Assert.Equal(1, body["max_tokens"]!.GetValue<int>());
        Assert.True(body["logprobs"]!.GetValue<bool>());
        Assert.Equal(20, body["top_logprobs"]!.GetValue<int>());
        Assert.Equal("system", body["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("Answer with exactly one of the labels, nothing else.", body["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal("user", body["messages"]![1]!["role"]!.GetValue<string>());
        Assert.Equal("Is the sky blue?\n\nLabels: yes, no, unsure\nAnswer:", body["messages"]![1]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Each_decision_is_its_own_run_and_ends_it()
    {
        var (client, engine) = Client();

        await LabelDecision.DecideAsync(client, "llama", "x", ["yes", "no"], CancellationToken.None);
        await LabelDecision.DecideAsync(client, "llama", "x", ["yes", "no"], CancellationToken.None);

        var runIds = engine.Sent.Where(s => s.Request.Method == HttpMethod.Post)
            .Select(s => s.Request.Headers.GetValues("x-luftpause-run-id").Single()).ToList();
        Assert.Equal(2, runIds.Distinct().Count());
        Assert.Equal(runIds.Select(id => $"/v1/runs/{id}"),
            engine.Sent.Where(s => s.Request.Method == HttpMethod.Delete).Select(s => s.Request.RequestUri!.AbsolutePath));
    }

    [Fact]
    public async Task The_result_names_the_best_label_its_probabilities_and_the_unscored_ones()
    {
        var (client, _) = Client();

        var json = JsonNode.Parse(await LabelDecision.DecideAsync(client, "llama", "x", ["yes", "no", "unsure"], CancellationToken.None))!;

        Assert.Equal("yes", json["label"]!.GetValue<string>());
        var probabilities = json["probabilities"]!.AsObject();
        Assert.Equal(["yes", "no", "unsure"], probabilities.Select(p => p.Key));
        Assert.Equal(Math.Exp(-0.1) / (Math.Exp(-0.1) + Math.Exp(-2.4)), probabilities["yes"]!.GetValue<double>(), 12);
        Assert.Equal(0, probabilities["unsure"]!.GetValue<double>());
        Assert.Equal(["unsure"], json["unscored"]!.AsArray().Select(l => l!.GetValue<string>()));
    }

    [Fact]
    public async Task A_reply_where_no_label_is_a_candidate_fails()
    {
        var (client, _) = Client();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => LabelDecision.DecideAsync(client, "llama", "x", ["red", "green"], CancellationToken.None));
    }

    [Fact]
    public async Task A_reply_without_logprobs_fails()
    {
        var (client, _) = Client("""{"choices":[{"message":{"content":"Yes"}}],"usage":{"prompt_tokens":3,"completion_tokens":1}}""");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => LabelDecision.DecideAsync(client, "llama", "x", ["yes", "no"], CancellationToken.None));
    }

    [Theory]
    [InlineData("only")]
    [InlineData("yes|  ")]
    [InlineData("Very good|very bad")]
    [InlineData("same|same")]
    public async Task Labels_that_cannot_be_scored_apart_are_rejected_before_any_request(string joined)
    {
        var labels = joined.Split('|');
        var (client, engine) = Client();

        await Assert.ThrowsAsync<ArgumentException>(
            () => LabelDecision.DecideAsync(client, "llama", "x", labels, CancellationToken.None));
        Assert.Empty(engine.Sent);
    }

    [Fact]
    public async Task More_than_twenty_labels_are_rejected()
    {
        var (client, _) = Client();
        var labels = Enumerable.Range(0, 21).Select(i => $"label{i}").ToArray();

        await Assert.ThrowsAsync<ArgumentException>(
            () => LabelDecision.DecideAsync(client, "llama", "x", labels, CancellationToken.None));
    }
}
