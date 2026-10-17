using System.Diagnostics;

namespace Luftpause.Scheduling.Tests;

public class AdmitTraceTests
{
    private static readonly TurnRequested Turn = new("t1", "r1", EngineClass.Generative, "llama", false, 0, ServiceClass.Batch);

    private static Dictionary<string, object?> Map(IEnumerable<KeyValuePair<string, object?>> tags) => tags.ToDictionary();

    [Fact]
    public void A_traceparent_becomes_the_remote_parent()
    {
        var parent = AdmitTrace.Parent("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");

        Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", parent.TraceId.ToHexString());
        Assert.Equal("00f067aa0ba902b7", parent.SpanId.ToHexString());
        Assert.True(parent.IsRemote);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00-nonsense-01")]
    public void Without_a_valid_traceparent_there_is_no_parent(string? traceparent)
    {
        Assert.Equal(default(ActivityContext), AdmitTrace.Parent(traceparent));
    }

    [Fact]
    public void A_requested_turn_is_tagged_with_its_ids_model_and_class()
    {
        var tags = Map(AdmitTrace.Requested(Turn));

        Assert.Equal("t1", tags["luftpause.turn_id"]);
        Assert.Equal("r1", tags["luftpause.run_id"]);
        Assert.Equal("llama", tags["gen_ai.request.model"]);
        Assert.Equal("Batch", tags["luftpause.service_class"]);
    }

    [Fact]
    public void A_routed_turn_names_its_replica_and_how_long_it_was_held()
    {
        var route = new RouteTurn("t1", new Replica("a", "http://a", EngineClass.Generative));

        var tags = Map(AdmitTrace.Admitted(route, TimeSpan.FromMilliseconds(1234.7)));

        Assert.Equal("a", tags["luftpause.replica"]);
        Assert.Equal(1234L, tags["luftpause.held_ms"]);
    }

    [Fact]
    public void A_refused_turn_carries_the_reason_instead_of_a_replica()
    {
        var tags = Map(AdmitTrace.Admitted(new RefuseTurn("t1", "no replica serves llama"), TimeSpan.Zero));

        Assert.Equal("no replica serves llama", tags["luftpause.refused"]);
        Assert.False(tags.ContainsKey("luftpause.replica"));
        Assert.Equal(0L, tags["luftpause.held_ms"]);
    }
}
