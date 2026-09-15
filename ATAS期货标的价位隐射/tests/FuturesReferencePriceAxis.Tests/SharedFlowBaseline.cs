using System.Diagnostics;
using WolfMoss.ATAS.PriceMapping.Core;

internal static class SharedFlowBaseline
{
    public static void Run()
    {
        Console.WriteLine($"shared_engine_baseline; runtime={Environment.Version}; seed=2210; warmup=3; samples=30; no_network=true");
        Console.WriteLine("mode,minutes,consumers,contracts,mean_ms,p95_ms,allocated_bytes_per_refresh");
        foreach (var mode in Enum.GetValues<OptionFlowBucketMode>())
        foreach (var minutes in new[] { 1, 3, 5, 10 })
        foreach (var consumers in new[] { 1, 2, 8 })
        {
            var start = new DateTime(2026, 9, 1, 13, 30, 0, DateTimeKind.Utc);
            var expiration = DateOnly.FromDateTime(start);
            var contracts = Enumerable.Range(0, 42).Select(i => new OptionContractDescriptor(i + 1, "QQQ", expiration,
                700 + i / 2, i % 2 == 0 ? OptionRight.Call : OptionRight.Put, "QQQ", "SMART", 100, "")).ToArray();
            OptionTradingSegment[] segments = [new(start, start.AddHours(6))];
            var book = new OptionFlowGroup(new("benchmark", "QQQ", expiration, mode, minutes, OptionFlowTradeScope.RegularTrades, 21), start);
            book.Configure(contracts, 710, segments, 1, start); book.ObservationChanged(1, start);
            for (var s = 1; s <= 600; s += 5)
            {
                foreach (var contract in contracts) book.Receive(new(contract.ConId, start.AddSeconds(s), s * 10, 2, 100, 2, 1), start.AddSeconds(s));
                book.Read(start.AddSeconds(s), false);
            }
            void Refresh(DateTime now)
            {
                for (var i = 0; i < consumers; i++)
                { book.Configure(contracts, 710, segments, 1, now); book.Read(now, false); }
            }
            for (var i = 0; i < 3; i++) Refresh(start.AddSeconds(601 + i));
            var times = new double[30];
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < times.Length; i++)
            {
                var begin = Stopwatch.GetTimestamp(); Refresh(start.AddSeconds(604 + i));
                times[i] = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
            }
            var allocated = (GC.GetAllocatedBytesForCurrentThread() - bytes) / times.Length;
            Array.Sort(times);
            Console.WriteLine(FormattableString.Invariant($"{mode},{minutes},{consumers},42,{times.Average():F5},{times[28]:F5},{allocated}"));
        }
    }
}
