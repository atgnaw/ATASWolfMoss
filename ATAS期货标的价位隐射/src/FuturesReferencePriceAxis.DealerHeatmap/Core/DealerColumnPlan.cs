namespace WolfMoss.ATAS.PriceMapping.Core;

[Flags]
public enum DealerColumnVisibility
{
    None = 0,
    Heatmap = 1 << 0,
    DealerGex = 1 << 1
}

public enum DealerColumnKind
{
    Heatmap,
    DealerGex
}

public readonly record struct DealerColumnPlan(
    int AxisRight,
    int ColumnWidth,
    DealerColumnVisibility Visibility)
{
    public int ColumnCount => DealerColumnPlanner.Count(Visibility);

    public int ReservedWidth => ColumnWidth * ColumnCount;

    public bool TryGetLeft(DealerColumnKind kind, out int left)
    {
        if (!DealerColumnPlanner.TryGetVisibleIndex(Visibility, kind, out var index))
        {
            left = -1;
            return false;
        }

        left = AxisRight + index * ColumnWidth;
        return true;
    }
}

public static class DealerColumnPlanner
{
    private static readonly DealerColumnKind[] OrderedColumns =
    [
        DealerColumnKind.Heatmap,
        DealerColumnKind.DealerGex
    ];

    public static DealerColumnVisibility FromToggles(
        bool showHeatmap,
        bool showDealerGex)
        => (showHeatmap ? DealerColumnVisibility.Heatmap : DealerColumnVisibility.None)
           | (showDealerGex ? DealerColumnVisibility.DealerGex : DealerColumnVisibility.None);

    public static int CalculateColumnWidth(
        int configuredWidth,
        int regionWidth,
        DealerColumnVisibility visibility)
    {
        var dataColumns = Count(visibility);

        if (dataColumns == 0)
            return Math.Min(configuredWidth, Math.Max(45, regionWidth / 3));

        var totalColumns = dataColumns + 1;
        var chartPreservingCap = Math.Max(1, regionWidth / (totalColumns + 2));
        return Math.Max(1, Math.Min(configuredWidth, chartPreservingCap));
    }

    public static DealerColumnPlan Create(
        int axisRight,
        int columnWidth,
        DealerColumnVisibility visibility)
        => new(axisRight, columnWidth, visibility);

    public static int Count(DealerColumnVisibility visibility)
    {
        var count = 0;

        foreach (var kind in OrderedColumns)
        {
            if ((visibility & ToVisibility(kind)) != 0)
                count++;
        }

        return count;
    }

    public static bool TryGetVisibleIndex(
        DealerColumnVisibility visibility,
        DealerColumnKind kind,
        out int index)
    {
        index = 0;

        foreach (var candidate in OrderedColumns)
        {
            if ((visibility & ToVisibility(candidate)) == 0)
                continue;

            if (candidate == kind)
                return true;

            index++;
        }

        index = -1;
        return false;
    }

    private static DealerColumnVisibility ToVisibility(DealerColumnKind kind)
        => kind switch
        {
            DealerColumnKind.Heatmap => DealerColumnVisibility.Heatmap,
            DealerColumnKind.DealerGex => DealerColumnVisibility.DealerGex,
            _ => DealerColumnVisibility.None
        };
}
