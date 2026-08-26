namespace WolfMoss.ATAS.PriceMapping.Core;

public readonly record struct DealerHeatmapValueRange(
    decimal Minimum,
    decimal Maximum);

public sealed class DealerRenderMetricsCache
{
    private DealerHeatmapFrame? _heatmapFrame;
    private DealerHeatmapValueRange _heatmapRange;
    private DealerGexFrame? _dealerGexFrame;
    private decimal _dealerGexMaximumAbsolute;

    public int HeatmapCalculationCount { get; private set; }

    public int DealerGexCalculationCount { get; private set; }

    public DealerHeatmapValueRange GetHeatmapRange(DealerHeatmapFrame frame)
    {
        if (ReferenceEquals(_heatmapFrame, frame))
            return _heatmapRange;

        var minimum = 0m;
        var maximum = 0m;

        foreach (var cell in frame.Cells)
        {
            minimum = Math.Min(minimum, cell.NetDealerGexUsd);
            maximum = Math.Max(maximum, cell.NetDealerGexUsd);
        }

        _heatmapFrame = frame;
        _heatmapRange = new DealerHeatmapValueRange(minimum, maximum);
        HeatmapCalculationCount++;
        return _heatmapRange;
    }

    public decimal GetDealerGexMaximumAbsolute(DealerGexFrame frame)
    {
        if (ReferenceEquals(_dealerGexFrame, frame))
            return _dealerGexMaximumAbsolute;

        var maximumAbsolute = 0m;

        foreach (var node in frame.Nodes)
            maximumAbsolute = Math.Max(maximumAbsolute, Math.Abs(node.NetGexUsd));

        _dealerGexFrame = frame;
        _dealerGexMaximumAbsolute = maximumAbsolute;
        DealerGexCalculationCount++;
        return maximumAbsolute;
    }
}
