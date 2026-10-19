using ModelContextProtocol.Protocol;

namespace Luftpause.Runtime;

public static class ToolApproval
{
    public static bool Needed(string tool, ToolAnnotations? annotations, IReadOnlySet<string>? approveTools) =>
        approveTools?.Contains(tool) == true
        || annotations is { DestructiveHint: true, ReadOnlyHint: not true };
}
