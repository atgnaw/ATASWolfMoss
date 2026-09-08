namespace WolfMoss.ATAS.PriceMapping;

using System.Drawing;
using OFT.Rendering.Context;
using WolfMoss.ATAS.PriceMapping.Core;
using Indicator = WolfMoss.ATAS.PriceMapping.FuturesReferencePriceAxisDealerHeatmapIndicator;

public sealed partial class FuturesReferencePriceAxisDealerHeatmapIndicator
{
    private sealed record ColumnLifecycle(Action<Indicator> Initialize, Action<Indicator> Restart,
        Action<Indicator> Pause, Action<Indicator> Changed, Action<Indicator> Stop);
    private sealed record ColumnRegistration(DealerColumnKind Kind, DealerColumnVisibility Visibility,
        Func<Indicator, bool> Enabled, Func<Indicator, object?> Snapshot,
        Action<Indicator, RenderContext, Rectangle, decimal> Draw,
        Action<Indicator, RenderContext, Rectangle, decimal> Tooltip,
        Action<Indicator, List<string>, object, int> Status, ColumnLifecycle Lifecycle);

    private static readonly ColumnLifecycle NightwatchColumns = new(
        static i => { i._dealerHeatmapLifetimeCancellation = new(); i.RestartDealerHeatmapSchedule(); },
        static i => i.RestartDealerHeatmapSchedule(),
        static i => i.StopDealerHeatmapSchedule(),
        static i => { Interlocked.Increment(ref i._dealerHeatmapGeneration); if (i.IsIndicatorInitialized) i.RestartDealerHeatmapSchedule(); },
        static i =>
        {
            Interlocked.Increment(ref i._dealerHeatmapGeneration);
            i.StopDealerHeatmapSchedule();
            i._dealerHeatmapLifetimeCancellation?.Cancel();
            i._dealerHeatmapLifetimeCancellation?.Dispose();
            i._dealerHeatmapLifetimeCancellation = null;
        });
    private static readonly ColumnLifecycle IbColumns = new(
        static i => i.InitializeOptionData(), static i => i.RestartOptionDataSchedule(),
        static i => i.PauseOptionDataSchedule(), static i => i.RestartOptionDataSchedule(),
        static i => i.StopOptionData());

    // Static delegates: no per-render closure allocation; shared source lifecycles run once.
    private static readonly ColumnRegistration[] RegisteredColumns =
    [
        new(DealerColumnKind.Heatmap, DealerColumnVisibility.Heatmap,
            static i => i._showDealerHeatmap, static i => Volatile.Read(ref i._dealerHeatmapSnapshot),
            static (i,c,r,p) => i.DrawHeatmapColumn(c,r,p), static (i,c,r,p) => i.DrawHeatmapTooltip(c,r,p),
            static (i,lines,s,n) => i.AppendHeatmapStatus(lines,(DealerHeatmapSnapshot)s,n), NightwatchColumns),
        new(DealerColumnKind.DealerGex, DealerColumnVisibility.DealerGex,
            static i => i._showDealerGex, static i => Volatile.Read(ref i._dealerGexSnapshot),
            static (i,c,r,p) => i.DrawDealerGexColumn(c,r,p), static (i,c,r,p) => i.DrawDealerGexTooltip(c,r,p),
            static (i,lines,s,n) => i.AppendDealerGexStatus(lines,(DealerGexSnapshot)s,n), NightwatchColumns),
        new(DealerColumnKind.OptionOpenInterest, DealerColumnVisibility.OptionOpenInterest,
            static i => i._showOptionOpenInterest, static i => Volatile.Read(ref i._optionOpenInterestSnapshot),
            static (i,c,r,p) => i.DrawOptionOpenInterestColumn(c,r,p), static (i,c,r,p) => i.DrawOptionOpenInterestTooltip(c,r,p),
            static (i,lines,s,n) => i.AppendOptionOpenInterestStatus(lines,(OptionOpenInterestSnapshot)s,n), IbColumns),
        new(DealerColumnKind.OptionPremiumFlow, DealerColumnVisibility.OptionPremiumFlow,
            static i => i._showOptionPremiumFlow, static i => Volatile.Read(ref i._optionFlowSnapshot),
            static (i,c,r,p) => i.DrawOptionFlowColumn(c,r,p), static (i,c,r,p) => i.DrawOptionFlowTooltip(c,r,p),
            static (i,lines,s,n) => i.AppendOptionPremiumFlowStatus(lines,(OptionFlowSnapshot)s,n), IbColumns)
    ];
    private static readonly DealerColumnCatalog RegisteredColumnCatalog = new(
        RegisteredColumns.Select(static c => new DealerColumnDefinition(c.Kind, c.Visibility)));
    private static readonly ColumnLifecycle[] RegisteredLifecycles =
        RegisteredColumns.Select(static c => c.Lifecycle).Distinct().ToArray();

