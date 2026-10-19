using ModelContextProtocol.Protocol;

namespace Luftpause.Runtime.Tests;

public sealed class ApprovalChannelTests
{
    private static readonly ClientCapabilities Eliciting = new() { Elicitation = new ElicitationCapability() };
    private static readonly ClientCapabilities Plain = new();
    private const string Stateful = "2025-11-25";
    private const string Sessionless = "2026-07-28";

    [Fact]
    public void A_session_that_advertised_elicitation_is_asked_directly() =>
        Assert.Equal(ApprovalChannel.Elicit, ApprovalChannels.Choose(false, Eliciting, null, Stateful));

    [Fact]
    public void A_session_without_elicitation_reads_the_status() =>
        Assert.Equal(ApprovalChannel.Status, ApprovalChannels.Choose(false, Plain, null, Stateful));

    [Fact]
    public void A_sessionless_request_declaring_elicitation_gets_an_input_required_result() =>
        Assert.Equal(ApprovalChannel.InputRequired, ApprovalChannels.Choose(false, null, Eliciting, Sessionless));

    [Fact]
    public void A_sessionless_request_without_elicitation_reads_the_status() =>
        Assert.Equal(ApprovalChannel.Status, ApprovalChannels.Choose(false, null, Plain, Sessionless));

    [Fact]
    public void A_sessionless_request_on_an_older_revision_reads_the_status() =>
        Assert.Equal(ApprovalChannel.Status, ApprovalChannels.Choose(false, null, Eliciting, Stateful));

    [Fact]
    public void Inside_a_task_the_tool_elicits_and_the_task_carries_it_as_an_input_request() =>
        Assert.Equal(ApprovalChannel.Elicit, ApprovalChannels.Choose(true, null, Plain, Sessionless));
}
