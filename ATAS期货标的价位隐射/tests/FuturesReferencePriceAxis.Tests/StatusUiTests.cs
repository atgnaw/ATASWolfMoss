using System.Collections;
using System.Drawing;
using System.Reflection;
using WolfMoss.ATAS.PriceMapping.Core;

internal static class StatusUiTests
{
    private static readonly string[] Names = { "ShowMappingStatus", "ShowHeatmapStatus", "ShowDealerGexStatus", "ShowOptionOiStatus", "ShowOptionFlowStatus", "ShowPerformanceStatus" };
    private static readonly DateTime Start = new(2026, 9, 9, 13, 30, 0, DateTimeKind.Utc);
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static object Get(object o, string name) => o.GetType().GetField(name, Private)!.GetValue(o)!;
    private static void Set(object o, string name, object value) => o.GetType().GetField(name, Private)!.SetValue(o, value);
    private static object Create() => Activator.CreateInstance(PerformanceDiagnosticTests.LoadPro()
        .GetType("WolfMoss.ATAS.PriceMapping.FuturesReferencePriceAxisDealerHeatmapIndicator", true)!)!;

    public static void SettingsAndIsolation()
    {
        var indicator = Create(); var type = indicator.GetType();
        foreach (var name in Names)
        {
            var property = type.GetProperty(name)!;
            Check(property.PropertyType == typeof(bool) && (bool)property.GetValue(indicator)!, $"Default enabled: {name}");
            Check(type.BaseType!.GetProperty(name) == null, "New setting must not leak to standard shared base");
            Check(property.GetCustomAttribute<System.ComponentModel.DataAnnotations.DisplayAttribute>() != null, "Visible property metadata");
        }
        var width = type.GetProperty("StatusPanelWidth")!;
        Check((int)width.GetValue(indicator)! == 480, "Existing width preserved by default");
        width.SetValue(indicator, 50); Check((int)width.GetValue(indicator)! == 180, "Width min");
        width.SetValue(indicator, 5000); Check((int)width.GetValue(indicator)! == 1600, "Width max");

        var fixture = new PerformanceBaseline.Fixture(PerformanceDiagnosticTests.LoadPro(), OptionFlowBucketMode.Rolling, 1, "QQQ", true);
        indicator = fixture.Indicator; type = indicator.GetType();
        Set(indicator, "_optionDataGeneration", 42L);
        var frame = Get(indicator, "_optionFlowSnapshot"); var samples = Get(indicator, "_rollingFlow");
        var performance = Get(indicator, "_performance");
        Set(indicator, "_performanceDiagnosticsMode", Enum.ToObject(type.GetProperty("PerformanceDiagnosticsMode")!.PropertyType, 2));
        using var source = new CancellationTokenSource(); Set(indicator, "_optionScheduleCancellation", source);
        for (var i = 0; i < 4; i++) foreach (var name in Names) type.GetProperty(name)!.SetValue(indicator, i % 2 == 0);
        type.GetProperty("StatusPanelWidth")!.SetValue(indicator, 900);
        Check((long)Get(indicator, "_optionDataGeneration") == 42 && !source.IsCancellationRequested, "Visibility cannot restart subscription");
        Check(ReferenceEquals(frame, Get(indicator, "_optionFlowSnapshot")) && ReferenceEquals(samples, Get(indicator, "_rollingFlow")), "Flow remains intact");
        Check(ReferenceEquals(performance, Get(indicator, "_performance")) && (int)Convert.ChangeType(Get(indicator, "_performanceDiagnosticsMode"), typeof(int)) == 2, "Record remains enabled");
    }

    public static void CategoryCombinations()
    {
        var indicator = Create(); var type = indicator.GetType();
        Set(indicator, "_showOptionOpenInterest", true); Set(indicator, "_showOptionPremiumFlow", true);
        Set(indicator, "_performanceLines", new[] { "PERF test" });
        var read = type.GetMethod("GetEditionStatusLines", Private)!;
        string[] categories = { "Heatmap", "Dealer GEX", "Option OI", "Option Flow", "PERF" };
        for (var mask = 0; mask < 64; mask++)
        {
            for (var i = 0; i < Names.Length; i++) type.GetProperty(Names[i])!.SetValue(indicator, (mask & (1 << i)) != 0);
            var lines = ((IEnumerable)read.Invoke(indicator, null)!).Cast<string>().ToArray();
            for (var i = 0; i < categories.Length; i++)
                Check(lines.Any(line => line.StartsWith(categories[i], StringComparison.Ordinal)) == ((mask & (1 << (i + 1))) != 0), "Category filtering " + categories[i]);
            var first = read.Invoke(indicator, null); var second = read.Invoke(indicator, null);
            Check(ReferenceEquals(first, second), "Unchanged status cache reused");
        }
        Set(indicator, "_showDealerHeatmap", false); type.GetProperty("ShowHeatmapStatus")!.SetValue(indicator, true);
        Check(!((IEnumerable)read.Invoke(indicator, null)!).Cast<string>().Any(line => line.StartsWith("Heatmap")), "Feature off remains absent");
    }

