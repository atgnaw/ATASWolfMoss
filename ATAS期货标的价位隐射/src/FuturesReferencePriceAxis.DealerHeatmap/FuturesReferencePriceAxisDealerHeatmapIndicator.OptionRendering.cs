namespace WolfMoss.ATAS.PriceMapping;

using System.Drawing;
using System.Globalization;

using global::ATAS.Indicators;

using OFT.Rendering.Context;

using WolfMoss.ATAS.PriceMapping.Core;

using DrawingColor = System.Drawing.Color;

public sealed partial class FuturesReferencePriceAxisDealerHeatmapIndicator
{
    private readonly OptionRenderCache _oiRenderCache = new();
    private readonly OptionRenderCache _flowRenderCache = new();
    private static readonly DrawingColor OptionTrackBackground =
        DrawingColor.FromArgb(185, 38, 45, 55);
    private static readonly DrawingColor OptionCallColor =
        DrawingColor.FromArgb(235, 47, 158, 101);
    private static readonly DrawingColor OptionPutColor =
        DrawingColor.FromArgb(235, 224, 74, 82);
    private static readonly DrawingColor OptionAtmColor =
        DrawingColor.FromArgb(255, 34, 211, 238);

    private void DrawOptionOpenInterestColumn(
        RenderContext context,
        Rectangle rect,
        decimal effectiveRatio)
    {
        if (!CanDrawColumn(rect))
            return;

        DrawColumnBackground(context, rect);
        var snapshot = Volatile.Read(ref _optionOpenInterestSnapshot);
        DrawColumnHeader(
            context,
            rect,
            snapshot.Rows.Any(static row =>
                row.CallOpenInterest.HasValue || row.PutOpenInterest.HasValue)
                ? snapshot.ReceivedUtc
                : null);

        if (snapshot.Ticker == null || snapshot.Rows.Count == 0)
        {
            DrawColumnMessage(context, rect, snapshot.Message);
            return;
        }

        var maximum = _oiRenderCache.Get(snapshot.Rows, true, OptionDataStatus.Daily).Maximum;
        DrawOptionRows(
            context,
            rect,
            effectiveRatio,
            snapshot.Rows,
            snapshot.AtmStrikeUsd,
            maximum,
            useOpenInterest: true,
            OptionDataStatus.Daily);
    }

    private void DrawOptionFlowColumn(
        RenderContext context,
        Rectangle rect,
        decimal effectiveRatio)
    {
        if (!CanDrawColumn(rect))
            return;

        DrawColumnBackground(context, rect);
        var snapshot = Volatile.Read(ref _optionFlowSnapshot);
        DrawColumnHeader(context, rect, snapshot.BucketEndUtc);

        if (snapshot.Ticker == null || snapshot.Rows.Count == 0)
        {
            DrawColumnMessage(context, rect, snapshot.Message);
            return;
        }

        var maximum = _flowRenderCache.Get(snapshot.Rows, false, snapshot.Status).Maximum;
        DrawOptionRows(
            context,
            rect,
            effectiveRatio,
            snapshot.Rows,
            snapshot.AtmStrikeUsd,
            maximum,
            useOpenInterest: false,
            snapshot.Status);
    }

