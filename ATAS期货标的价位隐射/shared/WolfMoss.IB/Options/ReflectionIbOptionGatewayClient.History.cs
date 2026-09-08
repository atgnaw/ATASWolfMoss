using WolfMoss.MarketData;
using WolfMoss.ATAS.PriceMapping.Core;

namespace WolfMoss.ATAS.PriceMapping;

internal sealed partial class ReflectionIbOptionGatewayClient
{
    private readonly Guid _historyStreamId = Guid.NewGuid();
    private sealed class IbHistoryObservation(Guid run, int request, MarketContractIdentity contract,
        MarketTradingContext trading)
    {
        public Guid Run = run;
        public int Request = request;
        public MarketContractIdentity Contract = contract;
        public MarketTradingContext Trading = trading;
        public OptionCumulativeSample? Regular, All;
    }

    private void RecordMarketInput(SharedSubscription subscription, IbOptionMarketDataUpdate update, MarketEventRecorder recorder, int? receivedRequestId = null)
    {
        // Only reception metadata; does not alter OI/Flow state or restore a live baseline.
        var requestId = receivedRequestId ?? subscription.RequestId;
        var fresh = subscription.History is not { } existing || existing.Run != recorder.RunId
            || existing.Request != requestId;
        if (fresh)
        {
            var c = subscription.Contract;
            subscription.History = new(recorder.RunId, requestId,
                new(c.ConId, c.Ticker, c.Expiration, c.StrikeUsd,
                    c.Right == OptionRight.Call ? MarketOptionRight.Call : MarketOptionRight.Put,
                    c.TradingClass, c.Exchange, c.Multiplier),
                new(null, null, null, Trim(c.TradingHours, 4096), Trim(c.TradingTimeZoneId, 128)));
        }
        var observation = subscription.History!;
        var sample = update.RegularTrades ?? update.AllTimeAndSales;
        var scope = update.RegularTrades.HasValue ? MarketTradeScope.RegularTrades
            : update.AllTimeAndSales.HasValue ? MarketTradeScope.AllTimeAndSales : MarketTradeScope.NotApplicable;
        var quality = MarketEventQuality.SourceContinuityUnknown;
        if (fresh) quality |= MarketEventQuality.ObservationStart | MarketEventQuality.PartialCoverage;
        if (update.IsDelayed) quality |= MarketEventQuality.Delayed;
        if (update.ErrorCode != null && update.ErrorCode is not ("SUBSCRIPTION_ENDED" or "SUBSCRIPTION_REPLACED" or "CONNECTION_CLOSED"))
            quality |= MarketEventQuality.SourceError | MarketEventQuality.PartialCoverage;
        if (update.ErrorCode is "SUBSCRIPTION_ENDED" or "SUBSCRIPTION_REPLACED" or "CONNECTION_CLOSED")
            quality |= MarketEventQuality.ObservationEnd | MarketEventQuality.PartialCoverage;
        if (!sample.HasValue) quality |= MarketEventQuality.SourceTimeUnknown;
        if (sample is { } current)
        {
            var previous = scope == MarketTradeScope.RegularTrades ? observation.Regular : observation.All;
            if (!previous.HasValue) quality |= MarketEventQuality.ObservationStart | MarketEventQuality.PartialCoverage;
            if (previous is { } old)
            {
                if (current.SampleUtc < old.SampleUtc) quality |= MarketEventQuality.OutOfOrder;
                else
                {
                    var reset = current.TotalVolume < old.TotalVolume || current.Multiplier != old.Multiplier;
                    try { reset |= current.Vwap * current.TotalVolume < old.Vwap * old.TotalVolume; }
                    catch (OverflowException) { reset = true; }
                    if (reset) quality |= MarketEventQuality.CounterReset | MarketEventQuality.PartialCoverage;
                }
            }
            if ((quality & MarketEventQuality.OutOfOrder) == 0)
            {
                if (scope == MarketTradeScope.RegularTrades) observation.Regular = current;
                else observation.All = current;
            }
        }
        recorder.Publish(new(1, recorder.RunId, recorder.NextSequence(), MarketEventSource.Ib,
            update.ErrorCode != null ? MarketEventKind.ObservationBoundary
                : update.OpenInterest.HasValue ? MarketEventKind.OptionOpenInterest : MarketEventKind.OptionCumulativeTrade,
            _historyStreamId, _diagnosticInstance, observation.Request, update.Contract.Ticker,
            update.Contract.Expiration, observation.Contract, sample?.SampleUtc, update.ReceivedUtc, observation.Trading,
            scope, quality, update.OpenInterest, sample is { } s
                ? new(s.TotalVolume, s.Vwap, s.Multiplier, s.LastTradePrice, s.LastTradeSize) : null,
            Boundary: update.ErrorCode == null ? null : update.ErrorCode switch
            {
                "SUBSCRIPTION_ENDED" => MarketBoundaryReason.SubscriptionEnded,
                "SUBSCRIPTION_REPLACED" => MarketBoundaryReason.SubscriptionReplaced,
                "CONNECTION_CLOSED" => MarketBoundaryReason.ConnectionClosed,
                "NO_PERMISSION" => MarketBoundaryReason.PermissionDenied,
                "LINE_LIMIT" => MarketBoundaryReason.LineLimit,
                "INVALID_SAMPLE" => MarketBoundaryReason.InvalidSourceSample,
                _ => MarketBoundaryReason.SourceUnavailable
            }));
    }
    private void RecordHistoryEnd(SharedSubscription subscription, string reason)
    {
        if (MarketEventHub.Current is not { } recorder) return;
        try { RecordMarketInput(subscription, new(subscription.Contract, DateTime.UtcNow, null, null, null,
            subscription.IsDelayed, reason), recorder); }
        catch { recorder.CaptureFailed(); }
    }
    private void RecordHistoryConnectionEnd()
    {
        if (MarketEventHub.Current == null) return;
        foreach (var subscription in _subscriptionsByRequest.Values) RecordHistoryEnd(subscription, "CONNECTION_CLOSED");
    }
    private static string Trim(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
}
