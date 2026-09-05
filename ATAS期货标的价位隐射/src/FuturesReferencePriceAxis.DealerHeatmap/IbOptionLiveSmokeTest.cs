namespace WolfMoss.ATAS.PriceMapping;

using System.Collections.Concurrent;

using WolfMoss.ATAS.PriceMapping.Core;

public sealed record IbOptionLiveSmokeResult(
    string Ticker,
    DateOnly Expiration,
    decimal AtmStrike,
    string Strikes,
    int ContractCount,
    int OpenInterestContractCount,
    int RegularTradeContractCount,
    int AllTradeContractCount,
    int ActiveMarketDataLines,
    bool Delayed);

/// <summary>
/// Explicit, opt-in diagnostic used by the standalone test harness. It never
/// requests account, position, or order data.
/// </summary>
public static class IbOptionLiveSmokeTest
{
    public static async Task<IbOptionLiveSmokeResult> RunAsync(
        string host,
        int port,
        int clientId,
        string ticker,
        decimal mappedSpot,
        int strikeLevels,
        int observationSeconds,
        CancellationToken cancellationToken)
    {
        if (!OptionUnderlyingProfile.TryResolve(ticker, out var profile))
            throw new ArgumentException("ticker must be QQQ or SPX", nameof(ticker));

        if (mappedSpot <= 0m)
            throw new ArgumentOutOfRangeException(nameof(mappedSpot));

        var expiration = NyseTradingCalendar
            .ResolveTarget(DateTime.UtcNow, profile.Ticker)
            .TargetExpiration;
        await using var gatewayLease = IbOptionGatewayPool.Acquire(
            new IbGatewayConnectionOptions(host, port, clientId));
        var gateway = gatewayLease.Client;
        await gateway.ConnectAsync(cancellationToken).ConfigureAwait(false);
        var available = await gateway.GetAvailableStrikesAsync(
                profile, expiration, cancellationToken)
            .ConfigureAwait(false);
        var requestedStrikes = OptionStrikeSelection.SelectCentered(
            available,
            mappedSpot,
            strikeLevels,
            out var selectionAtmStrike);
        var probeStrikes = OptionStrikeSelection.SelectCenteredCandidates(
            available,
            selectionAtmStrike,
            Math.Min(available.Count, requestedStrikes.Count + 16),
            out _);
        var probedContracts = await gateway.ResolveContractsAsync(
                profile, expiration, probeStrikes, cancellationToken)
            .ConfigureAwait(false);
        var resolvedStrikes = probedContracts
            .Select(static contract => contract.StrikeUsd)
            .Distinct()
            .OrderBy(static strike => strike)
            .ToArray();
        var strikes = OptionStrikeSelection.SelectCenteredCandidates(
            resolvedStrikes,
            selectionAtmStrike,
            requestedStrikes.Count,
            out var atmStrike);
        var strikeSet = strikes.ToHashSet();
        var contracts = probedContracts
            .Where(contract => strikeSet.Contains(contract.StrikeUsd))
            .ToArray();

        if (contracts.Length == 0)
            throw new IbOptionGatewayException("NO_CONTRACTS", "IB 未返回可测试的 0DTE 合约");

        var oiContracts = new ConcurrentDictionary<long, byte>();
        var regularContracts = new ConcurrentDictionary<long, byte>();
        var allContracts = new ConcurrentDictionary<long, byte>();
        var delayed = 0;

        void OnUpdate(IbOptionMarketDataUpdate update)
        {
            if (update.OpenInterest.HasValue)
                oiContracts.TryAdd(update.Contract.ConId, 0);

            if (update.RegularTrades.HasValue)
                regularContracts.TryAdd(update.Contract.ConId, 0);

            if (update.AllTimeAndSales.HasValue)
                allContracts.TryAdd(update.Contract.ConId, 0);

            if (update.IsDelayed)
                Interlocked.Exchange(ref delayed, 1);
        }

        using var subscription = await gateway.SubscribeAvailableAsync(
                contracts,
                new IbOptionSubscriptionRequirements(true, true, true),
                OnUpdate,
            Math.Max(contracts.Length, 10),
                cancellationToken)
            .ConfigureAwait(false);
        await Task.Delay(
                TimeSpan.FromSeconds(Math.Clamp(observationSeconds, 5, 120)),
                cancellationToken)
            .ConfigureAwait(false);

        return new IbOptionLiveSmokeResult(
            profile.Ticker,
            expiration,
            atmStrike,
            string.Join(",", strikes),
            subscription.ContractCount,
            oiContracts.Count,
            regularContracts.Count,
            allContracts.Count,
            gateway.ActiveMarketDataLines,
            Volatile.Read(ref delayed) != 0);
    }
}
