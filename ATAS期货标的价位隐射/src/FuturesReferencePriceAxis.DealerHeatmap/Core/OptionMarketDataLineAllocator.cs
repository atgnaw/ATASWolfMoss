namespace WolfMoss.ATAS.PriceMapping.Core;

public sealed record OptionMarketDataLineAllocation(
    IReadOnlyList<OptionContractDescriptor> Contracts,
    int RequestedContractCount,
    int RequiredNewLineCount,
    int AllocatedNewLineCount)
{
    public bool IsComplete => Contracts.Count == RequestedContractCount;
}

public static class OptionMarketDataLineAllocator
{
    public static OptionMarketDataLineAllocation Allocate(
        IReadOnlyList<OptionContractDescriptor> requestedContracts,
        IReadOnlyCollection<long> activeContractIds,
        int marketDataLineBudget)
    {
        var active = activeContractIds
            .Where(static conId => conId > 0)
            .ToHashSet();
        var uniqueRequested = requestedContracts
            .Where(static contract => contract.ConId > 0)
            .GroupBy(static contract => contract.ConId)
            .Select(static group => group.First())
            .ToArray();
        var availableNewLines = Math.Max(
            0,
            marketDataLineBudget - active.Count);
        var selected = new List<OptionContractDescriptor>(uniqueRequested.Length);
        var requiredNewLines = 0;
        var allocatedNewLines = 0;

        foreach (var contract in uniqueRequested)
        {
            if (active.Contains(contract.ConId))
            {
                selected.Add(contract);
                continue;
            }

            requiredNewLines++;

            if (availableNewLines <= 0)
                continue;

            selected.Add(contract);
            allocatedNewLines++;
            availableNewLines--;
        }

        return new OptionMarketDataLineAllocation(
            selected,
            uniqueRequested.Length,
            requiredNewLines,
            allocatedNewLines);
    }
}
