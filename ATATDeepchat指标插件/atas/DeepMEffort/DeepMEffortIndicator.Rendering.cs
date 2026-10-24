using System.Drawing;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;
using WolfMoss.ATAS.DeepMEffort.Core;
using MediaColor = System.Windows.Media.Color;

namespace WolfMoss.ATAS.DeepMEffort;

public sealed partial class DeepMEffortIndicator
{
    private RenderPen? _bullPen;
    private RenderPen? _bearPen;
    private (MediaColor Bull, MediaColor Bear, int Width)? _penStyle;

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        var chart = ChartInfo;
        if (chart is null) return;
        var container = chart.PriceChartContainer;
        var region = Rectangle.Intersect(container.Region, context.ClipBounds);
        if (region.Width <= 0 || region.Height <= 0) return;
        var oldClip = context.ClipBounds;
        context.SetClip(region);
        try
        {
            lock (_gate)
            {
                var first = Math.Max(0, FirstVisibleBarNumber);
                var last = Math.Min(LastVisibleBarNumber, _lastObserved);
                var style = (_bullishColor, _bearishColor, _zoneWidth);
                if (_penStyle != style)
                {
                    _bullPen = new RenderPen(DrawingColor(_bullishColor), _zoneWidth);
                    _bearPen = new RenderPen(DrawingColor(_bearishColor), _zoneWidth);
                    _penStyle = style;
                }
                if (_engine is not null && last >= first)
                {
                    var zones = _engine.Zones;
                    // Every exposed zone has a finite lifetime. Binary search avoids walking old history
                    // on every mouse repaint, while including zones that began left of the viewport.
                    var minCreated = Math.Max(0, first - _config.MaxZoneExtensions - 1);
                    var lo = 0;
                    var hi = zones.Count;
                    while (lo < hi)
                    {
                        var mid = lo + (hi - lo) / 2;
                        if (zones[mid].CreatedIndex < minCreated) lo = mid + 1;
                        else hi = mid;
                    }
                    for (var i = lo; i < zones.Count; i++)
                    {
                        var zone = zones[i];
                        if (zone.CreatedIndex > last) break;
                        var end = zone.IsActive ? _lastObserved : zone.EndIndex;
                        if (end < first) continue;
                        var x1 = container.GetXByBar(Math.Max(zone.CreatedIndex, first), false);
                        var x2 = container.GetXByBar(Math.Min(end, last + 1), false);
                        var y1 = container.GetYByPrice((decimal)zone.Upper, false);
                        var y2 = container.GetYByPrice((decimal)zone.Lower, false);
                        if (Math.Max(y1, y2) < region.Top || Math.Min(y1, y2) > region.Bottom) continue;
                        var rect = Rectangle.FromLTRB(x1, Math.Min(y1, y2), Math.Max(x1 + 1, x2), Math.Max(y1, y2));
                        var color = DrawingColor(zone.IsBullish ? _bullishColor : _bearishColor);
                        var pen = zone.IsBullish ? _bullPen! : _bearPen!;
                        if (rect.Height == 0) context.DrawLine(pen, rect.Left, rect.Top, rect.Right, rect.Top);
                        else
                        {
                            context.FillRectangle(Color.FromArgb(_fillAlpha, color), rect);
                            context.DrawRectangle(pen, rect);
                        }
                    }
                }
                var status = _configurationError;
                if (status is null && _showInputStatus)
                {
                    if (_lastObserved >= 0 && _tickSize <= 0) status = "Deep-M Effort：等待合约最小跳动单位。";
                    else
                    {
                        var flags = InputIssue.None;
                        var count = 0;
                        // Issues are sorted; only inspect the visible part of the history.
                        var lo = 0;
                        var hi = _issues.Count;
                        while (lo < hi) { var mid = lo + (hi - lo) / 2; if (_issues[mid].Bar < first) lo = mid + 1; else hi = mid; }
                        for (var i = lo; i < _issues.Count && _issues[i].Bar <= last; i++) { flags |= _issues[i].Issue; count++; }
                        if (count != 0)
                            status = $"Deep-M Effort：{count} 根柱跳过区域判断"
                                + (flags.HasFlag(InputIssue.Volume) ? "；Ask/Bid 不完整、含中间成交或非整数成交量" : "")
                                + (flags.HasFlag(InputIssue.Time) ? "；柱时间无效或倒序" : "")
                                + (flags.HasFlag(InputIssue.TickGrid) ? "；价格不在合约 tick 网格" : "");
                    }
                }
                if (status is not null)
                    context.DrawString(status, chart.PriceAxisFont, Color.FromArgb(255, 211, 145, 40), region.Left + 6, region.Top + 6);
            }
        }
        finally { context.SetClip(oldClip); }
    }

    private static Color DrawingColor(MediaColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);
}