    private void DrawOptionRows(
        RenderContext context,
        Rectangle columnRect,
        decimal ratio,
        IReadOnlyList<OptionStrikeRow> rows,
        decimal? atmStrike,
        decimal maximum,
        bool useOpenInterest,
        OptionDataStatus status)
    {
        var frame = (useOpenInterest ? _oiRenderCache : _flowRenderCache).Get(rows, useOpenInterest, status);
        var ordered = frame.Rows;
        var strikes = frame.Strikes;

        for (var index = 0; index < ordered.Length; index++)
        {
            var prepared = ordered[index];
            var row = prepared.Row;
            var rowRect = GetOptionRowRectangle(
                columnRect,
                strikes,
                index,
                ratio,
                prepared.Lower,
                prepared.Upper);

            if (!rowRect.HasValue)
                continue;

            var topHeight = Math.Max(1, rowRect.Value.Height / 2);
            var callRect = new Rectangle(
                rowRect.Value.Left,
                rowRect.Value.Top,
                rowRect.Value.Width,
                topHeight);
            var putRect = new Rectangle(
                rowRect.Value.Left,
                rowRect.Value.Top + topHeight,
                rowRect.Value.Width,
                Math.Max(1, rowRect.Value.Height - topHeight));
            var callValue = useOpenInterest
                ? row.CallOpenInterest
                : row.CallPremium;
            var putValue = useOpenInterest
                ? row.PutOpenInterest
                : row.PutPremium;
            var callColor = !useOpenInterest && row.CallFlowRetained
                ? DrawingColor.FromArgb(130, OptionCallColor) : OptionCallColor;
            var putColor = !useOpenInterest && row.PutFlowRetained
                ? DrawingColor.FromArgb(130, OptionPutColor) : OptionPutColor;
            DrawOptionLane(context, callRect, callValue, maximum, callColor, prepared.CallLabel);
            DrawOptionLane(context, putRect, putValue, maximum, putColor, prepared.PutLabel);

            if (atmStrike.HasValue && row.StrikeUsd == atmStrike.Value)
                DrawBorder(context, rowRect.Value, OptionAtmColor);
        }
    }

    private void DrawOptionLane(
        RenderContext context,
        Rectangle track,
        decimal? value,
        decimal maximum,
        DrawingColor color,
        string label)
    {
        if (!value.HasValue)
        {
            if (track.Height >= 9 && track.Width >= 38)
            {
                context.DrawString(
                    label,
                    ChartInfo!.PriceAxisFont,
                    DrawingColor.FromArgb(190, 160, 169, 181),
                    new Rectangle(track.Left + 2, track.Top, track.Width - 4, track.Height),
                    LeftCenteredStringFormat);
            }

            return;
        }

        context.FillRectangle(OptionTrackBackground, track);
        DrawOptionBar(context, track, value.Value, maximum, color, label);
    }

    private void DrawOptionBar(
        RenderContext context,
        Rectangle track,
        decimal value,
        decimal maximum,
        DrawingColor color,
        string label)
    {
        var ratio = OptionPresentation.GetFillRatio(value, maximum);
        var width = (int)Math.Round(track.Width * ratio);

        if (width > 0)
        {
            context.FillRectangle(
                color,
                new Rectangle(track.Left, track.Top, Math.Min(track.Width, width), track.Height));
        }

        if (track.Height < 9 || track.Width < 38)
            return;

        context.DrawString(
            label,
            ChartInfo!.PriceAxisFont,
            DrawingColor.White,
            new Rectangle(track.Left + 2, track.Top, track.Width - 4, track.Height),
            LeftCenteredStringFormat);
    }

    private Rectangle? GetOptionRowRectangle(
        Rectangle columnRect,
        IReadOnlyList<decimal> orderedStrikes,
        int index,
        decimal ratio,
        decimal? lowerBound = null,
        decimal? upperBound = null)
    {
        var bounds = lowerBound.HasValue && upperBound.HasValue
            ? (Lower: lowerBound.Value, Upper: upperBound.Value)
            : OptionStrikeSelection.GetRowBounds(orderedStrikes, index);
        var chart = ChartInfo!;
        var firstY = chart.GetYByPrice(bounds.Upper * ratio);
        var secondY = chart.GetYByPrice(bounds.Lower * ratio);
        var top = Math.Max(columnRect.Top + DealerHeaderHeight,
            Math.Min(firstY, secondY));
        var bottom = Math.Min(columnRect.Bottom - 1,
            Math.Max(firstY, secondY));

        if (bottom <= top)
            return null;

        return new Rectangle(
            columnRect.Left + 1,
            top,
            Math.Max(1, columnRect.Width - 2),
            Math.Max(1, bottom - top));
    }

