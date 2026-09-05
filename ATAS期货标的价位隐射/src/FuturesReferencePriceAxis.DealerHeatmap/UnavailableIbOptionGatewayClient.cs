namespace WolfMoss.ATAS.PriceMapping;

using WolfMoss.ATAS.PriceMapping.Core;

internal sealed class UnavailableIbOptionGatewayClient : IIbOptionGatewayClient
{
    public bool IsAvailable => false;

    public bool IsConnected => false;

    public int ActiveMarketDataLines => 0;

    public string AvailabilityMessage
        => "Pro DLL 未嵌入官方 IB API；请安装 TWS API 10.45 后重新编译";

    public Task ConnectAsync(CancellationToken cancellationToken)
        => Task.FromException(new IbOptionGatewayException(
            "IBAPI_MISSING",
            AvailabilityMessage));

    public Task<IReadOnlyList<decimal>> GetAvailableStrikesAsync(
        OptionUnderlyingProfile profile,
        DateOnly expiration,
        CancellationToken cancellationToken)
        => Task.FromException<IReadOnlyList<decimal>>(new IbOptionGatewayException(
            "IBAPI_MISSING",
            AvailabilityMessage));

    public Task<IReadOnlyList<OptionContractDescriptor>> ResolveContractsAsync(
        OptionUnderlyingProfile profile,
        DateOnly expiration,
        IReadOnlyList<decimal> strikes,
        CancellationToken cancellationToken)
        => Task.FromException<IReadOnlyList<OptionContractDescriptor>>(
            new IbOptionGatewayException("IBAPI_MISSING", AvailabilityMessage));

    public Task<IIbOptionSubscriptionLease> SubscribeAsync(
        IReadOnlyList<OptionContractDescriptor> contracts,
        IbOptionSubscriptionRequirements requirements,
        Action<IbOptionMarketDataUpdate> onUpdate,
        int marketDataLineBudget,
        CancellationToken cancellationToken)
        => Task.FromException<IIbOptionSubscriptionLease>(
            new IbOptionGatewayException("IBAPI_MISSING", AvailabilityMessage));

    public Task<IIbOptionSubscriptionLease> SubscribeAvailableAsync(
        IReadOnlyList<OptionContractDescriptor> contracts,
        IbOptionSubscriptionRequirements requirements,
        Action<IbOptionMarketDataUpdate> onUpdate,
        int marketDataLineBudget,
        CancellationToken cancellationToken)
        => Task.FromException<IIbOptionSubscriptionLease>(
            new IbOptionGatewayException("IBAPI_MISSING", AvailabilityMessage));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
