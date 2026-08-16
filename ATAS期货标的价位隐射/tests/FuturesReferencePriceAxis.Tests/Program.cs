using System.Text;
using System.Text.Json;

using WolfMoss.ATAS.PriceMapping.Core;

if (args.Contains("--live-spx", StringComparer.OrdinalIgnoreCase))
{
    await RunLiveSpxCheck();
    return;
}

if (args.Contains("--live-qqq-nasdaq", StringComparer.OrdinalIgnoreCase))
{
    await RunLiveNasdaqQqqCheck();
    return;
}

if (args.Contains("--live-qqq-yahoo", StringComparer.OrdinalIgnoreCase))
{
    await RunLiveYahooQqqCheck();
    return;
}

if (args.Contains("--live-spx-marketwatch", StringComparer.OrdinalIgnoreCase))
{
    await RunLiveMarketWatchSpxCheck();
    return;
}

var tests = new (string Name, Action Run)[]
{
    ("Instrument pair resolution", TestPairResolution),
    ("Mapping math", TestMappingMath),
    ("Minute close matching", TestMinuteClose),
    ("UTC to ATAS market time alignment", TestMarketTimeAlignment),
    ("UI UTC offset conversion", TestUiUtcOffset),
    ("Nice tick generation", TestTicks),
    ("Yahoo completed minute parsing", TestYahooCompletedMinute),
    ("Yahoo stale/duplicate rejection", TestYahooRejections),
    ("Yahoo last SPX close acceptance", TestYahooLastSpxClose),
    ("Yahoo closed SPX session handling", TestYahooClosedSpxSession),
    ("Yahoo malformed structure handling", TestYahooMalformedStructure),
    ("Nasdaq QQQ completed minute parsing", TestNasdaqQqqCompletedMinute),
    ("Nasdaq QQQ stale/duplicate rejection", TestNasdaqQqqRejections),
    ("QQQ closed-session calendar", TestQqqClosedSessionCalendar),
    ("QQQ closed-session quote acceptance", TestQqqClosedSessionQuoteAcceptance),
    ("NQ trading-session calendar", TestNqTradingSessionCalendar),
    ("QQQ/NQ common-minute selection", TestQqqNqCommonMinuteSelection),
    ("MarketWatch SPX completed minute parsing", TestMarketWatchSpxCompletedMinute),
    ("MarketWatch SPX closed-session handling", TestMarketWatchSpxClosedSession),
    ("Startup retry backoff", TestRetryBackoff),
    ("Automatic/manual ratio priority", TestRatioPriority),
    ("Process-wide quote request coordination", TestQuoteCoordinator),
    ("Coordinator cancellation and late completion", TestCoordinatorCancellation),
    ("Combined source diagnostics and retry classification", TestSourceDiagnostics),
    ("Per-instance quote validation", TestReferenceQuoteValidation),
    ("New York and Chicago DST alignment", TestDstMarketTimeAlignment),
    ("Unified US session boundary rules", TestUnifiedSessionBoundaries),
    ("Axis layout cache invalidation", TestAxisLayoutCache),
    ("Public settings compatibility baseline", TestPublicSettingsCompatibility)
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

static void TestUiUtcOffset()
{
    var utc = new DateTime(2026, 8, 16, 18, 1, 30, DateTimeKind.Utc);
    Equal(
        new DateTime(2026, 8, 17, 2, 1, 30, DateTimeKind.Utc),
        UiTimeZoneFormatter.ConvertFromUtc(utc, 8m));
    Equal(
        new DateTime(2026, 8, 16, 12, 31, 30, DateTimeKind.Utc),
        UiTimeZoneFormatter.ConvertFromUtc(utc, -5.5m));
    Equal("UTC+8", UiTimeZoneFormatter.FormatOffsetLabel(8m));
    Equal("UTC-5.5", UiTimeZoneFormatter.FormatOffsetLabel(-5.5m));
    Equal("UTC+0", UiTimeZoneFormatter.FormatOffsetLabel(0m));
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
        [t0.AddSeconds(59).ToUnixTimeSeconds(), t1.ToUnixTimeSeconds()],
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

static void TestNasdaqQqqCompletedMinute()
{
    var t0 = new DateTimeOffset(2026, 8, 6, 13, 14, 0, TimeSpan.Zero);
    var t1 = t0.AddMinutes(1);
    var json = CreateNasdaqJson(
        [
            NasdaqWallClockMilliseconds(2026, 8, 6, 9, 15),
            NasdaqWallClockMilliseconds(2026, 8, 6, 9, 14)
        ],
        ["710.25", "710.10"]);

    var result = NasdaqQqqParser.ParseLatestCompleted(
        Encoding.UTF8.GetBytes(json),
        t1.UtcDateTime.AddSeconds(30),
        TimeSpan.FromMinutes(20));

    Equal(t0.UtcDateTime, result.MinuteStartUtc);
    Equal(710.10m, result.Close);
    Equal("Nasdaq", result.Source);

    var winterJson = CreateNasdaqJson(
        [NasdaqWallClockMilliseconds(2026, 1, 6, 9, 14)],
        ["690.50"]);
    var winterResult = NasdaqQqqParser.ParseLatestCompleted(
        Encoding.UTF8.GetBytes(winterJson),
        new DateTime(2026, 1, 6, 14, 16, 0, DateTimeKind.Utc),
        TimeSpan.FromMinutes(20));

    Equal(
        new DateTime(2026, 1, 6, 14, 14, 0, DateTimeKind.Utc),
        winterResult.MinuteStartUtc);
}

static void TestNasdaqQqqRejections()
{
    var t0 = new DateTimeOffset(2026, 8, 6, 13, 14, 0, TimeSpan.Zero);
    var json = CreateNasdaqJson(
        [NasdaqWallClockMilliseconds(2026, 8, 6, 9, 14)],
        ["710.10"]);
    var bytes = Encoding.UTF8.GetBytes(json);

    ThrowsNasdaq("StaleQuote", () => NasdaqQqqParser.ParseLatestCompleted(
        bytes,
        t0.UtcDateTime.AddHours(1),
        TimeSpan.FromMinutes(20)));

    ThrowsNasdaq("DuplicateQuote", () => NasdaqQqqParser.ParseLatestCompleted(
        bytes,
        t0.UtcDateTime.AddMinutes(2),
        TimeSpan.FromMinutes(20),
        t0.UtcDateTime));
}

static void TestQqqClosedSessionCalendar()
{
    Assert(UsEquitySessionCalendar.IsQqqExtendedSessionClosed(
        new DateTime(2026, 8, 16, 16, 52, 0, DateTimeKind.Utc)));
    Assert(!UsEquitySessionCalendar.IsQqqExtendedSessionClosed(
        new DateTime(2026, 8, 17, 8, 1, 0, DateTimeKind.Utc)));
    Assert(!UsEquitySessionCalendar.IsQqqExtendedSessionClosed(
        new DateTime(2026, 8, 17, 23, 59, 0, DateTimeKind.Utc)));
    Assert(UsEquitySessionCalendar.IsQqqExtendedSessionClosed(
        new DateTime(2026, 8, 18, 0, 0, 0, DateTimeKind.Utc)));
    Assert(UsEquitySessionCalendar.IsQqqExtendedSessionClosed(
        new DateTime(2026, 7, 3, 14, 0, 0, DateTimeKind.Utc)));
}

static void TestQqqClosedSessionQuoteAcceptance()
{
    var lastClose = new DateTimeOffset(2026, 8, 14, 23, 59, 0, TimeSpan.Zero);
    var nowUtc = new DateTime(2026, 8, 16, 16, 52, 0, DateTimeKind.Utc);
    var yahooJson = CreateYahooJson(
        [lastClose.AddSeconds(59).ToUnixTimeSeconds()],
        ["730.85"]);
    var yahooResult = YahooChartParser.ParseLatestCompleted(
        Encoding.UTF8.GetBytes(yahooJson),
        "QQQ",
        nowUtc,
        TimeSpan.FromMinutes(20),
        allowExpiredQuote: true);

    Equal(lastClose.UtcDateTime, yahooResult.MinuteStartUtc);
    Equal(730.85m, yahooResult.Close);

    var nasdaqJson = CreateNasdaqJson(
        [NasdaqWallClockMilliseconds(2026, 8, 14, 19, 59)],
        ["730.85"]);
    var nasdaqResult = NasdaqQqqParser.ParseLatestCompleted(
        Encoding.UTF8.GetBytes(nasdaqJson),
        nowUtc,
        TimeSpan.FromMinutes(20),
        allowExpiredQuote: true);

    Equal(lastClose.UtcDateTime, nasdaqResult.MinuteStartUtc);
    Equal(730.85m, nasdaqResult.Close);
}

static void TestNqTradingSessionCalendar()
{
    Assert(NqTradingSessionCalendar.IsOpen(
        new DateTime(2026, 8, 14, 20, 59, 0, DateTimeKind.Utc)));
    Assert(!NqTradingSessionCalendar.IsOpen(
        new DateTime(2026, 8, 14, 21, 0, 0, DateTimeKind.Utc)));
    Assert(!NqTradingSessionCalendar.IsOpen(
        new DateTime(2026, 8, 14, 23, 59, 0, DateTimeKind.Utc)));
    Assert(!NqTradingSessionCalendar.IsOpen(
        new DateTime(2026, 8, 13, 20, 20, 0, DateTimeKind.Utc)));
    Assert(!NqTradingSessionCalendar.IsOpen(
        new DateTime(2026, 8, 13, 21, 30, 0, DateTimeKind.Utc)));
    Assert(NqTradingSessionCalendar.IsOpen(
        new DateTime(2026, 8, 13, 22, 0, 0, DateTimeKind.Utc)));
    Assert(!NqTradingSessionCalendar.IsOpen(
        new DateTime(2026, 8, 16, 21, 59, 0, DateTimeKind.Utc)));
    Assert(NqTradingSessionCalendar.IsOpen(
        new DateTime(2026, 8, 16, 22, 0, 0, DateTimeKind.Utc)));
}

static void TestQqqNqCommonMinuteSelection()
{
    var commonMinute = new DateTimeOffset(2026, 8, 14, 20, 59, 0, TimeSpan.Zero);
    var qqqOnlyMinute = new DateTimeOffset(2026, 8, 14, 23, 59, 0, TimeSpan.Zero);
    var nowUtc = new DateTime(2026, 8, 16, 16, 52, 0, DateTimeKind.Utc);
    var yahooJson = CreateYahooJson(
        [commonMinute.ToUnixTimeSeconds(), qqqOnlyMinute.ToUnixTimeSeconds()],
        ["729.25", "730.85"]);
    var yahooResult = YahooChartParser.ParseLatestCompleted(
        Encoding.UTF8.GetBytes(yahooJson),
        "QQQ",
        nowUtc,
        TimeSpan.FromMinutes(20),
        allowExpiredQuote: true,
        requireNqTradableMinute: true);

    Equal(commonMinute.UtcDateTime, yahooResult.MinuteStartUtc);
    Equal(729.25m, yahooResult.Close);

    var nasdaqJson = CreateNasdaqJson(
        [
            NasdaqWallClockMilliseconds(2026, 8, 14, 19, 59),
            NasdaqWallClockMilliseconds(2026, 8, 14, 16, 59)
        ],
        ["730.85", "729.25"]);
    var nasdaqResult = NasdaqQqqParser.ParseLatestCompleted(
        Encoding.UTF8.GetBytes(nasdaqJson),
        nowUtc,
        TimeSpan.FromMinutes(20),
        allowExpiredQuote: true,
        requireNqTradableMinute: true);

    Equal(commonMinute.UtcDateTime, nasdaqResult.MinuteStartUtc);
    Equal(729.25m, nasdaqResult.Close);
}

static void TestMarketWatchSpxCompletedMinute()
{
    var completed = new DateTimeOffset(2026, 8, 14, 19, 59, 0, TimeSpan.Zero);
    var current = completed.AddMinutes(1);
    var json = CreateMarketWatchJson(
        [current.ToUnixTimeMilliseconds(), completed.ToUnixTimeMilliseconds()],
        ["7785.73", "7784.90"]);

    var result = MarketWatchSpxParser.ParseLatestCompleted(
        Encoding.UTF8.GetBytes(json),
        current.UtcDateTime.AddSeconds(30),
        TimeSpan.FromMinutes(20));

    Equal(completed.UtcDateTime, result.MinuteStartUtc);
    Equal(7784.90m, result.Close);
    Equal("MarketWatch", result.Source);
}

static void TestMarketWatchSpxClosedSession()
{
    var lastClose = new DateTimeOffset(2026, 8, 14, 20, 0, 0, TimeSpan.Zero);
    var json = CreateMarketWatchJson(
        [lastClose.ToUnixTimeMilliseconds()],
        ["7785.73"]);
    var bytes = Encoding.UTF8.GetBytes(json);

    var result = MarketWatchSpxParser.ParseLatestCompleted(
        bytes,
        new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc),
        TimeSpan.FromMinutes(20),
        allowExpiredQuote: true);

    Equal(lastClose.UtcDateTime, result.MinuteStartUtc);
    Equal(7785.73m, result.Close);

    ThrowsMarketWatch("StaleQuote", () => MarketWatchSpxParser.ParseLatestCompleted(
        bytes,
        lastClose.UtcDateTime.AddHours(1),
        TimeSpan.FromMinutes(20),
        allowExpiredQuote: false));

    ThrowsMarketWatch("DuplicateQuote", () => MarketWatchSpxParser.ParseLatestCompleted(
        bytes,
        lastClose.UtcDateTime.AddHours(1),
        TimeSpan.FromMinutes(20),
        lastClose.UtcDateTime));
}

static long NasdaqWallClockMilliseconds(
    int year,
    int month,
    int day,
    int hour,
    int minute)
    => new DateTimeOffset(
            year,
            month,
            day,
            hour,
            minute,
            0,
            TimeSpan.Zero)
        .ToUnixTimeMilliseconds();

static void TestRetryBackoff()
{
    Equal(TimeSpan.FromSeconds(5), RetryDelayPolicy.ForConsecutiveFailure(0));
    Equal(TimeSpan.FromSeconds(10), RetryDelayPolicy.ForConsecutiveFailure(1));
    Equal(TimeSpan.FromSeconds(20), RetryDelayPolicy.ForConsecutiveFailure(2));
    Equal(TimeSpan.FromSeconds(30), RetryDelayPolicy.ForConsecutiveFailure(3));
    Equal(TimeSpan.FromSeconds(60), RetryDelayPolicy.ForConsecutiveFailure(4));
    Equal(TimeSpan.FromSeconds(60), RetryDelayPolicy.ForConsecutiveFailure(100));
    Equal(TimeSpan.FromSeconds(5), RetryDelayPolicy.ForConsecutiveFailure(-1));
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

static void TestQuoteCoordinator()
{
    var now = new DateTime(2026, 8, 17, 2, 0, 0, DateTimeKind.Utc);
    var coordinator = new ReferenceQuoteCoordinator<string, int>(
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(5),
        () => now);
    var release = new TaskCompletionSource<int>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var calls = 0;

    Task<int> Request(CancellationToken _)
    {
        Interlocked.Increment(ref calls);
        return release.Task;
    }

    var requests = Enumerable.Range(0, 8)
        .Select(_ => coordinator.GetAsync("QQQ/common", Request, CancellationToken.None))
        .ToArray();
    SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 1, 1000);
    release.SetResult(42);
    Task.WhenAll(requests).GetAwaiter().GetResult();
    Equal(1, calls);
    Assert(requests.All(task => task.Result == 42));

    Equal(
        42,
        coordinator.GetAsync("QQQ/common", Request, CancellationToken.None)
            .GetAwaiter()
            .GetResult());
    Equal(1, calls);

    now = now.AddSeconds(16);
    Equal(
        42,
        coordinator.GetAsync(
                "QQQ/common",
                _ =>
                {
                    Interlocked.Increment(ref calls);
                    return Task.FromResult(42);
                },
                CancellationToken.None)
            .GetAwaiter()
            .GetResult());
    Equal(2, calls);

    var failureCalls = 0;
    Task<int> Fail(CancellationToken _)
    {
        failureCalls++;
        return Task.FromException<int>(
            new ReferenceDataException("HTTP429", "Yahoo 429"));
    }

    Throws<ReferenceDataException>(() => coordinator
        .GetAsync("SPX/cash", Fail, CancellationToken.None)
        .GetAwaiter()
        .GetResult());
    Throws<ReferenceDataException>(() => coordinator
        .GetAsync("SPX/cash", Fail, CancellationToken.None)
        .GetAwaiter()
        .GetResult());
    Equal(1, failureCalls);
    now = now.AddSeconds(6);
    Throws<ReferenceDataException>(() => coordinator
        .GetAsync("SPX/cash", Fail, CancellationToken.None)
        .GetAwaiter()
        .GetResult());
    Equal(2, failureCalls);
}

static void TestCoordinatorCancellation()
{
    var coordinator = new ReferenceQuoteCoordinator<string, int>(
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(5));
    var release = new TaskCompletionSource<int>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var calls = 0;
    var cancellation = new CancellationTokenSource();
    var cancelledWaiter = coordinator.GetAsync(
        "QQQ/common",
        _ =>
        {
            calls++;
            return release.Task;
        },
        cancellation.Token);
    cancellation.Cancel();
    Throws<OperationCanceledException>(() => cancelledWaiter.GetAwaiter().GetResult());

    var activeWaiter = coordinator.GetAsync(
        "QQQ/common",
        _ => throw new InvalidOperationException("must share in-flight request"),
        CancellationToken.None);
    release.SetResult(7);
    Equal(7, activeWaiter.GetAwaiter().GetResult());
    Equal(1, calls);
}

static void TestSourceDiagnostics()
{
    var primary = new YahooDataException("StaleQuote", "QQQ 报价已过期");
    var fallback = new NasdaqDataException("HTTP429", "Nasdaq 429 限流");
    var combined = new ReferenceSourcesException(
        "Yahoo",
        primary,
        "Nasdaq",
        fallback);

    Assert(combined.Message.Contains("Yahoo: QQQ 报价已过期"));
    Assert(combined.Message.Contains("Nasdaq: HTTP429"));
    Assert(ReferenceErrorPolicy.IsTransient(combined));
    Assert(!ReferenceErrorPolicy.IsDuplicate(combined));
    Assert(ReferenceErrorPolicy.IsDuplicate(new ReferenceSourcesException(
        "Yahoo",
        new YahooDataException("DuplicateQuote", "duplicate"),
        "Nasdaq",
        new NasdaqDataException("DuplicateQuote", "duplicate"))));
    Assert(ReferenceErrorPolicy.IsTransient(
        new YahooDataException("HTTP503", "Yahoo HTTP 503")));
    Assert(ReferenceErrorPolicy.IsTransient(
        new YahooDataException("Timeout", "Yahoo 超时")));
    Assert(!ReferenceErrorPolicy.IsTransient(
        new YahooDataException("InvalidJson", "Yahoo JSON 无效")));
}

static void TestReferenceQuoteValidation()
{
    var minute = new DateTime(2026, 8, 17, 1, 30, 0, DateTimeKind.Utc);
    var quote = new ReferenceMinuteClose("QQQ", minute, 700m);
    Equal(
        quote,
        ReferenceQuoteValidator.Validate(
            quote,
            minute.AddMinutes(5),
            TimeSpan.FromMinutes(20),
            null,
            allowExpiredQuote: false));
    Equal(
        "DuplicateQuote",
        Throws<ReferenceDataException>(() => ReferenceQuoteValidator.Validate(
            quote,
            minute.AddMinutes(5),
            TimeSpan.FromMinutes(20),
            minute,
            allowExpiredQuote: false)).Code);
    Equal(
        "StaleQuote",
        Throws<ReferenceDataException>(() => ReferenceQuoteValidator.Validate(
            quote,
            minute.AddHours(1),
            TimeSpan.FromMinutes(20),
            null,
            allowExpiredQuote: false)).Code);
    Equal(
        quote,
        ReferenceQuoteValidator.Validate(
            quote,
            minute.AddDays(3),
            TimeSpan.FromMinutes(20),
            null,
            allowExpiredQuote: true));
}

static void TestDstMarketTimeAlignment()
{
    var winterNowUtc = new DateTime(2026, 1, 10, 18, 0, 0, DateTimeKind.Utc);
    var summerMinuteUtc = new DateTime(2026, 7, 10, 14, 30, 0, DateTimeKind.Utc);

    Equal(
        new DateTime(2026, 7, 10, 10, 30, 0),
        MarketTimeAlignment.UtcMinuteToMarketTime(
            summerMinuteUtc,
            new DateTime(2026, 1, 10, 13, 0, 0),
            winterNowUtc));
    Equal(
        new DateTime(2026, 7, 10, 9, 30, 0),
        MarketTimeAlignment.UtcMinuteToMarketTime(
            summerMinuteUtc,
            new DateTime(2026, 1, 10, 12, 0, 0),
            winterNowUtc));
    Equal(
        new DateTime(2026, 7, 10, 22, 30, 0),
        MarketTimeAlignment.UtcMinuteToMarketTime(
            summerMinuteUtc,
            new DateTime(2026, 1, 11, 2, 0, 0),
            winterNowUtc));
}

static void TestUnifiedSessionBoundaries()
{
    Assert(!NqTradingSessionCalendar.IsOpen(
        new DateTime(2026, 8, 13, 20, 15, 0, DateTimeKind.Utc)));
    Assert(NqTradingSessionCalendar.IsOpen(
        new DateTime(2026, 8, 13, 20, 30, 0, DateTimeKind.Utc)));
    Assert(!NqTradingSessionCalendar.IsOpen(
        new DateTime(2026, 8, 13, 21, 0, 0, DateTimeKind.Utc)));
    Assert(NqTradingSessionCalendar.IsOpen(
        new DateTime(2026, 8, 13, 22, 0, 0, DateTimeKind.Utc)));
    Assert(UsMarketClock.IsFullEquityMarketHoliday(new DateTime(2026, 7, 3)));
    Assert(UsEquitySessionCalendar.IsQqqExtendedSessionClosed(
        new DateTime(2026, 7, 3, 16, 0, 0, DateTimeKind.Utc)));
}

static void TestAxisLayoutCache()
{
    var cache = new AxisLayoutCache();
    var key = new AxisLayoutKey(23_000m, 24_000m, 40m, 72, 800, 2);
    var first = cache.GetOrCreate(key);
    var second = cache.GetOrCreate(key);
    Assert(ReferenceEquals(first, second));
    Equal(1, cache.BuildCount);
    Assert(first.Count > 0);
    Assert(first.All(tick => tick.Text.Contains('.')));

    var resized = cache.GetOrCreate(key with { Height = 600 });
    Equal(2, cache.BuildCount);
    Assert(!ReferenceEquals(first, resized));
    cache.GetOrCreate(key with { Ratio = 41m });
    Equal(3, cache.BuildCount);
}

static void TestPublicSettingsCompatibility()
{
    var root = FindRepositoryRoot();
    var main = File.ReadAllText(Path.Combine(
        root,
        "src",
        "FuturesReferencePriceAxis",
        "FuturesReferencePriceAxisIndicator.cs"));
    var settings = File.ReadAllText(Path.Combine(
        root,
        "src",
        "FuturesReferencePriceAxis",
        "FuturesReferencePriceAxisIndicator.Settings.cs"));

    foreach (var signature in new[]
             {
                 "public PairMode PairMode",
                 "public MappingMode MappingMode",
                 "public decimal ManualRatio",
                 "public int RefreshIntervalMinutes",
                 "public int MaxQuoteAgeMinutes",
                 "public bool ShowUpdateStatus",
                 "public int StatusPanelOffsetX",
                 "public int StatusPanelOffsetY",
                 "public decimal UiUtcOffsetHours",
                 "public bool ShowCrosshairPriceLabel",
                 "public int AxisWidth",
                 "public DrawingColor AxisTextColor",
                 "public DrawingColor AxisBackgroundColor",
                 "public DrawingColor AxisBorderColor",
                 "public DrawingColor StatusBackgroundColor"
             })
    {
        Assert(settings.Contains(signature, StringComparison.Ordinal), signature);
    }

    foreach (var defaultDeclaration in new[]
             {
                 "private PairMode _pairMode = Core.PairMode.Auto;",
                 "private MappingMode _mappingMode = Core.MappingMode.Automatic;",
                 "private int _refreshIntervalMinutes = 30;",
                 "private int _maxQuoteAgeMinutes = 20;",
                 "private bool _showUpdateStatus = true;",
                 "private bool _showCrosshairPriceLabel = true;",
                 "private int _axisWidth = 72;"
             })
    {
        Assert(main.Contains(defaultDeclaration, StringComparison.Ordinal), defaultDeclaration);
    }
}

static string FindRepositoryRoot()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

    while (directory != null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "FuturesReferencePriceAxis.sln")))
            return directory.FullName;

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("Repository root not found.");
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

