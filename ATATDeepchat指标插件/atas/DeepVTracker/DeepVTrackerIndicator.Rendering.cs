using System.Drawing;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;
using MediaColor = System.Windows.Media.Color;

namespace WolfMoss.ATAS.DeepVTracker;

public sealed partial class DeepVTrackerIndicator
{
    private readonly Dictionary<(Color Color, int Width), RenderPen> _pens = [];
    private RenderFont? _labelFont;
    private readonly Point[] _triangle = new Point[3];
    private static Color ColorOf(MediaColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);
    private RenderPen Pen(Color c, int width)
    {
        var key = (c, width);
        if (!_pens.TryGetValue(key, out var pen)) _pens[key] = pen = new RenderPen(c, width);
        return pen;
    }
    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        var chart = ChartInfo;
        if (chart is null) return;
        var container = chart.PriceChartContainer;
        var clip = context.ClipBounds;
        var region = Rectangle.Intersect(container.Region, clip);
        if (region.Width <= 0 || region.Height <= 0) return;
        context.SetClip(region);
        try
        {
            lock (_gate)
            {
                if (_disposed) return;
                var first = Math.Max(0, FirstVisibleBarNumber);
                var last = Math.Min(LastVisibleBarNumber, _observed);
                _labelFont ??= new RenderFont("Arial", _fontSize);
                int X(double bar)
                {
                    var whole = (int)Math.Floor(bar);
                    return container.GetXByBar(whole, false) + (int)((bar - whole) * (double)(container.BarsWidth + container.BarSpacing));
                }
                int Y(long ticks) => container.GetYByPrice(ticks * _tickSize, false);
                if (_replay is not null && _tickSize > 0 && last >= first)
                {
                    // Two passes put filled acceleration regions below all line/icon overlays.
                    for (var pass = 0; pass < 2; pass++)
                    foreach (var signal in _replay.Signals.Values)
                    {
                        if ((signal.Kind == SignalKind.Acceleration) != (pass == 0)) continue;
                        var end = signal.Active ? _observed : signal.End;
                        if (signal.Start + signal.StartOffset > last + 1 || end + signal.EndOffset < first) continue;
                        var left = X(Math.Max(first, signal.Start + signal.StartOffset));
                        var right = X(Math.Min(last + 1, end + signal.EndOffset));
                        var y = Y(signal.Lower);
                        if (signal.Kind == SignalKind.Acceleration)
                        {
                            var other = Y(signal.Upper);
                            if (Math.Max(y, other) < region.Top || Math.Min(y, other) > region.Bottom) continue;
                            var color = ColorOf(_accelerationColor);
                            var rect = Rectangle.FromLTRB(left, Math.Min(y, other), Math.Max(left + 1, right), Math.Max(y, other));
                            if (rect.Height == 0) context.DrawLine(Pen(color, 1), left, y, right, y);
                            else { context.FillRectangle(Color.FromArgb(_accelerationAlpha, color), rect); context.DrawRectangle(Pen(color, 1), rect); }
                        }
                        else if (signal.Kind is SignalKind.Control or SignalKind.Extreme)
                        {
                            var width = signal.Kind == SignalKind.Control ? _controlWidth : _extremeWidth;
                            if (width == 0 || y < region.Top - _fontSize || y > region.Bottom + _fontSize) continue;
                            var color = ColorOf(signal.Ask ? _askColor : _bidColor);
                            var pen = Pen(color, width);
                            if (signal.Absorption)
                            {
                                // Use the public line API for portable dashed strokes.
                                for (var x = Math.Max(left, region.Left); x < Math.Min(right, region.Right); x += 9)
                                    context.DrawLine(pen, x, y, Math.Min(x + 5, right), y);
                            }
                            else context.DrawLine(pen, left, y, right, y);
                            if (_showLabels)
                            {
                                var label = (signal.Absorption ? "A" : "P") + (signal.Kind == SignalKind.Control ? "C" : "E");
                                context.DrawString(label, _labelFont, color, Math.Max(left, region.Left) + 4, y - _fontSize - 4);
                            }
                        }
                        else
                        {
                            if (signal.Start + signal.StartOffset < first || y < region.Top - _iconSize || y > region.Bottom + _iconSize) continue;
                            var radius = _iconSize / 2; var d = signal.Up ? 1 : -1;
                            _triangle[0] = new(left, y - d * radius); _triangle[1] = new(left - radius, y + d * radius); _triangle[2] = new(left + radius, y + d * radius);
                            var color = ColorOf(signal.Kind == SignalKind.Slowdown ? _slowdownColor : _exhaustionColor);
                            if (signal.Kind == SignalKind.ExhaustionVolume) context.FillPolygon(color, _triangle);
                            else context.DrawPolygon(Pen(color, signal.Kind == SignalKind.Slowdown ? 2 : 1), _triangle);
                        }
                    }
                }
                if (_showStatus || _faulted)
                {
                    var issues = _replay?.Issues.Count(p => p.Key >= first && p.Key <= last) ?? 0;
                    var text = "Deep-V Tracker：" + _status;
                    if (issues > 0) text += $"；可见区 {issues} 根柱数据不足，已跳过并重新预热";
                    context.DrawString(text, chart.PriceAxisFont, Color.FromArgb(255, 211, 145, 40), region.Left + 6, region.Top + 6);
                }
            }
        }
        finally { context.SetClip(clip); }
    }
}
