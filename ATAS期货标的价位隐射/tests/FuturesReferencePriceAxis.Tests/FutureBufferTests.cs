using WolfMoss.ATAS.PriceMapping.Core;

internal static class FutureBufferTests
{
    private static readonly DateTime Start = new(2026, 9, 11, 13, 30, 0, DateTimeKind.Utc);
    private static readonly DateOnly Expiry = new(2026, 9, 11);
    private static readonly OptionTradingSegment[] Segments = [new(Start, Start.AddHours(6))];
    private static OptionContractDescriptor C(long id) => new(id, "QQQ", Expiry, 700 + id, OptionRight.Call, "QQQ", "SMART", 100, "");
    private static OptionCumulativeSample S(long id, double seconds, long volume) => new(id, Start.AddSeconds(seconds), volume, 2, 100, 2, 1);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static OptionFlowGroup Book(OptionFlowBucketMode mode, int minutes = 1, int contracts = 2)
    {
        var b = new OptionFlowGroup(new("buffer-offline", "QQQ", Expiry, mode, minutes, OptionFlowTradeScope.RegularTrades, 21), Start);
        b.Configure(Enumerable.Range(1, contracts).Select(i => C(i)).ToArray(), 701, Segments, 1, Start);
        b.ObservationChanged(1, Start);
        return b;
    }

    public static void EligibilityOrderAndDedup()
    {
        foreach (var mode in Enum.GetValues<OptionFlowBucketMode>())
        {
            var b = Book(mode);
            Check(b.Receive(S(1, 1, 100), Start.AddSeconds(1)), "Baseline");
            Check(b.ReceiveDetailed(S(1, 10.4, 110), Start.AddSeconds(10)) == FlowSampleDisposition.BufferedFuture, "400ms queued");
            Check(b.ReceiveDetailed(S(1, 10.4, 110), Start.AddSeconds(10)) == FlowSampleDisposition.Duplicate, "Cross-chart pending duplicate");
            Check(b.ReceiveDetailed(S(1, 10.5, 111), Start.AddSeconds(10.2)) == FlowSampleDisposition.BufferedFuture, "Successor also queues");
            Check(b.Receive(S(2, 10.2, 50), Start.AddSeconds(10.2)), "Other contract not blocked");
            b.Configure([C(1), C(2)], 701, Segments.ToArray(), 1, Start.AddSeconds(10.3));
            Check(b.FutureSnapshot().Pending == 2 && b.AcceptedSamples == 2, "Equivalent chart segments preserve pending; no premature write");
            b.Read(Start.AddSeconds(10.4), false);
            Check(b.FutureSnapshot().Pending == 1 && b.FutureSnapshot().Released == 1, "Release at original source time");
            b.Read(Start.AddSeconds(10.6), false);
            Check(b.FutureSnapshot() is { Pending: 0, Buffered: 2, Released: 2, Discarded: 0 }, "No new callback needed to flush");
            Check(b.ReceiveDetailed(S(1, 10.5, 111), Start.AddSeconds(10.2)) == FlowSampleDisposition.Duplicate
                && b.FutureSnapshot().Buffered == 2, "Late follower cannot requeue a released event");
            Check(b.ReceiveDetailed(S(1, 10.3, 109), Start.AddSeconds(10.7)) == FlowSampleDisposition.OutOfOrder, "Existing ordering checks retained");
            Check(b.ReceiveDetailed(S(1, 21, 120), Start.AddSeconds(20)) == FlowSampleDisposition.BufferedFuture, "Inclusive 1-second limit");
            Check(b.ReceiveDetailed(S(1, 22.001, 121), Start.AddSeconds(21)) == FlowSampleDisposition.FutureSource, "Above 1 second rejected");
            Check(b.FutureSnapshot().MaxLeadMilliseconds == 1000, "Buffered lead measured independently of rejected lead");
        }
        var d = new FlowReceptionDiagnostics();
        d.Record(Start.AddMilliseconds(400), Start, FlowSampleDisposition.BufferedFuture, null);
        Check(d.Read().Buffered == 1 && d.Read().Rejected == 0 && d.Read().Future == 0, "Queued callback is not rejected");
        var snapshot = new PerformanceCollector().Sample(Start, null, 84) with
        { Reception = d.Read(), FutureBuffer = new(1, 3, 1, 1, 400.125) };
        var columns = PerformanceDiagnosticRecorder.Header.Split(',').Zip(PerformanceDiagnosticRecorder.ToCsv(snapshot).Split(','))
            .ToDictionary(p => p.First, p => p.Second);
        Check(columns["flow_buffered"] == "1" && columns["flow_rejected"] == "0"
            && columns["future_pending"] == "1" && columns["future_buffered"] == "3"
            && columns["future_released"] == "1" && columns["future_discarded"] == "1"
            && columns["future_buffer_max_lead_ms"] == "400.125", "Record preserves buffer outcomes separately from rejection");
    }

