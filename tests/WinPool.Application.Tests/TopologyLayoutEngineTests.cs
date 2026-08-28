namespace WinPool.Application.Tests;

public sealed class TopologyLayoutEngineTests
{
    private const double Wide = 100_000;

    [Fact]
    public void NetworkThreeDisksTightensToOneColumnWhenRowHeightIsSeven()
    {
        var system = System(Primordial(), Pool01(1, 4), Network(3));
        var result = TopologyLayoutEngine.Layout(system, Wide);

        Assert.Equal(new[] { 0, 1, 2 }, Assert.Single(result.Rows));
        var network = result.Children[2];
        Assert.Equal(1, network.UnitWidth);
        Assert.Equal(4, network.UnitHeight);
        Assert.Equal(7, result.Children[0].UnitHeight);
        Assert.Equal(7, result.Children[1].UnitHeight);
    }

    [Fact]
    public void NetworkNineDisksUsesTwoColumnsWhenRowHeightIsSeven()
    {
        var result = TopologyLayoutEngine.Layout(System(Primordial(), Pool01(1, 4), Network(9)), Wide);
        var network = result.Children[2];
        Assert.Equal(2, network.UnitWidth);
        Assert.Equal(6, network.UnitHeight);
        Assert.Equal(2, network.FlowColumns);
    }

    [Fact]
    public void Pool01WithFourCapacityDisksMatchesPrimordialHeightEight()
    {
        var primordial = FlowHeader(Partitioned(6), Partitioned(2), Partitioned(2), Partitioned(2));
        var result = TopologyLayoutEngine.Layout(System(primordial, Pool01(1, 4)), Wide);
        Assert.Equal(8, result.Children[0].UnitHeight);
        Assert.Equal(8, result.Children[1].UnitHeight);
        Assert.Equal(2, result.Children[1].UnitWidth);
    }

    [Fact]
    public void Pool01DoesNotTightenWhenBothTiersHaveFourDisksAndRowHeightIsEight()
    {
        var primordial = FlowHeader(Partitioned(6), Partitioned(2), Partitioned(2), Partitioned(2));
        var result = TopologyLayoutEngine.Layout(System(primordial, Pool01(4, 4)), Wide);
        Assert.Equal(8, result.Children[0].UnitHeight);
        Assert.Equal(7, result.Children[1].UnitHeight);
        Assert.Equal(4, result.Children[1].UnitWidth);
    }

    [Fact]
    public void PacksPoolsToTheNextRowBeforeWrappingInnerDisks()
    {
        var network = Network(3);
        var primordial = Primordial();
        var pool = Pool01(1, 4);
        var together = TopologyLayoutEngine.Layout(System(primordial, pool, network), Wide);
        var threePixel = together.Children.Sum(child => child.PixelWidth)
            + (2 * TopologyLayoutEngine.SiblingSpacing);
        var result = TopologyLayoutEngine.Layout(
            System(primordial, pool, network),
            threePixel - 1);

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(new[] { 0, 1 }, result.Rows[0]);
        Assert.Equal(new[] { 2 }, result.Rows[1]);
        Assert.Equal(4, result.Children[1].Children[2].FlowColumns);
        Assert.Equal(4, result.Children[1].Children[2].Children.Count);
    }

    [Fact]
    public void SinglePoolShrinksInnerDisksWhenTheRowIsStillTooNarrow()
    {
        var pool = FlowHeader(Enumerable.Range(0, 16).Select(_ => Leaf()).ToArray());
        var threeColumns = TopologyLayoutEngine.AncestorChrome
            + (3 * TopologyLayoutEngine.LeafMinWidth)
            + (2 * TopologyLayoutEngine.SiblingSpacing);
        var result = TopologyLayoutEngine.Layout(pool, threeColumns + 8);

        Assert.Equal(3, result.FlowColumns);
        Assert.Equal(3, result.UnitWidth);
        Assert.Equal(7, result.UnitHeight);
        Assert.True(result.PixelWidth <= threeColumns + 8);
    }

    [Fact]
    public void CollapsedNodeIsOneByOne()
    {
        var collapsed = new TopologyLayoutInput(
            true,
            false,
            TopologyChildrenLayout.Flow,
            [Leaf(), Leaf(), Leaf()]);
        var result = TopologyLayoutEngine.Layout(collapsed, Wide);
        Assert.Equal(1, result.UnitWidth);
        Assert.Equal(1, result.UnitHeight);
        Assert.Equal(TopologyLayoutEngine.LeafMinWidth, result.PixelWidth);
        Assert.Empty(result.Children);
    }

    [Fact]
    public void MultipleVirtualDisksShareAHeaderlessFlow()
    {
        var group = new TopologyLayoutInput(
            false,
            true,
            TopologyChildrenLayout.Flow,
            [Partitioned(1), Partitioned(5)]);
        var pool = new TopologyLayoutInput(
            true,
            true,
            TopologyChildrenLayout.Stack,
            [group, FlowHeader(Leaf(), Leaf(), Leaf(), Leaf())]);
        var result = TopologyLayoutEngine.Layout(System(Primordial(), pool), Wide);
        var virtualGroup = result.Children[1].Children[0];
        Assert.Equal(2, virtualGroup.FlowColumns);
        Assert.Equal(6, virtualGroup.UnitHeight);
        Assert.Equal(2, virtualGroup.UnitWidth);
    }

    [Fact]
    public void FlowPixelWidthAddsChromeAndSpacing()
    {
        var result = TopologyLayoutEngine.Layout(FlowHeader(Leaf(), Leaf(), Leaf()), Wide);
        Assert.Equal(
            TopologyLayoutEngine.AncestorChrome
            + (3 * TopologyLayoutEngine.LeafMinWidth)
            + (2 * TopologyLayoutEngine.SiblingSpacing),
            result.PixelWidth);
        Assert.Equal(2, result.UnitHeight);
        Assert.Equal(3, result.UnitWidth);
    }

    private static TopologyLayoutInput System(params TopologyLayoutInput[] children) =>
        new(true, true, TopologyChildrenLayout.WeightedFlow, children);

    private static TopologyLayoutInput Primordial() =>
        FlowHeader(Partitioned(5), Partitioned(2), Partitioned(2), Partitioned(2));

    private static TopologyLayoutInput Pool01(int performanceDisks, int capacityDisks) =>
        new(
            true,
            true,
            TopologyChildrenLayout.Stack,
            [
                new TopologyLayoutInput(true, true, TopologyChildrenLayout.Stack, [Leaf()]),
                FlowHeader(Enumerable.Range(0, performanceDisks).Select(_ => Leaf()).ToArray()),
                FlowHeader(Enumerable.Range(0, capacityDisks).Select(_ => Leaf()).ToArray())
            ]);

    private static TopologyLayoutInput Network(int disks) =>
        FlowHeader(Enumerable.Range(0, disks).Select(_ => Leaf()).ToArray());

    private static TopologyLayoutInput FlowHeader(params TopologyLayoutInput[] children) =>
        new(true, true, TopologyChildrenLayout.Flow, children);

    private static TopologyLayoutInput Partitioned(int partitions) =>
        new(
            true,
            true,
            TopologyChildrenLayout.Stack,
            Enumerable.Range(0, partitions).Select(_ => Leaf()).ToList());

    private static TopologyLayoutInput Leaf() =>
        new(true, true, TopologyChildrenLayout.Stack, []);
}
