using ModelContextProtocol.Protocol;

namespace Luftpause.Runtime.Tests;

public sealed class ToolApprovalTests
{
    private static readonly IReadOnlySet<string> Listed = new HashSet<string>(StringComparer.Ordinal) { "write" };

    [Fact]
    public void A_tool_without_annotations_or_listing_runs_without_approval() =>
        Assert.False(ToolApproval.Needed("read", null, null));

    [Fact]
    public void A_tool_marked_destructive_needs_approval() =>
        Assert.True(ToolApproval.Needed("drop", new ToolAnnotations { DestructiveHint = true }, null));

    [Fact]
    public void A_destructive_hint_is_overruled_by_read_only() =>
        Assert.False(ToolApproval.Needed("drop", new ToolAnnotations { DestructiveHint = true, ReadOnlyHint = true }, null));

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void Only_an_explicit_destructive_hint_counts(bool? destructive) =>
        Assert.False(ToolApproval.Needed("drop", new ToolAnnotations { DestructiveHint = destructive }, null));

    [Fact]
    public void A_tool_named_in_approve_tools_needs_approval() =>
        Assert.True(ToolApproval.Needed("write", null, Listed));

    [Fact]
    public void A_listed_tool_needs_approval_even_when_read_only() =>
        Assert.True(ToolApproval.Needed("write", new ToolAnnotations { ReadOnlyHint = true }, Listed));

    [Fact]
    public void A_tool_not_in_approve_tools_falls_back_to_its_annotations() =>
        Assert.False(ToolApproval.Needed("read", new ToolAnnotations { DestructiveHint = false }, Listed));
}