    public static void WidthAndPlacement()
    {
        var lines = new[] { "中文 ABCDEF 中文", "第二行需要换行", "tail" };
        var wrapped = StatusUiPresentation.Wrap(lines, 5, 100, text => text.Length);
        Check(string.Concat(wrapped) == string.Concat(lines) && wrapped.All(line => line.Length <= 5), "Wrap preserves text");
        var clipped = StatusUiPresentation.Wrap(lines, 5, 2, text => text.Length);
        Check(clipped.Length == 2 && clipped[^1].StartsWith("…"), "Vertical clipping explicitly indicated");
        var region = new Rectangle(20, 30, 1000, 700);
        foreach (var height in new[] { 0, 150, 600 }) foreach (var offset in new[] { -10000, 0, 10000 })
        {
            var panel = StatusUiPresentation.Place(region, 250, 800, offset, offset, height, true);
            var footerTop = panel.Bottom + (height > 0 ? StatusUiPresentation.CountdownGap : 0);
            Check(panel.Width == 734 && panel.Left >= region.Left + 4 && panel.Right <= region.Right - 4, "Width and x clamped");
            Check(footerTop >= panel.Bottom && footerTop + StatusUiPresentation.CountdownHeight <= region.Bottom - 4, "Countdown below/outside panel and on chart");
        }
        Check(StatusUiPresentation.Wrap(Array.Empty<string>(), 100, 5, text => text.Length).Length == 0, "No empty status box");
    }

    public static void CountdownBoundaries()
    {
        var segments = new[] { new OptionTradingSegment(Start, Start.AddHours(2)) };
        foreach (var minutes in new[] { 1, 3, 5, 10 })
        {
            string Text(DateTime now) => FlowCountdownPresentation.Text(now, OptionFlowBucketMode.PreviousCompletedFixed, minutes, segments, null, OptionDataStatus.Live);
            Check(Text(Start).EndsWith($"{minutes:00}:00"), "Full countdown at bucket start");
            Check(Text(Start.AddMinutes(minutes).AddMilliseconds(-100)).EndsWith("00:01"), "Round remaining time up");
            Check(Text(Start.AddMinutes(minutes)).EndsWith($"{minutes:00}:00"), "Current forming bucket, not completed frame");
            Check(Text(Start.AddHours(2)) == $"Flow {minutes}m：休市", "Session end exclusive");
        }
        var tail = new[] { new OptionTradingSegment(Start, Start.AddMinutes(8)) };
        Check(FlowCountdownPresentation.Text(Start.AddMinutes(6), OptionFlowBucketMode.PreviousCompletedFixed, 5, tail, null, OptionDataStatus.Live).Contains("不足完整桶"), "No false countdown for discarded partial tail");
        Check(FlowCountdownPresentation.Text(Start, OptionFlowBucketMode.Rolling, 5, segments, Start.AddMinutes(1), OptionDataStatus.Partial).EndsWith("ATM 01:00"), "Rolling uses common minimum cadence, not own window");
        Check(FlowCountdownPresentation.Text(Start, OptionFlowBucketMode.Rolling, 5, segments, Start, OptionDataStatus.Live).Contains("等待 ATM"), "Expired coordinator deadline is not extrapolated");
        Check(FlowCountdownPresentation.Text(Start, OptionFlowBucketMode.Rolling, 5, segments, Start.AddMinutes(1), OptionDataStatus.Frozen).Contains("暂停"), "Frozen not presented as healthy countdown");
        Check(FlowCountdownPresentation.Text(Start, OptionFlowBucketMode.Rolling, 5, null, null, OptionDataStatus.Connecting).Contains("等待交易时段"), "No guessed session");
        var gth = new[] { new OptionTradingSegment(Start.AddHours(-13), Start.AddHours(-5)), new OptionTradingSegment(Start, Start.AddHours(2)) };
        Check(FlowCountdownPresentation.Text(Start.AddHours(-6), OptionFlowBucketMode.PreviousCompletedFixed, 3, gth, null, OptionDataStatus.Live).EndsWith("03:00"), "GTH anchor");
        Check(FlowCountdownPresentation.Text(Start.AddHours(-4), OptionFlowBucketMode.PreviousCompletedFixed, 3, gth, null, OptionDataStatus.Live).Contains("休市"), "Maintenance gap");
    }

    public static void CountdownLifecycle()
    {
        var indicator = Create(); var type = indicator.GetType();
        type.BaseType!.GetField("_initialized", Private)!.SetValue(indicator, true);
        var restart = type.GetMethod("RestartFlowCountdownClock", Private)!;
        var stop = type.GetMethod("StopFlowCountdownClock", Private)!;
        restart.Invoke(indicator, null);
        Check(Get(indicator, "_countdownCancellation") == null, "Flow off has no timer");
        Set(indicator, "_showOptionPremiumFlow", true);
        restart.Invoke(indicator, null);
        var first = (Task)Get(indicator, "_countdownTask");
        restart.Invoke(indicator, null);
        var second = (Task)Get(indicator, "_countdownTask");
        stop.Invoke(indicator, null);
        Check(Task.WaitAll(new[] { first, second }, 3000), "Timers stop promptly without unobserved cancellation");
        Check(first.IsCompletedSuccessfully && second.IsCompletedSuccessfully, "Normal shutdown does not fault");
    }
}
