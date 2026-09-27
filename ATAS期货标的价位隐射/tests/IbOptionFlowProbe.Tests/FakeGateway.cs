using WolfMoss.ATAS.PriceMapping;
using WolfMoss.ATAS.PriceMapping.Core;

namespace IbOptionFlowProbe.Tests;

internal sealed class FakeGateway : IIbOptionGatewayClient
{
    public Action<IbRawMarketEvent>? Observer;
    public string? ConnectError;
    public bool MissingContracts;
    public bool CloseAfterSubscribe = true;
    public bool PartialPermissionWarning;
    public bool Disposed, LeaseDisposed;
    public decimal[] Strikes = [98, 99, 100, 100.5m, 101, 102];
    public int LastBudget, LastContractCount;
    public bool IsAvailable => true;
    public bool IsConnected { get; private set; }
    public int ActiveMarketDataLines { get; private set; }
    public string AvailabilityMessage => "FAKE";
    public Task ConnectAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (ConnectError != null) throw new IbOptionGatewayException(ConnectError, "Test");
        IsConnected = true; return Task.CompletedTask;
    }
    public Task<IReadOnlyList<decimal>> GetAvailableStrikesAsync(OptionUnderlyingProfile p, DateOnly e, CancellationToken t)
        => Task.FromResult<IReadOnlyList<decimal>>(Strikes);
    public Task<IReadOnlyList<OptionContractDescriptor>> ResolveContractsAsync(OptionUnderlyingProfile p, DateOnly e,
        IReadOnlyList<decimal> strikes, CancellationToken t)
        => Task.FromResult<IReadOnlyList<OptionContractDescriptor>>(MissingContracts ? [] : strikes.Where(x => x != 100.5m)
            .SelectMany(s => new[] { OptionRight.Call, OptionRight.Put }.Select(r =>
                new OptionContractDescriptor((long)s * 10 + (int)r, p.Ticker, e, s, r,
                    p.PreferredTradingClass, p.Exchange, 100, "20260916:0930-20260916:1600"))).ToArray());
    public Task<IIbOptionSubscriptionLease> SubscribeAsync(IReadOnlyList<OptionContractDescriptor> contracts,
        IbOptionSubscriptionRequirements requirements, Action<IbOptionMarketDataUpdate> update, int budget, CancellationToken t)
    {
        if (requirements.GenericTickList != "375,233" || contracts.Count > budget) throw new InvalidOperationException();
        ActiveMarketDataLines = contracts.Count;
        LastBudget = budget; LastContractCount = contracts.Count;
        Observer?.Invoke(new(1, DateTime.UtcNow, 1, "tickString", 1, contracts[0], 77, "1;2;1789560000000;20;1;true"));
        if (PartialPermissionWarning)
        {
            Observer?.Invoke(new(2, DateTime.UtcNow, 2, "error", 1, contracts[0], ErrorCode: 10090));
            Observer?.Invoke(new(3, DateTime.UtcNow, 3, "tickString", 1, contracts[0], 77, "1;2;1789560001000;22;1;true"));
        }
        if (CloseAfterSubscribe) Observer?.Invoke(new(2, DateTime.UtcNow, 2, "connectionClosed", -1, null));
        return Task.FromResult<IIbOptionSubscriptionLease>(new FakeLease(this, contracts));
    }
    public Task<IIbOptionSubscriptionLease> SubscribeAvailableAsync(IReadOnlyList<OptionContractDescriptor> c,
        IbOptionSubscriptionRequirements r, Action<IbOptionMarketDataUpdate> u, int b, CancellationToken t)
        => SubscribeAsync(c, r, u, b, t);
    public ValueTask DisposeAsync() { Disposed = true; IsConnected = false; return ValueTask.CompletedTask; }
    private sealed class FakeLease(FakeGateway owner, IReadOnlyList<OptionContractDescriptor> contracts) : IIbOptionSubscriptionLease
    {
        public int ContractCount => contracts.Count;
        public IReadOnlyList<long> ContractIds => contracts.Select(x => x.ConId).ToArray();
        public Task UpdateAsync(IReadOnlyList<OptionContractDescriptor> c, IbOptionSubscriptionRequirements r,
            int b, CancellationToken t) => throw new NotSupportedException();
        public void Dispose() { owner.LeaseDisposed = true; owner.ActiveMarketDataLines = 0; }
    }
}
