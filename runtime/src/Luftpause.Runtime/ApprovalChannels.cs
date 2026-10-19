using ModelContextProtocol.Protocol;

namespace Luftpause.Runtime;

public enum ApprovalChannel { Elicit, InputRequired, Status }

public static class ApprovalChannels
{
    private const string SessionlessRevision = "2026-07-28";

    public static ApprovalChannel Choose(bool inTask, ClientCapabilities? session, ClientCapabilities? request, string? protocolVersion) =>
        inTask || session?.Elicitation is not null ? ApprovalChannel.Elicit
        : session is null && Sessionless(protocolVersion) && request?.Elicitation is not null ? ApprovalChannel.InputRequired
        : ApprovalChannel.Status;

    public static bool Sessionless(string? protocolVersion) =>
        protocolVersion is not null && string.CompareOrdinal(protocolVersion, SessionlessRevision) >= 0;
}
