namespace WolfMoss.ATAS.PriceMapping;

using System.ComponentModel.DataAnnotations;
using WolfMoss.ATAS.PriceMapping.Core;

public sealed partial class FuturesReferencePriceAxisDealerHeatmapIndicator
{
    private const string StatusSettingsGroup = "Status UI / 状态信息";
    private bool _showMappingStatus = true, _showHeatmapStatus = true, _showDealerGexStatus = true;
    private bool _showOptionOiStatus = true, _showOptionFlowStatus = true, _showPerformanceStatus = true;
    private int _statusPanelWidth = 480;

    [Display(Name = "Mapping information / 映射轴信息", GroupName = StatusSettingsGroup, Order = 600)]
    public bool ShowMappingStatus { get => _showMappingStatus; set => SetStatusVisibility(ref _showMappingStatus, value); }
    [Display(Name = "Heatmap information / Heatmap 信息", GroupName = StatusSettingsGroup, Order = 610)]
    public bool ShowHeatmapStatus { get => _showHeatmapStatus; set => SetStatusVisibility(ref _showHeatmapStatus, value); }
    [Display(Name = "Dealer GEX information / Dealer GEX 信息", GroupName = StatusSettingsGroup, Order = 620)]
    public bool ShowDealerGexStatus { get => _showDealerGexStatus; set => SetStatusVisibility(ref _showDealerGexStatus, value); }
    [Display(Name = "Option OI information / Option OI 信息", GroupName = StatusSettingsGroup, Order = 630)]
    public bool ShowOptionOiStatus { get => _showOptionOiStatus; set => SetStatusVisibility(ref _showOptionOiStatus, value); }
    [Display(Name = "Option Flow information / Option Flow 信息", GroupName = StatusSettingsGroup, Order = 640)]
    public bool ShowOptionFlowStatus { get => _showOptionFlowStatus; set => SetStatusVisibility(ref _showOptionFlowStatus, value); }
    [Display(Name = "Performance information / 性能监控信息", Description = "Only hides text; does not stop diagnostics or Record files / 只隐藏文字，不停止诊断或文件记录", GroupName = StatusSettingsGroup, Order = 650)]
    public bool ShowPerformanceStatus { get => _showPerformanceStatus; set => SetStatusVisibility(ref _showPerformanceStatus, value); }

    [Display(Name = "Status width / 状态面板宽度", Description = "Pixels; constrained to chart width / 像素，受图表可用宽度限制", GroupName = StatusSettingsGroup, Order = 660)]
    [Range(180, 1600)]
    public int StatusPanelWidth
    {
        get => _statusPanelWidth;
        set { var width = Math.Clamp(value, 180, 1600); if (width == _statusPanelWidth) return; _statusPanelWidth = width; RequestRedraw(); }
    }

    private void SetStatusVisibility(ref bool field, bool value)
    {
        if (field == value) return;
        field = value;
        _cachedStatusVisibility = (DealerColumnVisibility)(-1);
        RequestRedraw(); // Presentation only: no schedule, lease or aggregation changes.
    }

    private DealerColumnVisibility StatusColumnVisibility =>
        (_showHeatmapStatus ? DealerColumnVisibility.Heatmap : 0)
        | (_showDealerGexStatus ? DealerColumnVisibility.DealerGex : 0)
        | (_showOptionOiStatus ? DealerColumnVisibility.OptionOpenInterest : 0)
        | (_showOptionFlowStatus ? DealerColumnVisibility.OptionPremiumFlow : 0);
}
