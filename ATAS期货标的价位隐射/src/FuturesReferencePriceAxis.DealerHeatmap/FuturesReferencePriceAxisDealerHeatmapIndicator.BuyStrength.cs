namespace WolfMoss.ATAS.PriceMapping;

using WolfMoss.ATAS.PriceMapping.Core;

public sealed partial class FuturesReferencePriceAxisDealerHeatmapIndicator
{
    private IReadOnlyList<OptionStrikeRow>? _strengthBaseRows, _strengthRows;
    private DateTime? _strengthBucket;
    private OptionFlowSnapshot AddBuyStrength(OptionFlowSnapshot snapshot)
    {
        if (!BuyStrengthActive || snapshot.BucketStartUtc is not { } start || snapshot.BucketEndUtc is not { } end)
            return snapshot;
        if (!ReferenceEquals(snapshot.Rows, _strengthBaseRows) || _strengthBucket != start)
        {
            var client = _optionGatewayLease?.Client;
            OptionBuyStrength Read(decimal strike, OptionRight right)
            {
                if (start < _buyStrengthEnabledUtc) return OptionBuyStrength.Missing with { Status = BuyStrengthStatus.Partial };
                if (snapshot.Ticker == null || !snapshot.Expiration.HasValue) return OptionBuyStrength.Missing;
                return client?.ReadBuyStrength(snapshot.Ticker, snapshot.Expiration.Value, strike, right,
                    start, end, snapshot.ReceivedUtc) ?? OptionBuyStrength.Missing;
            }
            _strengthBaseRows = snapshot.Rows; _strengthBucket = start;
            _strengthRows = snapshot.Rows.Select(r => r with
            {
                CallBuyStrength = Read(r.StrikeUsd, OptionRight.Call),
                PutBuyStrength = Read(r.StrikeUsd, OptionRight.Put)
            }).ToArray();
        }
        return snapshot with { Rows = _strengthRows! };
    }
}