    private void DrawOptionOpenInterestTooltip(
        RenderContext context,
        Rectangle rect,
        decimal ratio)
    {
        var snapshot = Volatile.Read(ref _optionOpenInterestSnapshot);

        if (!TryGetHoveredOptionRow(rect, ratio, _oiRenderCache.Get(snapshot.Rows, true, OptionDataStatus.Daily),
                out var row, out var right, out _))
        {
            return;
        }

        var value = right == OptionRight.Call
            ? row.CallOpenInterest
            : row.PutOpenInterest;
        var location = GetOptionStrikeLocation(snapshot.Rows, row.StrikeUsd, snapshot.AtmStrikeUsd);
        DrawTooltip(context, MouseLocationInfo!.LastPosition, new[]
        {
            $"{snapshot.Ticker} {row.StrikeUsd.ToString("0.####", CultureInfo.InvariantCulture)} {right}",
            $"OI {(value.HasValue ? value.Value.ToString("#,0", CultureInfo.InvariantCulture) : "--")}",
            $"Expiration {snapshot.Expiration:yyyy-MM-dd}",
            $"Position {location}",
            $"State {snapshot.Status.ToString().ToUpperInvariant()}",
            $"Last IB receipt {FormatOptionalOptionTime(snapshot.ReceivedByStrike.GetValueOrDefault((row.StrikeUsd, right)))}",
            snapshot.Message
        }, 300);
    }

    private void DrawOptionFlowTooltip(
        RenderContext context,
        Rectangle rect,
        decimal ratio)
    {
        var snapshot = Volatile.Read(ref _optionFlowSnapshot);

        if (!TryGetHoveredOptionRow(rect, ratio, _flowRenderCache.Get(snapshot.Rows, false, snapshot.Status),
                out var row, out var right, out _))
        {
            return;
        }

        var premium = right == OptionRight.Call ? row.CallPremium : row.PutPremium;
        var volume = right == OptionRight.Call ? row.CallVolume : row.PutVolume;
        var observedStartUtc = right == OptionRight.Call
            ? row.CallFlowObservedStartUtc
            : row.PutFlowObservedStartUtc;
        var isPartial = right == OptionRight.Call
            ? row.CallFlowIsPartial
            : row.PutFlowIsPartial;
        var flowCoverage = right == OptionRight.Call
            ? row.CallFlowCoverage : row.PutFlowCoverage;
        var retained = right == OptionRight.Call
            ? row.CallFlowRetained : row.PutFlowRetained;
        var coverage = OptionPresentation.GetFlowCoverageLabel(
            snapshot.Status,
            premium.HasValue,
            isPartial,
            flowCoverage);
        var location = GetOptionStrikeLocation(snapshot.Rows, row.StrikeUsd, snapshot.AtmStrikeUsd);
        var session = GetOptionSessionLabel(snapshot.Ticker, snapshot.BucketEndUtc);
        var lines = new List<string>
        {
            $"{snapshot.Ticker} {row.StrikeUsd.ToString("0.####", CultureInfo.InvariantCulture)} {right}",
            $"Premium {(premium.HasValue ? premium.Value.ToString("#,0.##", CultureInfo.InvariantCulture) : "--")}",
            $"Volume {(volume.HasValue ? volume.Value.ToString("#,0", CultureInfo.InvariantCulture) : "--")}",
            $"Expiration {snapshot.Expiration:yyyy-MM-dd}",
            $"Position {location}",
            $"{snapshot.IntervalMinutes}m {snapshot.BucketMode} / {snapshot.TradeScope}",
            $"Bucket {FormatOptionalOptionTime(snapshot.BucketStartUtc)} - {FormatOptionalOptionTime(snapshot.BucketEndUtc)}",
            $"Coverage {coverage}",
            $"Observed from {FormatOptionalOptionTime(observedStartUtc)}",
            $"Session {session}",
            $"Received {FormatDealerTime(snapshot.ReceivedUtc)}"
        };
        if (snapshot.BucketMode == OptionFlowBucketMode.Rolling)
        {
            lines.Add(retained
                ? "RETAINED / 已移出范围，仅保留窗口内旧数据"
                : "ACTIVE / 当前接收范围");
            if (snapshot.AtmLockedUntilUtc.HasValue)
                lines.Add($"ATM locked until {FormatOptionalOptionTime(snapshot.AtmLockedUntilUtc)}");
            if (isPartial)
                lines.Add("* PARTIAL / 仅统计已观察到的部分成交");
        }
        DrawTooltip(context, MouseLocationInfo!.LastPosition, lines.ToArray(), 420);
    }

