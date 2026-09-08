// Source-shared module: compiled privately into each consuming plugin.
namespace WolfMoss.ATAS.PriceMapping;

using WolfMoss.ATAS.PriceMapping.Core;

internal readonly record struct IbGatewayConnectionOptions(
    string Host,
    int Port,
    int ClientId)
{
    public string PoolKey => IbSessionReservation.Key(Host, Port, ClientId);
}

internal readonly record struct IbOptionSubscriptionRequirements(
    bool OpenInterest,
    bool RegularTrades,
    bool AllTimeAndSales)
{
    public string GenericTickList
        => string.Join(",", new[]
        {
            OpenInterest ? "101" : null,
            RegularTrades ? "375" : null,
            AllTimeAndSales ? "233" : null
        }.Where(static value => value != null));

    public IbOptionSubscriptionRequirements Union(
        IbOptionSubscriptionRequirements other)
        => new(
            OpenInterest || other.OpenInterest,
            RegularTrades || other.RegularTrades,
            AllTimeAndSales || other.AllTimeAndSales);
}

internal readonly record struct IbOptionMarketDataUpdate(
    OptionContractDescriptor Contract,
    DateTime ReceivedUtc,
    long? OpenInterest,
    OptionCumulativeSample? RegularTrades,
    OptionCumulativeSample? AllTimeAndSales,
    bool IsDelayed,
    string? ErrorCode = null,
    string? ErrorMessage = null);

internal interface IIbOptionSubscriptionLease : IDisposable
{
    int ContractCount { get; }

    IReadOnlyList<long> ContractIds { get; }

    Task UpdateAsync(IReadOnlyList<OptionContractDescriptor> contracts,
        IbOptionSubscriptionRequirements requirements, int marketDataLineBudget,
        CancellationToken cancellationToken);
}

internal interface IIbOptionGatewayClient : IAsyncDisposable
{
    IbPerformanceSnapshot? PerformanceSnapshot => null;
    bool IsAvailable { get; }

    bool IsConnected { get; }

    int ActiveMarketDataLines { get; }

    string AvailabilityMessage { get; }

    Task ConnectAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<decimal>> GetAvailableStrikesAsync(
        OptionUnderlyingProfile profile,
        DateOnly expiration,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<OptionContractDescriptor>> ResolveContractsAsync(
        OptionUnderlyingProfile profile,
        DateOnly expiration,
        IReadOnlyList<decimal> strikes,
        CancellationToken cancellationToken);

    Task<IIbOptionSubscriptionLease> SubscribeAsync(
        IReadOnlyList<OptionContractDescriptor> contracts,
        IbOptionSubscriptionRequirements requirements,
        Action<IbOptionMarketDataUpdate> onUpdate,
        int marketDataLineBudget,
        CancellationToken cancellationToken);

    Task<IIbOptionSubscriptionLease> SubscribeAvailableAsync(
        IReadOnlyList<OptionContractDescriptor> contracts,
        IbOptionSubscriptionRequirements requirements,
        Action<IbOptionMarketDataUpdate> onUpdate,
        int marketDataLineBudget,
        CancellationToken cancellationToken);
}

internal sealed class IbOptionGatewayException : Exception
{
    public IbOptionGatewayException(string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }

    public string Code { get; }
}
