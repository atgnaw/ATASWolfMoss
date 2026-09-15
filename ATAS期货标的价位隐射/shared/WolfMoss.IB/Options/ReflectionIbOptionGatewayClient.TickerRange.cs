namespace WolfMoss.ATAS.PriceMapping;

using WolfMoss.ATAS.PriceMapping.Core;

internal sealed partial class ReflectionIbOptionGatewayClient
{
    internal readonly OptionTickerCoordinator Tickers = new();
    private readonly Dictionary<string, (DateOnly Expiration, HashSet<long> Contracts)> _tickerRanges = new(StringComparer.OrdinalIgnoreCase);
    internal void ApplyTickerRange(string ticker, DateOnly expiration, IReadOnlyList<OptionContractDescriptor> contracts)
    {
        var notifications = new List<(Action<IbOptionMarketDataUpdate> Callback, IbOptionMarketDataUpdate Update)>();
        lock (_sync)
        {
            ThrowIfDisposed();
            var ids = contracts.Select(c => c.ConId).ToHashSet();
            _tickerRanges[ticker] = (expiration, ids);
            foreach (var obsolete in _subscriptionsByConId.Values.Where(s => s.Contract.Ticker == ticker
                && (s.Contract.Expiration != expiration || !ids.Contains(s.Contract.ConId))).ToArray())
            {
                // Retire all charts' old edges before admitting new edges.
                RecordHistoryEnd(obsolete, "SUBSCRIPTION_REPLACED");
                var update = new IbOptionMarketDataUpdate(obsolete.Contract, DateTime.UtcNow,
                    null, null, null, false, "RANGE_CHANGED", "合约已退出统一接收范围") { ErrorOrigin = "LOCAL" };
                foreach (var callback in obsolete.Callbacks) notifications.Add((callback, update));
                _subscriptionsByRequest.TryRemove(obsolete.RequestId, out _);
                if (obsolete.RequestId != 0) QueueCancel(obsolete.RequestId);
                obsolete.RequestId = 0;
                obsolete.Consumers.Clear();
                obsolete.Demands.Clear();
                obsolete.RefreshCallbacks();
                _subscriptionsByConId.Remove(obsolete.Contract.ConId);
            }
            UpdateDiagnosticOccupancy();
        }
        foreach (var notification in notifications)
            try { notification.Callback(notification.Update); } catch { }
    }
    private void ValidateTickerRange(IReadOnlyList<OptionContractDescriptor> contracts)
    {
        foreach (var contract in contracts)
            if (_tickerRanges.TryGetValue(contract.Ticker, out var range)
                && (contract.Expiration != range.Expiration || !range.Contracts.Contains(contract.ConId)))
                throw new IbOptionGatewayException("RANGE_CHANGED", "统一接收范围已更新；等待同步新梯级");
    }
}
