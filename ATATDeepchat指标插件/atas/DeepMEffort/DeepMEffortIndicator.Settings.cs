using System.ComponentModel.DataAnnotations;
using ATAS.Indicators;
using WolfMoss.ATAS.DeepMEffort.Core;
using MediaColor = System.Windows.Media.Color;

namespace WolfMoss.ATAS.DeepMEffort;

public sealed partial class DeepMEffortIndicator
{
    private MediaColor _bullishColor = MediaColor.FromRgb(0, 255, 0);
    private MediaColor _bearishColor = MediaColor.FromRgb(128, 21, 232);
    private int _fillAlpha = 125;
    private int _zoneWidth = 1;
    private bool _showEma = true;
    private bool _showInputStatus = true;

    [Display(Name = "最小 Delta 占比 (%)", GroupName = "计算", Order = 10, Description = "0 关闭下限。")]
    [Range(0, 100)]
    public double MinDeltaPercent { get => _config.MinDeltaPercent; set => Change(c => c with { MinDeltaPercent = Percent(value) }); }

    [Display(Name = "最大 Delta 占比 (%)", GroupName = "计算", Order = 20, Description = "0 关闭上限。")]
    [Range(0, 100)]
    public double MaxDeltaPercent { get => _config.MaxDeltaPercent; set => Change(c => c with { MaxDeltaPercent = Percent(value) }); }

    [Display(Name = "最大 |Effort|", GroupName = "计算", Order = 30, Description = "Delta / (实体 tick 数 + 1)，整数向零截断。0 关闭过滤。")]
    [Range(0, 1000000)]
    public double MaxDeltaEffort { get => _config.MaxDeltaEffort; set => Change(c => c with { MaxDeltaEffort = FiniteClamp(value, 1000000) }); }

    [Display(Name = "最大柱耗时 (秒)", GroupName = "计算", Order = 40, Description = "相邻柱 Time 的差，整数秒向下取整。0 关闭过滤。")]
    [Range(0, 86400000)]
    public int MaxTimeSeconds { get => (int)_config.MaxTimeSeconds; set => Change(c => c with { MaxTimeSeconds = Math.Clamp(value, 0, 86400000) }); }

    [Display(Name = "最少连续合格柱", GroupName = "计算", Order = 50)]
    [Range(1, 4096)]
    public int MinimumBars { get => _config.MinimumBars; set => Change(c => c with { MinimumBars = Math.Clamp(value, 1, 4096) }); }

    [Display(Name = "EMA 长度", GroupName = "计算", Order = 60)]
    [Range(1, 4096)]
    public int EmaLength { get => _config.EmaLength; set => Change(c => c with { EmaLength = Math.Clamp(value, 1, 4096) }); }

    [Display(Name = "区域最大延伸柱跨度", GroupName = "区域", Order = 70)]
    [Range(1, 4096)]
    public int MaxZoneExtensions { get => _config.MaxZoneExtensions; set => Change(c => c with { MaxZoneExtensions = Math.Clamp(value, 1, 4096) }); }

    [Display(Name = "保留完整范围的最大段长", GroupName = "区域", Order = 80)]
    [Range(0, 4096)]
    public int FullRangeMaxBars { get => _config.FullRangeMaxBars; set => Change(c => c with { FullRangeMaxBars = Math.Clamp(value, 0, 4096) }); }

    [Display(Name = "区域裁剪比例 (%)", GroupName = "区域", Order = 90, Description = "超过完整范围段长后，上行从顶部裁剪，下行从底部裁剪；50 保留一半，100 压缩为边界线。")]
    [Range(0, 100)]
    public double EntryZonePercent { get => _config.EntryZonePercent; set => Change(c => c with { EntryZonePercent = Percent(value) }); }

    [Display(Name = "上行区域颜色", GroupName = "外观", Order = 100)]
    public MediaColor BullishColor { get => _bullishColor; set { lock (_gate) _bullishColor = value; RedrawChart(); } }

    [Display(Name = "下行区域颜色", GroupName = "外观", Order = 110)]
    public MediaColor BearishColor { get => _bearishColor; set { lock (_gate) _bearishColor = value; RedrawChart(); } }

    [Display(Name = "填充不透明度 (0–255)", GroupName = "外观", Order = 120)]
    [Range(0, 255)]
    public int FillAlpha { get => _fillAlpha; set { lock (_gate) _fillAlpha = Math.Clamp(value, 0, 255); RedrawChart(); } }

    [Display(Name = "区域边框宽度", GroupName = "外观", Order = 130)]
    [Range(1, 20)]
    public int ZoneWidth { get => _zoneWidth; set { lock (_gate) _zoneWidth = Math.Clamp(value, 1, 20); RedrawChart(); } }

    [Display(Name = "显示 EMA", GroupName = "外观", Order = 140)]
    public bool ShowEma
    {
        get => _showEma;
        set { lock (_gate) { _showEma = value; _ema.VisualType = value ? VisualMode.Line : VisualMode.Hide; _ema.ShowCurrentValue = value; } RedrawChart(); }
    }

    [Display(Name = "EMA 颜色", GroupName = "外观", Order = 150)]
    public MediaColor EmaColor { get => _ema.Color; set { lock (_gate) _ema.Color = value; RedrawChart(); } }

    [Display(Name = "EMA 线宽", GroupName = "外观", Order = 160)]
    [Range(1, 20)]
    public int EmaWidth { get => _ema.Width; set { lock (_gate) _ema.Width = Math.Clamp(value, 1, 20); RedrawChart(); } }

    [Display(Name = "显示输入提示", GroupName = "外观", Order = 170)]
    public bool ShowInputStatus { get => _showInputStatus; set { lock (_gate) _showInputStatus = value; RedrawChart(); } }

    private static double Percent(double value) => FiniteClamp(value, 100);
    private static double FiniteClamp(double value, double max)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        return Math.Clamp(value, 0, max);
    }

    private void Change(Func<EffortConfig, EffortConfig> update)
    {
        lock (_gate)
        {
            var next = update(_config);
            if (next == _config) return;
            _config = next;
        }
        RecalculateValues();
        RedrawChart();
    }
}