static string CreateNasdaqJson(long[] timestamps, string[] closes)
{
    var points = timestamps
        .Zip(closes, (timestamp, close) => $$"""{ "x": {{timestamp}}, "y": {{close}} }""");
    return $$"""
        {
          "data": {
            "symbol": "QQQ",
            "chart": [{{string.Join(',', points)}}]
          }
        }
        """;
}

static string CreateMarketWatchJson(long[] timestamps, string[] closes)
{
    return JsonSerializer.Serialize(new
    {
        TimeInfo = new { Ticks = timestamps },
        Series = new[]
        {
            new
            {
                SeriesId = "s1",
                DataPoints = closes
                    .Select(close => new[] { decimal.Parse(close) })
                    .ToArray()
            }
        }
    });
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

static async Task RunLiveNasdaqQqqCheck()
{
    const string url =
        "https://api.nasdaq.com/api/quote/QQQ/chart?assetclass=etf";
    using var client = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(10)
    };
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 ATAS-FuturesReferencePriceAxis/1.0");
    var json = await client.GetByteArrayAsync(url);
    var nasdaqNowUtc = DateTime.UtcNow;
    var allowExpiredNasdaq = UsEquitySessionCalendar
                                 .IsQqqExtendedSessionClosed(nasdaqNowUtc)
                             || !NqTradingSessionCalendar.IsOpen(nasdaqNowUtc);
    var result = NasdaqQqqParser.ParseLatestCompleted(
        json,
        nasdaqNowUtc,
        TimeSpan.FromMinutes(20),
        allowExpiredQuote: allowExpiredNasdaq,
        requireNqTradableMinute: true);

    Console.WriteLine(
        $"LIVE  {result.Symbol} {result.MinuteStartUtc:yyyy-MM-dd HH:mm:ss} UTC"
        + $" close={result.Close} source={result.Source}");
}

