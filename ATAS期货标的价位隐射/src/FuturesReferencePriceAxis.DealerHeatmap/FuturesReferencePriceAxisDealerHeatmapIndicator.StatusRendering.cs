namespace WolfMoss.ATAS.PriceMapping;

using System.Drawing;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;
using WolfMoss.ATAS.PriceMapping.Core;

public sealed partial class FuturesReferencePriceAxisDealerHeatmapIndicator
{
    private Rectangle? _renderedStatusPanel;
    private string[]? _panelBaseLines;
    private IReadOnlyList<string>? _panelEditionLines;
    private object? _panelFont;
    private int _panelTextWidth, _panelMaxRows;
    private string[] _panelWrappedLines = Array.Empty<string>();
    private (long Second, FlowCountdownContext? Context, DateTime? Switch, OptionFlowBucketMode Mode, int Minutes, OptionDataStatus Status) _countdownTextKey;
    private string _countdownText = string.Empty;

    protected override void DrawStatusPanel(RenderContext context, InstrumentPair pair,
        UpdateAttemptSnapshot attempt, MappingSnapshot? mapping, decimal? effectiveRatio)
    {
        var region = ChartInfo!.PriceChartContainer.Region;
        var width = GetActualAxisWidth(AxisWidth, region);
        var anchor = StatusUiPresentation.Place(region, width + GetEditionReservedWidth(width),
            _statusPanelWidth, StatusPanelOffsetX, StatusPanelOffsetY, 0, _showOptionPremiumFlow);
        var room = region.Height - 8 - 10
            - (_showOptionPremiumFlow ? StatusUiPresentation.CountdownHeight + StatusUiPresentation.CountdownGap : 0);
        var maxRows = Math.Max(0, room / 19);
        if (anchor.Width < 180 || maxRows == 0) return;

        var baseLines = _showMappingStatus ? GetStatusLines(pair, attempt, mapping, effectiveRatio) : Array.Empty<string>();
        var editionLines = GetEditionStatusLines();
        var font = ChartInfo.PriceAxisFont;
        if (!ReferenceEquals(_panelBaseLines, baseLines) || !ReferenceEquals(_panelEditionLines, editionLines)
            || !ReferenceEquals(_panelFont, font) || _panelTextWidth != anchor.Width - 16 || _panelMaxRows != maxRows)
        {
            _panelBaseLines = baseLines; _panelEditionLines = editionLines; _panelFont = font;
            _panelTextWidth = anchor.Width - 16; _panelMaxRows = maxRows;
            _panelWrappedLines = WrapStatusLines(context, font, baseLines, editionLines, _panelTextWidth, maxRows);
        }
        if (_panelWrappedLines.Length == 0) return;
        var panel = StatusUiPresentation.Place(region, width + GetEditionReservedWidth(width),
            _statusPanelWidth, StatusPanelOffsetX, StatusPanelOffsetY, 10 + _panelWrappedLines.Length * 19, _showOptionPremiumFlow);
        _renderedStatusPanel = panel;
        context.FillRectangle(StatusBackgroundColor, panel);
        DrawBorder(context, panel, AxisBorderColor);
        var firstColor = attempt.State switch
        {
            UpdateState.Success => CallWallColor,
            UpdateState.Frozen or UpdateState.Failed => Color.Coral,
            _ => AxisTextColor
        };
        for (var i = 0; i < _panelWrappedLines.Length; i++)
            context.DrawString(_panelWrappedLines[i], font, i == 0 && _showMappingStatus ? firstColor : AxisTextColor,
                new Rectangle(panel.Left + 8, panel.Top + 5 + i * 19, panel.Width - 16, 19), LeftCenteredStringFormat);
    }

    // Keep the measuring delegate/closure entirely off the unchanged render path.
    private static string[] WrapStatusLines(RenderContext context, RenderFont font,
        string[] baseLines, IReadOnlyList<string> editionLines, int width, int maxRows)
        => StatusUiPresentation.Wrap(baseLines.Concat(editionLines).ToArray(), width, maxRows,
            text => context.MeasureString(text, font).Width);

    private void DrawFlowCountdown(RenderContext context)
    {
        if (!_showOptionPremiumFlow || ChartInfo == null || Container == null) return;
        var region = ChartInfo.PriceChartContainer.Region;
        if (region.Height < StatusUiPresentation.CountdownHeight + 8) return;
        var width = GetActualAxisWidth(AxisWidth, region);
        var panel = _renderedStatusPanel ?? StatusUiPresentation.Place(region,
            width + GetEditionReservedWidth(width), _statusPanelWidth, StatusPanelOffsetX, StatusPanelOffsetY, 0, true);
        if (panel.Width < 180) return;
        var rect = new Rectangle(panel.Left, panel.Bottom + (panel.Height > 0 ? StatusUiPresentation.CountdownGap : 0),
            panel.Width, StatusUiPresentation.CountdownHeight);
        context.FillRectangle(StatusBackgroundColor, rect);
        DrawBorder(context, rect, CallWallColor);
        // Immutable references only. No data-owner locks or IB requests on the rendering thread.
        var timing = Volatile.Read(ref _flowCountdownContext);
        var frame = Volatile.Read(ref _optionFlowSnapshot);
        var plan = Volatile.Read(ref _tickerPlan);
        var nextSwitch = timing != null && plan?.Ticker == timing.Ticker && plan.Expiration == timing.Expiration
            ? (DateTime?)plan.NextSwitchUtc : null;
        var now = RealtimeUtcNow();
        var key = (now.Ticks / TimeSpan.TicksPerSecond, timing, nextSwitch, _optionFlowBucketMode, _optionFlowIntervalMinutes, frame.Status);
        if (key != _countdownTextKey)
        {
            _countdownTextKey = key;
            _countdownText = FlowCountdownPresentation.Text(now, _optionFlowBucketMode,
                _optionFlowIntervalMinutes, timing?.Segments, nextSwitch, frame.Status);
        }
        context.DrawString(_countdownText, ChartInfo.PriceAxisFont, AxisTextColor,
            new Rectangle(rect.Left + 8, rect.Top + 3, rect.Width - 16, rect.Height - 6), LeftCenteredStringFormat);
    }
}
