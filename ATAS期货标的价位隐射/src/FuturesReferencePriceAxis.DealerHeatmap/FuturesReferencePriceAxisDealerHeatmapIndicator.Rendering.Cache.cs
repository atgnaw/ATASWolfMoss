namespace WolfMoss.ATAS.PriceMapping;
using WolfMoss.ATAS.PriceMapping.Core;

public sealed partial class FuturesReferencePriceAxisDealerHeatmapIndicator
{
    private readonly DealerRenderMetricsCache _dealerRenderMetricsCache = new();
    private object?[]? _statusSnapshots;
    private object?[]? _cachedStatusSnapshots;
    private DealerColumnVisibility _cachedStatusVisibility = (DealerColumnVisibility)(-1);
    private decimal _cachedStatusUtcOffset = decimal.MinValue;
    private int _cachedStatusHeight, _cachedStatusActiveLines, _cachedStatusBudget;
    private IReadOnlyList<string> _cachedEditionStatusLines = Array.Empty<string>();

    private bool TryGetCachedEditionStatusLines(DealerColumnVisibility visibility, object?[] snapshots,
        string[] performanceLines, int height, int activeLines, out IReadOnlyList<string> lines)
    {
        if (ReferenceEquals(_cachedPerformanceLines, performanceLines)
            && _cachedStatusHeight == height && _cachedStatusActiveLines == activeLines
            && _cachedStatusBudget == _ibOptionMarketDataLineBudget && _cachedStatusVisibility == visibility
            && _cachedStatusUtcOffset == ConfiguredUiUtcOffsetHours
            && _cachedStatusSnapshots is { } previous && previous.Length == snapshots.Length)
        {
            var same = true;
            for (var i = 0; i < previous.Length; i++) if (!ReferenceEquals(previous[i], snapshots[i])) { same = false; break; }
            if (same) { lines = _cachedEditionStatusLines; return true; }
        }
        lines = Array.Empty<string>();
        return false;
    }

    private IReadOnlyList<string> CacheEditionStatusLines(DealerColumnVisibility visibility, object?[] snapshots,
        int height, int activeLines, List<string> lines, string[] performanceLines)
    {
        _cachedStatusVisibility = visibility;
        _cachedStatusSnapshots = (object?[])snapshots.Clone(); // Clone only when data actually changes.
        _cachedStatusHeight = height; _cachedStatusActiveLines = activeLines;
        _cachedStatusBudget = _ibOptionMarketDataLineBudget;
        _cachedPerformanceLines = performanceLines;
        _cachedStatusUtcOffset = ConfiguredUiUtcOffsetHours;
        return _cachedEditionStatusLines = lines.ToArray();
    }
}