static async Task RunLiveYahooQqqCheck()
{
    const string url =
        "https://query1.finance.yahoo.com/v8/finance/chart/QQQ"
        + "?range=1d&interval=1m&includePrePost=true&events=div%2Csplits";
    using var client = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(10)
    };
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 ATAS-FuturesReferencePriceAxis/1.0");
    var json = await client.GetByteArrayAsync(url);
    var nowUtc = DateTime.UtcNow;
    var isClosed = UsEquitySessionCalendar.IsQqqExtendedSessionClosed(nowUtc);
    var allowExpired = isClosed || !NqTradingSessionCalendar.IsOpen(nowUtc);
    var result = YahooChartParser.ParseLatestCompleted(
        json,
        "QQQ",
        nowUtc,
        TimeSpan.FromMinutes(20),
        allowExpiredQuote: allowExpired,
        requireNqTradableMinute: true);

    Console.WriteLine(
        $"LIVE  {result.Symbol} {result.MinuteStartUtc:yyyy-MM-dd HH:mm:ss} UTC"
        + $" close={result.Close} source={result.Source} closed={isClosed}");
}

static async Task RunLiveMarketWatchSpxCheck()
{
    const string token = "cecc4267a0194af89ca343805a3e57af";
    var payload = JsonSerializer.Serialize(new
    {
        Step = "PT1M",
        TimeFrame = "D5",
        EntitlementToken = token,
        IncludeMockTick = true,
        FilterNullSlots = false,
        FilterClosedPoints = true,
        IncludeClosedSlots = false,
        IncludeOfficialClose = true,
        InjectOpen = false,
        ShowPreMarket = false,
        ShowAfterHours = false,
        UseExtendedTimeFrame = false,
        WantPriorClose = true,
        IncludeCurrentQuotes = false,
        ResetTodaysAfterHoursPercentChange = false,
        Series = new[]
        {
            new
            {
                Key = "INDEX/US/S&P US/SPX",
                Dialect = "Charting",
                Kind = "Ticker",
                SeriesId = "s1",
                DataTypes = new[] { "Last" }
            }
        }
    });
    var url = "https://api-secure.wsj.net/api/michelangelo/timeseries/history"
              + $"?json={Uri.EscapeDataString(payload)}&ckey={token[..10]}";
    using var client = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(15)
    };
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 ATAS-FuturesReferencePriceAxis/1.0");
    client.DefaultRequestHeaders.TryAddWithoutValidation(
        "Dylan2010.EntitlementToken",
        token);
    client.DefaultRequestHeaders.TryAddWithoutValidation(
        "Origin",
        "https://www.marketwatch.com");
    client.DefaultRequestHeaders.Referrer = new Uri(
        "https://www.marketwatch.com/investing/index/spx/charts");
    var json = await client.GetByteArrayAsync(url);
    using (var diagnostic = JsonDocument.Parse(json))
    {
        var root = diagnostic.RootElement;
        var ticks = root.GetProperty("TimeInfo").GetProperty("Ticks");
        var series = root.GetProperty("Series");
        Console.WriteLine(
            $"LIVE  ticks={ticks.GetArrayLength()} series={series.GetArrayLength()}");
        for (var index = 0; index < series.GetArrayLength(); index++)
        {
            var points = series[index].GetProperty("DataPoints");
            var first = points.GetArrayLength() > 0
                ? points[0].GetRawText()
                : "--";
            var last = points.GetArrayLength() > 0
                ? points[points.GetArrayLength() - 1].GetRawText()
                : "--";
            Console.WriteLine(
                $"LIVE  series[{index}] points={points.GetArrayLength()}"
                + $" first={first} last={last}");
        }
    }
    var result = MarketWatchSpxParser.ParseLatestCompleted(
        json,
        DateTime.UtcNow,
        TimeSpan.FromMinutes(20),
        allowExpiredQuote: true);

    Console.WriteLine(
        $"LIVE  {result.Symbol} {result.MinuteStartUtc:yyyy-MM-dd HH:mm:ss} UTC"
        + $" close={result.Close} source={result.Source}");
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

static TException Throws<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException exception)
    {
        return exception;
    }

    throw new InvalidOperationException(
        $"Expected {typeof(TException).Name}");
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

static NasdaqDataException ThrowsNasdaq(string expectedCode, Action action)
{
    try
    {
        action();
    }
    catch (NasdaqDataException ex)
    {
        Equal(expectedCode, ex.Code);
        return ex;
    }

    throw new InvalidOperationException(
        $"Expected {nameof(NasdaqDataException)} with code {expectedCode}");
}

static MarketWatchDataException ThrowsMarketWatch(string expectedCode, Action action)
{
    try
    {
        action();
    }
    catch (MarketWatchDataException ex)
    {
        Equal(expectedCode, ex.Code);
        return ex;
    }

    throw new InvalidOperationException(
        $"Expected {nameof(MarketWatchDataException)} with code {expectedCode}");
}
