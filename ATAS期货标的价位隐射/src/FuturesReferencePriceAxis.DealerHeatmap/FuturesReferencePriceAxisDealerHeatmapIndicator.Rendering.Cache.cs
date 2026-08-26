namespace WolfMoss.ATAS.PriceMapping;

using WolfMoss.ATAS.PriceMapping.Core;

public sealed partial class FuturesReferencePriceAxisDealerHeatmapIndicator
{
    private readonly DealerRenderMetricsCache _dealerRenderMetricsCache = new();
    private DealerHeatmapSnapshot? _cachedStatusHeatmapSnapshot;
    private DealerGexSnapshot? _cachedStatusDealerGexSnapshot;
    private DealerColumnVisibility _cachedStatusVisibility = (DealerColumnVisibility)(-1);
    private decimal _cachedStatusUtcOffset = decimal.MinValue;
    private IReadOnlyList<string> _cachedEditionStatusLines = Array.Empty<string>();

    private DealerColumnVisibility VisibleDealerColumns
        => DealerColumnPlanner.FromToggles(_showDealerHeatmap, _showDealerGex);

    private bool TryGetCachedEditionStatusLines(
        DealerColumnVisibility visibility,
        DealerHeatmapSnapshot? heatmapSnapshot,
        DealerGexSnapshot? dealerGexSnapshot,
        out IReadOnlyList<string> lines)
    {
        if (_cachedStatusVisibility == visibility
            && _cachedStatusUtcOffset == ConfiguredUiUtcOffsetHours
            && ReferenceEquals(_cachedStatusHeatmapSnapshot, heatmapSnapshot)
            && ReferenceEquals(_cachedStatusDealerGexSnapshot, dealerGexSnapshot))
        {
            lines = _cachedEditionStatusLines;
            return true;
        }

        lines = Array.Empty<string>();
        return false;
    }

    private IReadOnlyList<string> CacheEditionStatusLines(
        DealerColumnVisibility visibility,
        DealerHeatmapSnapshot? heatmapSnapshot,
        DealerGexSnapshot? dealerGexSnapshot,
        List<string> lines)
    {
        _cachedStatusVisibility = visibility;
        _cachedStatusUtcOffset = ConfiguredUiUtcOffsetHours;
        _cachedStatusHeatmapSnapshot = heatmapSnapshot;
        _cachedStatusDealerGexSnapshot = dealerGexSnapshot;
        _cachedEditionStatusLines = lines.ToArray();
        return _cachedEditionStatusLines;
    }
}
