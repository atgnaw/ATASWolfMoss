namespace WolfMoss.ATAS.PriceMapping.Core;

public readonly record struct DealerColumns(
    int ColumnWidth,
    int HeatmapLeft,
    int DealerGexLeft,
    int DataColumnCount)
{
    public int ReservedEditionWidth => ColumnWidth * DataColumnCount;
}

public static class DealerColumnsLayout
{
    public static int CalculateColumnWidth(
        int configuredWidth,
        int regionWidth,
        bool showHeatmap,
        bool showDealerGex)
    {
        var dataColumns = (showHeatmap ? 1 : 0) + (showDealerGex ? 1 : 0);

        if (dataColumns == 0)
            return Math.Min(configuredWidth, Math.Max(45, regionWidth / 3));

        var totalColumns = dataColumns + 1;
        var chartPreservingCap = Math.Max(1, regionWidth / (totalColumns + 2));
        return Math.Max(1, Math.Min(configuredWidth, chartPreservingCap));
    }

    public static DealerColumns Calculate(
        int axisRight,
        int columnWidth,
        bool showHeatmap,
        bool showDealerGex)
    {
        var nextLeft = axisRight;
        var heatmapLeft = -1;
        var dealerGexLeft = -1;

        if (showHeatmap)
        {
            heatmapLeft = nextLeft;
            nextLeft += columnWidth;
        }

        if (showDealerGex)
            dealerGexLeft = nextLeft;

        return new DealerColumns(
            columnWidth,
            heatmapLeft,
            dealerGexLeft,
            (showHeatmap ? 1 : 0) + (showDealerGex ? 1 : 0));
    }
}
