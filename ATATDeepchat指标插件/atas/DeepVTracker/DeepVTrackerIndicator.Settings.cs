using System.ComponentModel.DataAnnotations;
using MediaColor = System.Windows.Media.Color;

namespace WolfMoss.ATAS.DeepVTracker;

public enum RangeRuleMode { [Display(Name = "自动识别")] Auto, [Display(Name = "Range 图")] Range, [Display(Name = "其他图表")] Other }

public sealed partial class DeepVTrackerIndicator
{
    private int _historyBars = 3000, _rangeTicks = 40;
    private RangeRuleMode _rangeMode;
    private MediaColor _askColor = MediaColor.FromRgb(50, 205, 50), _bidColor = MediaColor.FromRgb(255, 82, 82);
    private MediaColor _exhaustionColor = MediaColor.FromRgb(0, 255, 255), _slowdownColor = MediaColor.FromRgb(255, 215, 0);
    private MediaColor _accelerationColor = MediaColor.FromRgb(105, 105, 105);
    private int _accelerationAlpha = 45, _controlWidth = 2, _extremeWidth = 1, _iconSize = 10, _fontSize = 12;
    private bool _showLabels = true, _showStatus = true;

    [Display(Name = "成交加速", GroupName = "功能", Order = 10)]
    public bool EnableAcceleration { get => _config.EnableAcceleration; set => Change(c => c with { EnableAcceleration = value }); }
    [Display(Name = "双路衰竭", GroupName = "功能", Order = 20)]
    public bool EnableExhaustion { get => _config.EnableExhaustion; set => Change(c => c with { EnableExhaustion = value }); }
    [Display(Name = "减速标记", GroupName = "功能", Order = 30)]
    public bool EnableSlowdown { get => _config.EnableSlowdown; set => Change(c => c with { EnableSlowdown = value }); }
    [Display(Name = "吸收 / 压力线", GroupName = "功能", Order = 40)]
    public bool EnableLevels { get => _config.EnableLevels; set => Change(c => c with { EnableLevels = value }); }
    [Display(Name = "加速强度", GroupName = "计算", Order = 50)]
    public SignalStrength AccelerationStrength { get => _config.Acceleration; set => Change(c => c with { Acceleration = Valid(value) }); }
    [Display(Name = "衰竭强度", GroupName = "计算", Order = 60)]
    public SignalStrength ExhaustionStrength { get => _config.Exhaustion; set => Change(c => c with { Exhaustion = Valid(value) }); }
    [Display(Name = "减速强度", GroupName = "计算", Order = 70)]
    public SignalStrength SlowdownStrength { get => _config.Slowdown; set => Change(c => c with { Slowdown = Valid(value) }); }
    [Display(Name = "吸收强度", GroupName = "计算", Order = 80)]
    public SignalStrength AbsorptionStrength { get => _config.Absorption; set => Change(c => c with { Absorption = Valid(value) }); }
    [Display(Name = "水平线位置", GroupName = "计算", Order = 90)]
    public LevelPosition LevelMode { get => _config.LevelMode; set => Change(c => c with { LevelMode = Valid(value) }); }
    [Display(Name = "水平线延伸柱跨度", GroupName = "计算", Order = 100)]
    [Range(1, 1024)]
    public int ProjectionBars { get => _config.ProjectionBars; set => Change(c => c with { ProjectionBars = Math.Clamp(value, 1, 1024) }); }
    [Display(Name = "历史回看柱数", GroupName = "数据", Order = 110, Description = "默认请求最近 3000 根已收柱的原始逐笔。更改起点会改变预热统计。")]
    [Range(50, 100000)]
    public int HistoryBars { get => _historyBars; set { lock (_gate) _historyBars = Math.Clamp(value, 50, 100000); RecalculateValues(); } }
    [Display(Name = "区间规则", GroupName = "数据", Order = 120, Description = "自动模式仅将 ChartType=Range 识别为 Range；Range X/XV/Z 等使用其他图表规则。")]
    public RangeRuleMode RangeRules { get => _rangeMode; set { lock (_gate) _rangeMode = Valid(value); RecalculateValues(); } }
    [Display(Name = "手动 Range 大小 (tick)", GroupName = "数据", Order = 130, Description = "仅在区间规则手动选择 Range 时使用，必须与图表相同。")]
    [Range(1, 1000000)]
    public int RangeTicks { get => _rangeTicks; set { lock (_gate) _rangeTicks = Math.Clamp(value, 1, 1000000); RecalculateValues(); } }
    [Display(Name = "Ask 线色", GroupName = "外观", Order = 140)]
    public MediaColor AskColor { get => _askColor; set { lock (_gate) _askColor = value; Restyle(); } }
    [Display(Name = "Bid 线色", GroupName = "外观", Order = 150)]
    public MediaColor BidColor { get => _bidColor; set { lock (_gate) _bidColor = value; Restyle(); } }
    [Display(Name = "衰竭颜色", GroupName = "外观", Order = 160)]
    public MediaColor ExhaustionColor { get => _exhaustionColor; set { lock (_gate) _exhaustionColor = value; Restyle(); } }
    [Display(Name = "减速颜色", GroupName = "外观", Order = 170)]
    public MediaColor SlowdownColor { get => _slowdownColor; set { lock (_gate) _slowdownColor = value; Restyle(); } }
    [Display(Name = "加速区域颜色", GroupName = "外观", Order = 180)]
    public MediaColor AccelerationColor { get => _accelerationColor; set { lock (_gate) _accelerationColor = value; Restyle(); } }
    [Display(Name = "加速填充不透明度", GroupName = "外观", Order = 190)]
    [Range(0, 255)]
    public int AccelerationAlpha { get => _accelerationAlpha; set { lock (_gate) _accelerationAlpha = Math.Clamp(value, 0, 255); Restyle(); } }
    [Display(Name = "控制线宽度", GroupName = "外观", Order = 200)]
    [Range(0, 20)]
    public int ControlWidth { get => _controlWidth; set { lock (_gate) _controlWidth = Math.Clamp(value, 0, 20); Restyle(); } }
    [Display(Name = "极值线宽度", GroupName = "外观", Order = 210)]
    [Range(0, 20)]
    public int ExtremeWidth { get => _extremeWidth; set { lock (_gate) _extremeWidth = Math.Clamp(value, 0, 20); Restyle(); } }
    [Display(Name = "三角标记大小", GroupName = "外观", Order = 220)]
    [Range(4, 40)]
    public int IconSize { get => _iconSize; set { lock (_gate) _iconSize = Math.Clamp(value, 4, 40); Restyle(); } }
    [Display(Name = "标签字号", GroupName = "外观", Order = 230)]
    [Range(8, 40)]
    public int FontSize { get => _fontSize; set { lock (_gate) _fontSize = Math.Clamp(value, 8, 40); Restyle(); } }
    [Display(Name = "显示 AC/AE/PC/PE", GroupName = "外观", Order = 240)]
    public bool ShowLabels { get => _showLabels; set { lock (_gate) _showLabels = value; Restyle(); } }
    [Display(Name = "显示数据状态", GroupName = "外观", Order = 250)]
    public bool ShowStatus { get => _showStatus; set { lock (_gate) _showStatus = value; Restyle(); } }
    private static T Valid<T>(T value) where T : struct, Enum => Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value));
    private void Change(Func<TrackerConfig, TrackerConfig> change)
    {
        lock (_gate) { var next = change(_config); if (next == _config) return; _config = next; }
        RecalculateValues(); RedrawChart();
    }
    private void Restyle() { lock (_gate) { _pens.Clear(); _labelFont = null; } RedrawChart(); }
}
