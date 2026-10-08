using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Luftpause.LoadGen;

public sealed class DecisionAgent(HttpClient http, AgentShape shape, int calls)
{
    public async Task<IReadOnlyList<TurnSample>> RunAsync(int index, CancellationToken cancel)
    {
        var samples = new List<TurnSample>();
        for (var call = 0; call < calls; call++)
        {
            var body = new JsonObject { ["model"] = shape.Model, ["input"] = $"Agent {index} state {call}: is the task done?" };
            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/decide")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("x-luftpause-class", shape.Service);

            var clock = Stopwatch.StartNew();
            using var response = await http.SendAsync(request, cancel);
            var text = await response.Content.ReadAsStringAsync(cancel);
            var elapsed = clock.Elapsed.TotalMilliseconds;
            if (!response.IsSuccessStatusCode)
                Console.Error.WriteLine($"{(int)response.StatusCode}: {text[..Math.Min(text.Length, 400)]}");
            samples.Add(new TurnSample(false, elapsed, elapsed, (int)response.StatusCode, null, null, 0));
        }
        return samples;
    }
}