    public static void BucketsAndSessions()
    {
        foreach (var minutes in new[] { 1, 3, 5, 10 })
        {
            var seconds = minutes * 60;
            var b = Book(OptionFlowBucketMode.PreviousCompletedFixed, minutes);
            b.Receive(S(1, 1, 100), Start.AddSeconds(1));
            Check(b.ReceiveDetailed(S(1, seconds - .2, 110), Start.AddSeconds(seconds - .5)) == FlowSampleDisposition.BufferedFuture, "End-of-bucket queued");
            b.Read(Start.AddSeconds(seconds + .1), false);
            var done = b.Read(Start.AddSeconds(seconds + 3), false);
            Check(done.BucketEndUtc == Start.AddSeconds(seconds) && done.Rows[0].CallVolume == 11,
                "Original source bucket preserved including partial first-trade semantics");
            var next = S(1, seconds + 3.4, 115);
            Check(b.ReceiveDetailed(next, Start.AddSeconds(seconds + 3)) == FlowSampleDisposition.BufferedFuture, "New bucket pending");
            b.Read(next.SampleUtc, false);
            var second = b.Read(Start.AddSeconds(2 * seconds + 3), false);
            Check(second.Rows[0].CallVolume == 5, "No cross-bucket double counting");

            var rolling = Book(OptionFlowBucketMode.Rolling, minutes);
            rolling.Receive(S(1, 1, 100), Start.AddSeconds(1));
            rolling.ReceiveDetailed(S(1, 10.4, 110), Start.AddSeconds(10));
            rolling.Read(Start.AddSeconds(10.5), false);
            Check(rolling.FutureSnapshot().Released == 1, "Rolling supports same buffering");
            var aged = rolling.Read(Start.AddSeconds(seconds + 12), false);
            Check(aged.Rows.All(r => !r.CallVolume.HasValue || r.CallVolume == 0), "Rolling data expires by original event time");
        }
        foreach (var mode in Enum.GetValues<OptionFlowBucketMode>())
        {
            var gth = Book(mode);
            var hours = new[] { new OptionTradingSegment(Start, Start.AddSeconds(60)), new OptionTradingSegment(Start.AddSeconds(90), Start.AddMinutes(5)) };
            gth.Configure([C(1), C(2)], 701, hours, 2, Start);
            Check(gth.ReceiveDetailed(S(1, 60.1, 100), Start.AddSeconds(59.8)) == FlowSampleDisposition.OutsideSession, "Maintenance gap not admitted");
            gth.ReceiveDetailed(S(1, 59.9, 100), Start.AddSeconds(59.5));
            gth.Read(Start.AddSeconds(60.2), false);
            Check(gth.FutureSnapshot().Released == 1, "Previous segment event can finish at close without crossing segment");
        }
    }

    public static void BoundariesAndLimits()
    {
        foreach (var mode in Enum.GetValues<OptionFlowBucketMode>())
        {
            var b = Book(mode);
            b.Receive(S(1, 1, 100), Start.AddSeconds(1));
            b.ReceiveDetailed(S(1, 10.8, 110), Start.AddSeconds(10));
            b.Retire(1, Start.AddSeconds(10.2));
            Check(b.FutureSnapshot() is { Pending: 0, Discarded: 1 }, "ATM removal discards not-yet-due input");
            b.Configure([C(1), C(2)], 701, Segments, 2, Start.AddSeconds(11));
            b.Receive(S(1, 12, 200), Start.AddSeconds(12));
            var value = b.Read(Start.AddSeconds(63), false);
            Check(value.Rows.All(r => !r.CallVolume.HasValue || r.CallVolume < 100), "Reentry cannot bridge unobserved gap");

            var disconnected = Book(mode);
            disconnected.ReceiveDetailed(S(1, 10.8, 110), Start.AddSeconds(10));
            disconnected.ObservationChanged(-1, Start.AddSeconds(10.2));
            disconnected.ObservationChanged(1, Start.AddSeconds(11));
            disconnected.Read(Start.AddSeconds(12), false);
            Check(disconnected.FutureSnapshot() is { Released: 0, Pending: 0, Discarded: 1 }, "Disconnect cancels pending epoch");

            var stalled = Book(mode);
            stalled.ReceiveDetailed(S(1, 10.8, 110), Start.AddSeconds(10));
            stalled.Read(Start.AddSeconds(14), false);
            Check(stalled.FutureSnapshot() is { Released: 0, Pending: 0, Discarded: 1 }, "Bounded residence; no stale publication");
            Check(stalled.ReceiveDetailed(S(1, 15, 120), Start.AddSeconds(15)) == FlowSampleDisposition.OutsideLadder, "Expired queue retires observation until fresh configuration");

            var clock = Book(mode);
            clock.ObserveClock(Start.AddSeconds(10), 0, 1000);
            clock.ReceiveDetailed(S(1, 10.8, 110), Start.AddSeconds(10));
            clock.ObserveClock(Start.AddSeconds(5), 1000, 1000);
            Check(clock.FutureSnapshot() is { Pending: 0, Discarded: 1 }, "Clock correction clears queue");
        }
        var limited = Book(OptionFlowBucketMode.Rolling);
        for (var i = 0; i < OptionFlowGroup.FuturePerContractLimit; i++)
            Check(limited.ReceiveDetailed(S(1, 10.5 + i / 1000d, i), Start.AddSeconds(10)) == FlowSampleDisposition.BufferedFuture, "Bounded enqueue");
        Check(limited.ReceiveDetailed(S(1, 10.9, 999), Start.AddSeconds(10)) == FlowSampleDisposition.FutureBufferOverflow
            && limited.FutureSnapshot() is { Pending: 0, Discarded: 129 }, "Overflow explicit and clears contract observation");
        var global = Book(OptionFlowBucketMode.Rolling, contracts: 33);
        for (var id = 1; id <= 32; id++) for (var i = 0; i < 128; i++)
            global.ReceiveDetailed(S(id, 10.5 + i / 1000d, i), Start.AddSeconds(10));
        Check(global.FutureSnapshot().Pending == 4096
            && global.ReceiveDetailed(S(33, 10.5, 1), Start.AddSeconds(10)) == FlowSampleDisposition.FutureBufferOverflow,
            "Global memory limit");
    }
}
