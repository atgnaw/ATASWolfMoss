using System.Text;

using WolfMoss.ATAS.PriceMapping.Core;

if (args.Contains("--live-spx", StringComparer.OrdinalIgnoreCase))
{
    await RunLiveSpxCheck();
    return;
}

var tests = new (string Name, Action Run)[]
{
    ("Instrument pair resolution", TestPairResolution),
    ("Mapping math", TestMappingMath),
    ("Minute close matching", TestMinuteClose),
    ("UTC to ATAS market time alignment", TestMarketTimeAlignment),
    ("Nice tick generation", TestTicks),
    ("Yahoo completed minute parsing", TestYahooCompletedMinute),
    ("Yahoo stale/duplicate rejection", TestYahooRejections),
    ("Yahoo last SPX close acceptance", TestYahooLastSpxClose),
    ("Yahoo closed SPX session handling", TestYahooClosedSpxSession),
    ("Yahoo malformed structure handling", TestYahooMalformedStructure),
    ("Automatic/manual ratio priority", TestRatioPriority)
};

var failures = new List<string>();

foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{test.Name}: {ex.Message}");
        Console.WriteLine($"FAIL  {test.Name}: {ex.Message}");
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count} test(s) failed.");
    Environment.ExitCode = 1;
}
else
{
    Console.WriteLine($"All {tests.Length} tests passed.");
}

static void TestPairResolution()
{
    Assert(InstrumentPairResolver.TryResolve(PairMode.Auto, "/NQU6", out var nq));
    Equal("QQQ", nq.ReferenceSymbol);
    Assert(InstrumentPairResolver.TryResolve(PairMode.Auto, "MNQ 09-26", out var mnq));
    Equal("QQQ", mnq.ReferenceSymbol);
    Assert(InstrumentPairResolver.TryResolve(PairMode.Auto, "ESZ26", out var es));
    Equal("SPX", es.ReferenceSymbol);
    Assert(InstrumentPairResolver.TryResolve(PairMode.Auto, "MES@CME", out var mes));
    Equal("SPX", mes.ReferenceSymbol);
    Assert(!InstrumentPairResolver.TryResolve(PairMode.Auto, "CLQ26", out _));
    Assert(InstrumentPairResolver.TryResolve(PairMode.EsSpx, "CLQ26", out var forced));
    Equal("SPX", forced.ReferenceSymbol);
}

static void TestMappingMath()
{
    Assert(MappingMath.TryCalculateRatio(24_000m, 600m, out var ratio));
    Equal(40m, ratio);
    Equal(601m, MappingMath.ToReferencePrice(24_040m, ratio));
    Equal(24_040m, MappingMath.ToFuturesPrice(601m, ratio));
    Assert(!MappingMath.TryCalculateRatio(1m, 0m, out _));
}

static void TestMinuteClose()
{
    var minute = new DateTime(2026, 7, 27, 9, 30, 0);
    var trades = new[]
    {
        new MarketTradeSample(minute.AddSeconds(55), 101m),
        new MarketTradeSample(minute.AddSeconds(5), 99m),
        new MarketTradeSample(minute.AddMinutes(1), 102m),
        new MarketTradeSample(minute.AddSeconds(59), 101.25m)
    };

    Assert(MinuteCloseMatcher.TryFindClose(trades, minute, out var close));
    Equal(101.25m, close);
}

static void TestMarketTimeAlignment()
{
    var utcMinute = new DateTime(2026, 7, 27, 14, 30, 0, DateTimeKind.Utc);
    var utcNow = new DateTime(2026, 7, 27, 14, 45, 10, DateTimeKind.Utc);
    var marketNow = new DateTime(2026, 7, 27, 9, 45, 9, DateTimeKind.Unspecified);
    var result = MarketTimeAlignment.UtcMinuteToMarketTime(
        utcMinute,
        marketNow,
        utcNow);

    Equal(new DateTime(2026, 7, 27, 9, 30, 0), result);
    Equal(DateTimeKind.Unspecified, result.Kind);
}

static void TestTicks()
{
    var step = TickGenerator.CalculateNiceStep(590m, 610m, 600);
    Assert(step > 0m);
    var ticks = TickGenerator.EnumerateTicks(590m, 610m, step).ToArray();
    Assert(ticks.Length is >= 2 and <= 200);
    Assert(ticks.SequenceEqual(ticks.OrderBy(x => x)));
}

static void TestYahooCompletedMinute()
{
    var t0 = new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);
    var t1 = t0.AddMinutes(1);
    var json = CreateYahooJson(
        [t0.ToUnixTimeSeconds(), t1.ToUnixTimeSeconds()],
        ["600.25", "601.50"]);

    var result = YahooChartParser.ParseLatestCompleted(
        Encoding.UTF8.GetBytes(json),
        "QQQ",
        t1.UtcDateTime.AddSeconds(10),
        TimeSpan.FromMinutes(20));

    Equal(t0.UtcDateTime, result.MinuteStartUtc);
    Equal(600.25m, result.Close);
}

