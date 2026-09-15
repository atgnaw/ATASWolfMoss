namespace WolfMoss.ATAS.PriceMapping;

using WolfMoss.ATAS.PriceMapping.Core;

public sealed partial class FuturesReferencePriceAxisDealerHeatmapIndicator
{
    private long _coordinatorQuoteTicks;
    private OptionTickerCoordinator.Plan? _tickerPlan;
    private int _tickerReceiverLevels;
    private long _tickerRangeVersion, _tickerAppliedRangeVersion;
    private int _tickerConfiguredDisplayLevels;

    protected override void OnCalculate(int bar, decimal value)
    {
        base.OnCalculate(bar, value);
        if (bar == CurrentBar - 1 && LatestChartPrice > 0)
            Interlocked.Exchange(ref _coordinatorQuoteTicks, RealtimeUtcNow().Ticks);
    }

    private DateTime TickerAnchor(DateTime now, DateOnly expiration, string ticker)
    {
        if (_activeOptionContracts.Count > 0)
        {
            var segments = GetCachedFlowSegments(ticker, _activeOptionContracts);
            foreach (var segment in segments)
                if (segment.Contains(now)) return segment.StartUtc;
            var next = segments.FirstOrDefault(s => s.StartUtc > now);
            if (next != default) return next.StartUtc;
        }
        return NyseTradingCalendar.EasternToUtc(expiration.ToDateTime(new TimeOnly(9, 30)));
    }

    private string TickerDiagnosticLine()
    {
        var plan = Volatile.Read(ref _tickerPlan);
        return plan == null ? "ATM 协调: 未注册" :
            $"ATM 统一: {plan.Ticker}；主图 {plan.Master}{(plan.MasterStale ? " STALE" : "") }；{plan.CadenceMinutes}m；下次 UTC {plan.NextSwitchUtc:HH:mm:ss}；接收档 {Volatile.Read(ref _tickerReceiverLevels)}；实例 {plan.Consumers}";
    }
}
