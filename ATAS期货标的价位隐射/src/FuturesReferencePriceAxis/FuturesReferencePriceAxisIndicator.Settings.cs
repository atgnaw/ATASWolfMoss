namespace WolfMoss.ATAS.PriceMapping;

using System.ComponentModel.DataAnnotations;

using WolfMoss.ATAS.PriceMapping.Core;

using DrawingColor = System.Drawing.Color;

public abstract partial class FuturesReferencePriceAxisIndicatorBase
{
    [Display(
        Name = "Pair / 映射组合",
        GroupName = "Mapping / 映射",
        Order = 10)]
    public PairMode PairMode
    {
        get => _pairMode;
        set
        {
            if (_pairMode == value)
                return;

            _pairMode = value;
            Interlocked.Exchange(ref _successfulMapping, null);
            ConfigurationChanged(restartSchedule: false);
        }
    }

    [Display(
        Name = "Mode / 模式",
        GroupName = "Mapping / 映射",
        Order = 20)]
    public MappingMode MappingMode
    {
        get => _mappingMode;
        set
        {
            if (_mappingMode == value)
                return;

            _mappingMode = value;
            ConfigurationChanged(restartSchedule: true);
        }
    }

    [Display(
        Name = "Manual ratio / 手动比例",
        Description = "Futures price divided by QQQ/SPX price / 期货价除以参考价",
        GroupName = "Mapping / 映射",
        Order = 30)]
    [Range(typeof(decimal), "0", "1000000")]
    public decimal ManualRatio
    {
        get => _manualRatio;
        set
        {
            if (value < 0m || _manualRatio == value)
                return;

            _manualRatio = value;

            if (_mappingMode == Core.MappingMode.Manual)
            {
                SetAttempt(new UpdateAttemptSnapshot(
                    UpdateState.Manual,
                    CurrentUtcTime(),
                    "使用手动比例"));
            }

            RequestRedraw();
        }
    }

    [Display(
        Name = "Refresh interval (minutes) / 刷新分钟",
        GroupName = "Automatic / 自动",
        Order = 40)]
    [Range(1, 1440)]
    public int RefreshIntervalMinutes
    {
        get => _refreshIntervalMinutes;
        set
        {
            var normalized = Math.Clamp(value, 1, 1440);

            if (_refreshIntervalMinutes == normalized)
                return;

            _refreshIntervalMinutes = normalized;
            ConfigurationChanged(restartSchedule: true);
        }
    }

    [Display(
        Name = "Max quote age (minutes) / 最大报价延迟",
        GroupName = "Automatic / 自动",
        Order = 50)]
    [Range(1, 240)]
    public int MaxQuoteAgeMinutes
    {
        get => _maxQuoteAgeMinutes;
        set
        {
            var normalized = Math.Clamp(value, 1, 240);

            if (_maxQuoteAgeMinutes == normalized)
                return;

            _maxQuoteAgeMinutes = normalized;
            ConfigurationChanged(restartSchedule: false);
        }
    }

    [Display(
        Name = "Show update status / 显示更新状态",
        GroupName = "Status / 状态",
        Order = 60)]
    public bool ShowUpdateStatus
    {
        get => _showUpdateStatus;
        set
        {
            _showUpdateStatus = value;
            RequestRedraw();
        }
    }

    [Display(
        Name = "Status panel X offset (px) / 状态面板水平偏移",
        Description = "Positive moves right; negative moves left / 正数向右，负数向左",
        GroupName = "Status / 状态",
        Order = 70)]
    [Range(-2000, 2000)]
    public int StatusPanelOffsetX
    {
        get => _statusPanelOffsetX;
        set
        {
            var normalized = Math.Clamp(value, -2000, 2000);

            if (_statusPanelOffsetX == normalized)
                return;

            _statusPanelOffsetX = normalized;
            RequestRedraw();
        }
    }

    [Display(
        Name = "Status panel Y offset (px) / 状态面板垂直偏移",
        Description = "Positive moves down; negative moves up / 正数向下，负数向上",
        GroupName = "Status / 状态",
        Order = 80)]
    [Range(-2000, 2000)]
    public int StatusPanelOffsetY
    {
        get => _statusPanelOffsetY;
        set
        {
            var normalized = Math.Clamp(value, -2000, 2000);

            if (_statusPanelOffsetY == normalized)
                return;

            _statusPanelOffsetY = normalized;
            RequestRedraw();
        }
    }

    [Display(
        Name = "UI UTC offset (hours) / UI显示UTC偏移",
        Description = "Example: 8 = UTC+8; -5 = UTC-5 / 例如：8 表示 UTC+8",
        GroupName = "Status / 状态",
        Order = 90)]
    [Range(typeof(decimal), "-12", "14")]
    public decimal UiUtcOffsetHours
    {
        get => _uiUtcOffsetHours;
        set
        {
            var normalized = Math.Clamp(value, -12m, 14m);

            if (_uiUtcOffsetHours == normalized)
                return;

            _uiUtcOffsetHours = normalized;
            RequestRedraw();
        }
    }

    [Display(
        Name = "Show crosshair mapped price / 显示十字线映射价",
        GroupName = "Appearance / 外观",
        Order = 105)]
    public bool ShowCrosshairPriceLabel
    {
        get => _showCrosshairPriceLabel;
        set
        {
            _showCrosshairPriceLabel = value;
            RequestRedraw();
        }
    }

    [Display(
        Name = "Axis width / 左轴宽度",
        GroupName = "Appearance / 外观",
        Order = 100)]
    [Range(45, 160)]
    public int AxisWidth
    {
        get => _axisWidth;
        set
        {
            _axisWidth = Math.Clamp(value, 45, 160);
            RequestRedraw();
        }
    }

    [Display(
        Name = "Axis text / 轴文字",
        GroupName = "Appearance / 外观",
        Order = 110)]
    public DrawingColor AxisTextColor
    {
        get => _axisTextColor;
        set
        {
            _axisTextColor = value;
            RequestRedraw();
        }
    }

    [Display(
        Name = "Axis background / 轴背景",
        GroupName = "Appearance / 外观",
        Order = 120)]
    public DrawingColor AxisBackgroundColor
    {
        get => _axisBackgroundColor;
        set
        {
            _axisBackgroundColor = value;
            RequestRedraw();
        }
    }

    [Display(
        Name = "Axis border / 轴边框",
        GroupName = "Appearance / 外观",
        Order = 130)]
    public DrawingColor AxisBorderColor
    {
        get => _axisBorderColor;
        set
        {
            _axisBorderColor = value;
            RequestRedraw();
        }
    }

    [Display(
        Name = "Status background / 状态背景",
        GroupName = "Appearance / 外观",
        Order = 140)]
    public DrawingColor StatusBackgroundColor
    {
        get => _statusBackgroundColor;
        set
        {
            _statusBackgroundColor = value;
            RequestRedraw();
        }
    }

}
