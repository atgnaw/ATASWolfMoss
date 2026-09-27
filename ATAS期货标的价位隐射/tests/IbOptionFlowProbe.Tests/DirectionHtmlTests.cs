using System.Text.Json;
using IbOptionFlowProbe;
using WolfMoss.ATAS.PriceMapping.Core;

namespace IbOptionFlowProbe.Tests;

internal static class DirectionHtmlTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static DirectionReport Fixture(string ticker = "QQQ", bool populated = true)
    {
        var row = new DirectionBucket
        {
            Contract = new(1, ticker, new DateOnly(2026, 9, 16), 600, OptionRight.Call,
                "QQQ", "SMART", 100, "20260916:0930-20260916:1600"),
            Scope = "RegularTrades", Session = "RTH", IntervalMinutes = 1,
            StartUtc = new DateTime(2026, 9, 16, 13, 30, 0, DateTimeKind.Utc),
            EndUtc = new DateTime(2026, 9, 16, 13, 31, 0, DateTimeKind.Utc),
            Events = populated ? 30 : 0, Volume = populated ? 100 : 0
        };
        var settings = new DirectionSettings();
        if (populated)
        {
            row.Primary.Add(1, 60, "STABLE_BUY");
            row.Primary.Add(-1, 30, "STABLE_SELL");
            row.Primary.Add(0, 10, "OFFSET_SENSITIVE");
            row.TrackLargest(20);
        }
        foreach (var scenario in settings.Scenarios)
        {
            var amounts = new DirectionAmounts();
            if (populated) amounts.Add(1, 100, scenario.Method == "MidpointTick" ? "MIDPOINT_TICK" : "AT_ASK");
            row.Scenarios.Add(scenario.Id, amounts);
        }
        return new(settings, new(), [], [row], 1, 0);
    }

    private static JsonDocument Data(string html)
    {
        const string marker = "<script id=\"robust-data\" type=\"application/json\">";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Check(start >= 0, "Fragment must embed its own JSON for offline use");
        start += marker.Length;
        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        return JsonDocument.Parse(html[start..end]);
    }

    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("direction HTML filters methods summary and pagination", () =>
        {
            var html = DirectionHtml.Render(Fixture());
            foreach (var id in new[] { "robust-ticker", "robust-strike", "robust-right", "robust-scope",
                "robust-interval", "robust-method", "robust-alignment", "robust-scenario", "robust-page",
                "robust-prev", "robust-next", "robust-rows" })
                Check(html.Contains("id=\"" + id + "\"", StringComparison.Ordinal), "Missing control " + id);
            Check(html.Contains("value=\"Midpoint\" selected", StringComparison.Ordinal)
                && html.Contains("value=\"Consensus\" selected", StringComparison.Ordinal)
                && html.Contains("value=\"RegularTrades\" selected", StringComparison.Ordinal)
                && html.Contains("value=\"1\" selected", StringComparison.Ordinal), "Defaults must select primary one-minute regular trades");
            foreach (var method in new[] { "AtQuote", "Midpoint", "MidpointTick" })
                Check(html.Contains("value=\"" + method + "\"", StringComparison.Ordinal), "Missing method " + method);
            Check(!html.Contains("value=\"Primary\"", StringComparison.Ordinal), "Consensus must not masquerade as a fourth classifier");
            foreach (var key in new[] { "tendency", "coverage", "bounds", "timing", "unknown", "tolerance" })
                Check(html.Contains("data-help=\"" + key + "\""), "Missing term explanation " + key);
            Check(html.Contains("value=\"0\"", StringComparison.Ordinal) && html.Contains("const pageSize = 50;", StringComparison.Ordinal),
                "Summary rows must be separate and DOM pagination limited to 50 buckets");
            using var data = Data(html);
            Check(data.RootElement.GetProperty("settings").GetProperty("scenarios").GetArrayLength() == 36,
                "All 36 raw scenario settings must remain available");
            return Task.CompletedTask;
        });
        await test("direction HTML escapes hostile report strings and uses safe DOM text", () =>
        {
            const string hostile = "</script><img src=x onerror=alert(1)>";
            var report = Fixture(hostile);
            report.Buckets[0].Primary.Reasons[hostile] = 5;
            var html = DirectionHtml.Render(report);
            Check(!html.Contains(hostile, StringComparison.Ordinal) && !html.Contains("<img", StringComparison.Ordinal),
                "Report values must not break out of JSON or create markup");
            using var data = Data(html);
            Check(data.RootElement.GetProperty("buckets")[0].GetProperty("contract").GetProperty("ticker").GetString() == hostile,
                "Escaping must preserve the original report string");
            Check(html.Contains("textContent", StringComparison.Ordinal) && !html.Contains("innerHTML", StringComparison.Ordinal),
                "Dynamic report strings must enter the DOM only as text");
            return Task.CompletedTask;
        });
        await test("direction HTML preserves nulls and explains uncertainty offline", () =>
        {
            var html = DirectionHtml.Render(Fixture(populated: false));
            using var data = Data(html);
            var row = data.RootElement.GetProperty("buckets")[0];
            Check(!row.GetProperty("assessable").GetBoolean()
                && row.GetProperty("highestPassedPressure").ValueKind == JsonValueKind.Null
                && row.GetProperty("primary").GetProperty("metrics").GetProperty("tendency").ValueKind == JsonValueKind.Null,
                "Missing data and ineligible pressure claims must remain null");
            foreach (var text in new[] { "不是概率", "抗误判余量", "MIDPOINT_TICK", "decisions-*.jsonl", "原始序号", "Δ" })
                Check(html.Contains(text, StringComparison.Ordinal), "Missing evidence explanation " + text);
            Check(!html.Contains("NaN", StringComparison.Ordinal) && !html.Contains("fetch(", StringComparison.Ordinal)
                && !html.Contains("XMLHttpRequest", StringComparison.Ordinal) && !html.Contains("<script src", StringComparison.Ordinal)
                && !html.Contains("https://", StringComparison.Ordinal) && !html.Contains("http://", StringComparison.Ordinal),
                "Standalone report must not emit invalid numbers or load network resources");
            return Task.CompletedTask;
        });
    }
}