    private DealerColumnVisibility VisibleDealerColumns
    {
        get
        {
            var visible = DealerColumnVisibility.None;
            foreach (var column in RegisteredColumns) if (column.Enabled(this)) visible |= column.Visibility;
            return visible;
        }
    }

    private void AppendHeatmapStatus(List<string> lines, DealerHeatmapSnapshot heatmapValue, int activeLines)
    {
        var snapshot = heatmapValue;
        var target = snapshot.Target is { } value
            ? $"{value.Ticker} {value.TargetExpiration:yyyy-MM-dd}"
            : "--";
        var asOf = snapshot.Frame == null
            ? "--"
            : FormatUiTime(
                DealerSamplingTime.FromBucketStartUtc(snapshot.Frame.MinuteAtUtc),
                includeSeconds: false);
        var next = snapshot.NextAttemptUtc.HasValue
            ? FormatUiTime(snapshot.NextAttemptUtc, includeSeconds: true)
            : "--";
        lines.Add($"Heatmap: {target}；{StateLabel(snapshot.State)}");
        lines.Add($"Heatmap 数据: {asOf} [{FormatUtcOffsetLabel()}]；节点: {snapshot.Frame?.Cells.Count ?? 0}");
        lines.Add($"Heatmap 下次: {next}");
        lines.Add($"Heatmap 信息: {snapshot.Message}");
    }

    private void AppendDealerGexStatus(List<string> lines, DealerGexSnapshot dealerGexValue, int activeLines)
    {
        var snapshot = dealerGexValue;
        var asOf = snapshot.Frame == null
            ? "--"
            : FormatUiTime(
                DealerSamplingTime.FromBucketStartUtc(snapshot.Frame.SnapshotAtUtc),
                includeSeconds: false);
        var next = snapshot.NextAttemptUtc.HasValue
            ? FormatUiTime(snapshot.NextAttemptUtc, includeSeconds: true)
            : "--";
        lines.Add($"Dealer GEX: {snapshot.Ticker ?? "--"}；{StateLabel(snapshot.State)}");
        lines.Add($"Dealer GEX 数据: {asOf} [{FormatUtcOffsetLabel()}]；价位: {snapshot.Frame?.Nodes.Count ?? 0}");
        lines.Add($"Dealer GEX 下次: {next}");
        lines.Add($"Dealer GEX 信息: {snapshot.Message}");
    }

    private void AppendOptionOpenInterestStatus(List<string> lines, OptionOpenInterestSnapshot optionOi, int activeLines)
    {
        lines.Add($"Option OI: {optionOi.Ticker ?? "--"}；{OptionStateLabel(optionOi.Status)}");
        lines.Add($"Option OI: {optionOi.Expiration:yyyy-MM-dd}；档位 {optionOi.ActiveStrikeCount}/{optionOi.RequestedStrikeCount}");
        lines.Add($"Option OI 信息: {optionOi.Message}");
    }

    private void AppendOptionPremiumFlowStatus(List<string> lines, OptionFlowSnapshot optionFlow, int activeLines)
    {
        var bucket = optionFlow.BucketEndUtc.HasValue
            ? FormatDealerTime(optionFlow.BucketEndUtc.Value)
            : "--";
        lines.Add($"Option Flow: {optionFlow.Ticker ?? "--"}；{OptionStateLabel(optionFlow.Status)}");
        lines.Add($"Option Flow 数据: {bucket}；{optionFlow.IntervalMinutes}m {optionFlow.BucketMode}");
        lines.Add($"Option Flow 行情线: {activeLines}/{_ibOptionMarketDataLineBudget}");
        lines.Add($"Option Flow 信息: {optionFlow.Message}");
        if (optionFlow.BucketMode == OptionFlowBucketMode.Rolling
            && optionFlow.AtmLockedUntilUtc.HasValue)
            lines.Add($"Option Flow ATM: {optionFlow.AtmStrikeUsd}；锁定至 {FormatOptionalOptionTime(optionFlow.AtmLockedUntilUtc)}");
    }


}