    private bool TryGetHoveredOptionRow(
        Rectangle rect,
        decimal ratio,
        OptionRenderFrame frame,
        out OptionStrikeRow row,
        out OptionRight right,
        out Rectangle rowRect)
    {
        row = default;
        right = default;
        rowRect = default;
        var mouse = MouseLocationInfo;

        if (mouse == null
            || mouse.IsMouseLeave
            || mouse.IsMovingChartUsingMouse
            || !rect.Contains(mouse.LastPosition))
        {
            return false;
        }

        var ordered = frame.Rows;
        var strikes = frame.Strikes;

        for (var index = 0; index < ordered.Length; index++)
        {
            var candidate = GetOptionRowRectangle(rect, strikes, index, ratio,
                ordered[index].Lower, ordered[index].Upper);

            if (!candidate.HasValue || !candidate.Value.Contains(mouse.LastPosition))
                continue;

            row = ordered[index].Row;
            rowRect = candidate.Value;
            right = mouse.LastPosition.Y < candidate.Value.Top + candidate.Value.Height / 2
                ? OptionRight.Call
                : OptionRight.Put;
            return true;
        }

        return false;
    }

    private static string GetOptionStrikeLocation(
        IReadOnlyList<OptionStrikeRow> rows,
        decimal strike,
        decimal? atmStrike)
    {
        if (!atmStrike.HasValue)
            return "--";

        if (strike == atmStrike.Value)
            return "ATM";

        var ordered = rows.Select(static row => row.StrikeUsd)
            .Distinct()
            .OrderBy(static value => value)
            .ToArray();
        var strikeIndex = Array.IndexOf(ordered, strike);
        var atmIndex = Array.IndexOf(ordered, atmStrike.Value);

        if (strikeIndex < 0 || atmIndex < 0)
            return strike > atmStrike.Value ? "ATM above" : "ATM below";

        var distance = Math.Abs(strikeIndex - atmIndex);
        return strikeIndex > atmIndex
            ? $"ATM above +{distance}"
            : $"ATM below -{distance}";
    }

    private static string GetOptionSessionLabel(string? ticker, DateTime? utc)
    {
        if (!utc.HasValue)
            return "--";

        if (!string.Equals(ticker, "SPX", StringComparison.OrdinalIgnoreCase))
            return "RTH";

        var eastern = UsMarketClock.ToEastern(utc.Value);
        var open = eastern.Date + NyseTradingCalendar.RegularOpen;
        var close = eastern.Date + NyseTradingCalendar.GetRegularClose(eastern.Date);
        return eastern >= open && eastern <= close ? "RTH" : "GTH";
    }

    private string FormatOptionalOptionTime(DateTime? utc)
        => utc.HasValue ? FormatDealerTime(utc.Value) : "--";

    private static string OptionStateLabel(OptionDataStatus state)
        => state switch
        {
            OptionDataStatus.Disabled => "OFF",
            OptionDataStatus.Connecting => "CONNECTING",
            OptionDataStatus.WaitingChain => "WAITING CHAIN",
            OptionDataStatus.WaitingOpenInterest => "WAITING OI",
            OptionDataStatus.Warming => "WARMING",
            OptionDataStatus.Live => "LIVE",
            OptionDataStatus.Daily => "DAILY",
            OptionDataStatus.Partial => "PARTIAL",
            OptionDataStatus.Closed => "CLOSED",
            OptionDataStatus.Frozen => "FROZEN",
            OptionDataStatus.Delayed => "DELAYED",
            OptionDataStatus.NoPermission => "NO PERMISSION",
            OptionDataStatus.LineLimit => "LINE LIMIT",
            OptionDataStatus.NoContracts => "NO CONTRACTS",
            _ => state.ToString().ToUpperInvariant()
        };
}
