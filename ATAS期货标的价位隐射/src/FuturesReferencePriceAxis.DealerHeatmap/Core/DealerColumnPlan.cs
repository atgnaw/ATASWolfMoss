namespace WolfMoss.ATAS.PriceMapping.Core;

[Flags]
public enum DealerColumnVisibility
{
    None = 0,
    Heatmap = 1 << 0,
    DealerGex = 1 << 1,
    OptionOpenInterest = 1 << 2,
    OptionPremiumFlow = 1 << 3
}

public enum DealerColumnKind
{
    Heatmap,
    DealerGex,
    OptionOpenInterest,
    OptionPremiumFlow
}

public readonly record struct DealerColumnPlan(
    int AxisRight,
    int ColumnWidth,
    DealerColumnVisibility Visibility)
{
    public DealerColumnCatalog Catalog { get; init; } = DealerColumnCatalog.Default;
    public int ColumnCount => (Catalog ?? DealerColumnCatalog.Default).Count(Visibility);

    public int ReservedWidth => ColumnWidth * ColumnCount;

    public bool TryGetLeft(DealerColumnKind kind, out int left)
    {
        if (!(Catalog ?? DealerColumnCatalog.Default).TryGetVisibleIndex(Visibility, kind, out var index))
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

    public static DealerColumnVisibility FromToggles(
        bool showHeatmap,
        bool showDealerGex,
        bool showOptionOpenInterest = false,
        bool showOptionPremiumFlow = false)
        => (showHeatmap ? DealerColumnVisibility.Heatmap : DealerColumnVisibility.None)
           | (showDealerGex ? DealerColumnVisibility.DealerGex : DealerColumnVisibility.None)
           | (showOptionOpenInterest
               ? DealerColumnVisibility.OptionOpenInterest
               : DealerColumnVisibility.None)
           | (showOptionPremiumFlow
               ? DealerColumnVisibility.OptionPremiumFlow
               : DealerColumnVisibility.None);

    public static int CalculateColumnWidth(
        int configuredWidth,
        int regionWidth,
        DealerColumnVisibility visibility,
        DealerColumnCatalog? catalog = null)
    {
        var dataColumns = (catalog ?? DealerColumnCatalog.Default).Count(visibility);

        if (dataColumns == 0)
            return Math.Min(configuredWidth, Math.Max(45, regionWidth / 3));

        var totalColumns = dataColumns + 1;
        var chartPreservingCap = Math.Max(1, regionWidth / (totalColumns + 2));
        return Math.Max(1, Math.Min(configuredWidth, chartPreservingCap));
    }

    public static DealerColumnPlan Create(
        int axisRight,
        int columnWidth,
        DealerColumnVisibility visibility,
        DealerColumnCatalog? catalog = null)
        => new(axisRight, columnWidth, visibility) { Catalog = catalog ?? DealerColumnCatalog.Default };

    public static int Count(DealerColumnVisibility visibility)
        => DealerColumnCatalog.Default.Count(visibility);

    public static bool TryGetVisibleIndex(
        DealerColumnVisibility visibility,
        DealerColumnKind kind,
        out int index)
        => DealerColumnCatalog.Default.TryGetVisibleIndex(visibility, kind, out index);
}
