using System.Diagnostics;
using WolfMoss.ATAS.PriceMapping;
using WolfMoss.ATAS.PriceMapping.Core;

namespace IbOptionFlowProbe;

internal static class ProbeDemo
{
    public static async Task<string> CreateAsync(string parent)
    {
        var directory = Path.Combine(Path.GetFullPath(parent), "synthetic-demo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var run = new Guid("AA801111-DAA0-4000-9000-000000000002");
        var start = new DateTime(2026, 9, 16, 13, 30, 0, DateTimeKind.Utc);
        var profiles = new[] { ("QQQ", 100m, "QQQ"), ("SPX", 7000m, "SPXW") };
        var contracts = profiles.SelectMany((p, j) => new[] { OptionRight.Call, OptionRight.Put }.Select(r =>
            new OptionContractDescriptor(100 + j * 2 + (int)r, p.Item1, new DateOnly(2026, 9, 16), p.Item2, r,
                p.Item3, p.Item1 == "QQQ" ? "SMART" : "CBOE", 100, "20260916:0930-20260916:1600"))).ToArray();
        var selections = contracts.Select(c => new ProbeContract(c, new(start, start.AddHours(6.5)), "RTH", c.StrikeUsd, c.StrikeUsd)).ToArray();
        var manifest = new ProbeManifest(1, run, "0.5.1", start, Stopwatch.Frequency,
            new ProbeConfig { ReferenceSpots = new() { ["QQQ"] = 100, ["SPX"] = 7000 } }, selections, true);
        await CaptureRunner.SaveNewAsync(Path.Combine(directory, "manifest.json"), manifest);
        await CaptureRunner.SaveNewAsync(Path.Combine(directory, "contracts.json"), selections);
        await using var writer = new RawCaptureWriter(directory, run);
        long sequence = 0;
        void Emit(OptionContractDescriptor c, double ms, string kind, int? field = null, string? value = null, int? dataType = null)
        {
            var utc = start.AddMilliseconds(ms);
            writer.Publish(new(++sequence, utc, (long)(ms / 1000 * Stopwatch.Frequency), kind,
                (int)c.ConId, c, field, value, MarketDataType: dataType));
        }
        foreach (var c in contracts) Emit(c, 0, "marketDataType", dataType: 1);
        for (var second = 0; second < 720; second++)
        {
            foreach (var c in contracts)
            {
                if (second % 2 == 0) { Emit(c, second * 1000, "tickPrice", 1, "1.00"); Emit(c, second * 1000, "tickPrice", 2, "1.10"); }
            }
            foreach (var c in contracts)
            {
                var price = second % 5 == 0 ? "1.05" : second % 3 == 0 ? "1.00" : "1.10";
                var source = new DateTimeOffset(start.AddSeconds(second).AddMilliseconds(40)).ToUnixTimeMilliseconds();
                var size = second % 13 == 0 ? 1 : 2;
                foreach (var field in new[] { 48, 77 }) Emit(c, second * 1000 + 50, "tickString", field,
                    $"{price};{size};{source};{100 + second * 2};1.05;true");
            }
        }
        await writer.DisposeAsync();
        await CaptureRunner.SaveNewAsync(Path.Combine(directory, "capture-summary.json"),
            new CaptureSummary(run, start.AddMinutes(12), "COMPLETED", writer.Dropped == 0,
                writer.Accepted, writer.Written, writer.Dropped, writer.QueuePeak, writer.Bytes, 0, writer.Failure, start));
        return directory;
    }
}
