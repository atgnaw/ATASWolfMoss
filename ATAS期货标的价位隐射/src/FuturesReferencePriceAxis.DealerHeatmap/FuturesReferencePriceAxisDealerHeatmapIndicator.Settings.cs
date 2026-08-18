namespace WolfMoss.ATAS.PriceMapping;

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

using WolfMoss.ATAS.PriceMapping.Core;

public sealed partial class FuturesReferencePriceAxisDealerHeatmapIndicator
{
    private bool _showDealerHeatmap = true;
    private string _nightwatchApiKey = string.Empty;
    private int _dealerHeatmapRthRefreshMinutes = 5;
    private int _dealerHeatmapOffHoursRefreshMinutes = 60;

    [Display(
        Name = "Show Dealer Heatmap / 显示 Dealer 热力图",
        GroupName = "Dealer Heatmap / Dealer 热力图",
        Order = 200)]
    public bool ShowDealerHeatmap
    {
        get => _showDealerHeatmap;
        set
        {
            if (_showDealerHeatmap == value)
                return;

            _showDealerHeatmap = value;
            ReconfigureDealerHeatmap();
        }
    }

    [Display(
        Name = "Nightwatch API key / Nightwatch API 密钥",
        Description = "Masked in the UI but may be stored as plain text by ATAS / UI 中遮盖，但 ATAS 可能明文保存",
        GroupName = "Dealer Heatmap / Dealer 热力图",
        Order = 210)]
    [PasswordPropertyText(true)]
    public string NightwatchApiKey
    {
        get => _nightwatchApiKey;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;

            if (string.Equals(_nightwatchApiKey, normalized, StringComparison.Ordinal))
                return;

            _nightwatchApiKey = normalized;

            if (IsIndicatorInitialized && normalized.Length > 0)
            {
                SetDealerHeatmapSnapshot(new DealerHeatmapSnapshot(
                    null,
                    null,
                    DealerHeatmapState.Waiting,
                    CurrentUtcTime(),
                    null,
                    "API key 已更新，等待重新连接",
                    false));
            }

            ReconfigureDealerHeatmap();
        }
    }

    [Display(
        Name = "RTH refresh (minutes) / RTH 刷新分钟",
        GroupName = "Dealer Heatmap / Dealer 热力图",
        Order = 220)]
    [Range(5, 60)]
    public int DealerHeatmapRthRefreshMinutes
    {
        get => _dealerHeatmapRthRefreshMinutes;
        set
        {
            var normalized = DealerHeatmapSchedule.NormalizeMinutes(value, 5, 60);

            if (_dealerHeatmapRthRefreshMinutes == normalized)
                return;

            _dealerHeatmapRthRefreshMinutes = normalized;
            ReconfigureDealerHeatmap();
        }
    }

    [Display(
        Name = "Off-hours refresh (minutes) / 休盘刷新分钟",
        GroupName = "Dealer Heatmap / Dealer 热力图",
        Order = 230)]
    [Range(5, 1440)]
    public int DealerHeatmapOffHoursRefreshMinutes
    {
        get => _dealerHeatmapOffHoursRefreshMinutes;
        set
        {
            var normalized = DealerHeatmapSchedule.NormalizeMinutes(value, 5, 1440);

            if (_dealerHeatmapOffHoursRefreshMinutes == normalized)
                return;

            _dealerHeatmapOffHoursRefreshMinutes = normalized;
            ReconfigureDealerHeatmap();
        }
    }

    private void ReconfigureDealerHeatmap()
    {
        Interlocked.Increment(ref _dealerHeatmapGeneration);

        if (!IsIndicatorInitialized)
            return;

        RestartDealerHeatmapSchedule();
        RequestRedraw();
    }
}