static void TestYahooRejections()
{
    var t0 = new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);
    var json = CreateYahooJson([t0.ToUnixTimeSeconds()], ["600.25"]);
    var bytes = Encoding.UTF8.GetBytes(json);

    ThrowsYahoo("StaleQuote", () => YahooChartParser.ParseLatestCompleted(
        bytes,
        "QQQ",
        t0.UtcDateTime.AddHours(1),
        TimeSpan.FromMinutes(20)));

    ThrowsYahoo("DuplicateQuote", () => YahooChartParser.ParseLatestCompleted(
        bytes,
        "QQQ",
        t0.UtcDateTime.AddMinutes(2),
        TimeSpan.FromMinutes(20),
        t0.UtcDateTime));
}

static void TestYahooLastSpxClose()
{
    var closeMinute = new DateTimeOffset(2026, 7, 24, 19, 59, 0, TimeSpan.Zero);
    var json = CreateYahooJson(
        [closeMinute.ToUnixTimeSeconds()],
        ["7411.98"]);

    var result = YahooChartParser.ParseLatestCompleted(
        Encoding.UTF8.GetBytes(json),
        "^GSPC",
        new DateTime(2026, 7, 27, 11, 30, 0, DateTimeKind.Utc),
        TimeSpan.FromMinutes(20),
        allowExpiredQuote: true);

    Equal(closeMinute.UtcDateTime, result.MinuteStartUtc);
    Equal(7411.98m, result.Close);
}

static void TestYahooClosedSpxSession()
{
    const string json =
        """
        {
          "chart": {
            "result": [{
              "meta": { "symbol": "^GSPC" },
              "timestamp": null,
              "indicators": {
                "quote": [{
                  "close": null,
                  "open": null,
                  "high": null,
                  "low": null,
                  "volume": null
                }]
              }
            }],
            "error": null
          }
        }
        """;

    var error = ThrowsYahoo(
        "NoMinuteSeries",
        () => YahooChartParser.ParseLatestCompleted(
            Encoding.UTF8.GetBytes(json),
            "^GSPC",
            new DateTime(2026, 7, 27, 11, 30, 0, DateTimeKind.Utc),
            TimeSpan.FromMinutes(20)));

    Assert(error.Message.StartsWith("SPX ", StringComparison.Ordinal));
}

static void TestYahooMalformedStructure()
{
    const string json = """{ "notChart": {} }""";

    ThrowsYahoo("InvalidJson", () => YahooChartParser.ParseLatestCompleted(
        Encoding.UTF8.GetBytes(json),
        "QQQ",
        DateTime.UtcNow,
        TimeSpan.FromMinutes(20)));
}

static void TestRatioPriority()
{
    var pair = new InstrumentPair("NQ", "QQQ", "QQQ", 2);
    var mapping = new MappingSnapshot(
        pair,
        DateTime.UtcNow,
        DateTime.Now,
        24_000m,
        600m,
        40m,
        DateTime.Now);

    Equal(40m, MappingMath.SelectEffectiveRatio(MappingMode.Automatic, 39m, mapping));
    Equal(39m, MappingMath.SelectEffectiveRatio(MappingMode.Manual, 39m, mapping));
    Equal(39m, MappingMath.SelectEffectiveRatio(MappingMode.Automatic, 39m, null));
    Assert(MappingMath.SelectEffectiveRatio(MappingMode.Automatic, 0m, null) is null);
}

static string CreateYahooJson(long[] timestamps, string[] closes)
{
    var timestampText = string.Join(',', timestamps);
    var closeText = string.Join(',', closes);
    return $$"""
        {
          "chart": {
            "result": [{
              "timestamp": [{{timestampText}}],
              "indicators": { "quote": [{ "close": [{{closeText}}] }] }
            }],
            "error": null
          }
        }
        """;
}

static async Task RunLiveSpxCheck()
{
    const string url =
        "https://query1.finance.yahoo.com/v8/finance/chart/%5EGSPC"
        + "?range=5d&interval=1m&includePrePost=false&events=div%2Csplits";
    using var client = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(10)
    };
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 ATAS-FuturesReferencePriceAxis/1.0");
    var json = await client.GetByteArrayAsync(url);
    var result = YahooChartParser.ParseLatestCompleted(
        json,
        "^GSPC",
        DateTime.UtcNow,
        TimeSpan.FromMinutes(20),
        allowExpiredQuote: true);

    Console.WriteLine(
        $"LIVE  {result.Symbol} {result.MinuteStartUtc:yyyy-MM-dd HH:mm:ss} UTC"
        + $" close={result.Close}");
}

static void Assert(bool condition, string message = "Assertion failed")
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}");
}

static YahooDataException ThrowsYahoo(string expectedCode, Action action)
{
    try
    {
        action();
    }
    catch (YahooDataException ex)
    {
        Equal(expectedCode, ex.Code);
        return ex;
    }

    throw new InvalidOperationException(
        $"Expected {nameof(YahooDataException)} with code {expectedCode}");
}
