namespace Luftpause.Runtime.Tests;

public class RunTreeTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static RunRecord Record(string handle, string? parent = null, int startedSeconds = 0, RunWork? work = null, string serviceClass = "interactive") =>
        new(handle, new RunSpec("task", "fake", serviceClass, null, 8), T0.AddSeconds(startedSeconds), new CancellationTokenSource(), parent)
        {
            Work = work ?? RunWork.None,
        };

    private static RunWork Work(int turns, int toolCalls, int failed, long prompt, long completion, double turnSeconds, double toolSeconds) =>
        new(turns, toolCalls, failed, prompt, null, completion, turnSeconds, toolSeconds);

    [Fact]
    public void No_runs_make_an_empty_tree()
    {
        Assert.Empty(RunTree.Build([], T0));
    }

    [Fact]
    public void Children_nest_under_their_parent_in_start_order()
    {
        var tree = RunTree.Build(
            [Record("child-late", "root", 5), Record("root"), Record("child-early", "root", 2), Record("grandchild", "child-late", 6)],
            T0.AddSeconds(10));

        var root = Assert.Single(tree);
        Assert.Equal("root", root.Handle);
        Assert.Equal(["child-early", "child-late"], root.Children.Select(c => c.Handle));
        Assert.Equal("grandchild", Assert.Single(root.Children[1].Children).Handle);
        Assert.Equal("child-late", root.Children[1].Children[0].Parent);
    }

    [Fact]
    public void A_node_shows_its_status_class_and_elapsed_time()
    {
        var finished = Record("done", startedSeconds: 1, serviceClass: "batch");
        finished.Completion = Task.FromResult(new RunOutcome(RunStatus.Completed, "ok", null));
        finished.Finished = T0.AddSeconds(4);

        var node = Assert.Single(RunTree.Build([finished], T0.AddSeconds(100)));

        Assert.Equal(RunStatus.Completed, node.Status);
        Assert.Equal("batch", node.Class);
        Assert.Null(node.Parent);
        Assert.Equal(3.0, node.ElapsedSeconds);
    }

    [Fact]
    public void Subtree_totals_add_every_descendants_own_work()
    {
        var tree = RunTree.Build(
            [
                Record("root", work: Work(1, 2, 0, 100, 10, 1.0, 2.0)),
                Record("a", "root", 1, Work(2, 1, 1, 200, 20, 0.5, 4.0)),
                Record("b", "root", 2, Work(3, 0, 0, 300, 30, 1.5, 0)),
                Record("a1", "a", 3, Work(1, 4, 2, 50, 5, 0.25, 1.0)),
            ],
            T0.AddSeconds(10));

        var root = Assert.Single(tree);
        Assert.Equal(new WorkTotals(1, 2, 0, 100, 10, 1.0, 2.0), root.Own);
        Assert.Equal(new WorkTotals(7, 7, 3, 650, 65, 3.25, 7.0), root.Subtree);
        Assert.Equal(new WorkTotals(3, 5, 3, 250, 25, 0.75, 5.0), root.Children[0].Subtree);
        Assert.Equal(root.Children[1].Own, root.Children[1].Subtree);
    }
}
