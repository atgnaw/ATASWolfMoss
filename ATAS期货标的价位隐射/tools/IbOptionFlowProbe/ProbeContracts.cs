using WolfMoss.ATAS.PriceMapping;
using WolfMoss.ATAS.PriceMapping.Core;

namespace IbOptionFlowProbe;

internal static class ProbeContracts
{
    public static DateOnly Expiration(OptionUnderlyingProfile profile, DateTime utc)
    {
        var et = UsMarketClock.ToEastern(utc);
        if (profile.Ticker == "QQQ" && (!NyseTradingCalendar.IsTradingDay(et.Date)
            || et.TimeOfDay < NyseTradingCalendar.RegularOpen || et.TimeOfDay >= NyseTradingCalendar.GetRegularClose(et.Date)))
            throw new IbOptionGatewayException("CLOSED", "QQQ 仅在 RTH 采集；请稍后重新启动。");
        // For SPX the evening GTH contract belongs to the next trading day.
        return DateOnly.FromDateTime(NyseTradingCalendar.IsTradingDay(et.Date)
            && et.TimeOfDay < NyseTradingCalendar.GetRegularClose(et.Date)
                ? et.Date : NyseTradingCalendar.GetNextTradingDay(et.Date));
    }
    public static decimal[] SelectThree(IEnumerable<decimal> strikes, decimal spot)
        => SelectSymmetric(strikes, spot, 1);
    public static decimal[] SelectSymmetric(IEnumerable<decimal> strikes, decimal spot, int eachSide)
    {
        if (eachSide is < 0 or > 20) throw new ArgumentOutOfRangeException(nameof(eachSide));
        var sorted = strikes.Where(x => x > 0).Distinct().Order().ToArray();
        if (sorted.Length < 2 * eachSide + 1) return [];
        var atm = sorted.OrderBy(x => Math.Abs(x - spot)).ThenBy(x => x).First();
        var index = Array.IndexOf(sorted, atm);
        return index < eachSide || index + eachSide >= sorted.Length ? [] : sorted[(index - eachSide)..(index + eachSide + 1)];
    }
    public static async Task<ProbeContract[]> DiscoverAsync(IIbOptionGatewayClient gateway,
        OptionUnderlyingProfile profile, decimal spot, DateTime utc, CancellationToken token, int eachSide = 1)
    {
        if (eachSide is < 0 or > 20) throw new ArgumentOutOfRangeException(nameof(eachSide));
        var strikeCount = 2 * eachSide + 1;
        var expiration = Expiration(profile, utc);
        var candidates = (await gateway.GetAvailableStrikesAsync(profile, expiration, token))
            .Where(x => x > 0).Distinct().OrderBy(x => Math.Abs(x - spot)).ThenBy(x => x).ToArray();
        var verified = new List<OptionContractDescriptor>();
        decimal[] selected = [];
        var proven = false;
        // SecDef strike/expiry sets are not a Cartesian product. Verify nearby pairs,
        // and keep searching until no unexamined candidate could change the selection.
        var bound = Math.Min(128, candidates.Length);
        for (var offset = 0; offset < bound; offset += 8)
        {
            var batch = candidates.Skip(offset).Take(Math.Min(8, bound - offset)).ToArray();
            var contracts = await gateway.ResolveContractsAsync(profile, expiration, batch, token);
            verified.AddRange(contracts.Where(c => c.ConId > 0 && c.Multiplier > 0
                && c.Expiration == expiration && c.Ticker == profile.Ticker
                && c.TradingClass == profile.PreferredTradingClass && batch.Contains(c.StrikeUsd)));
            var pairs = verified.GroupBy(x => x.StrikeUsd)
                .Where(g => g.Select(x => x.Right).Distinct().Count() == 2).Select(g => g.Key);
            selected = SelectSymmetric(pairs, spot, eachSide);
            if (selected.Length == strikeCount && (offset + batch.Length >= candidates.Length
                || Math.Abs(candidates[offset + batch.Length] - spot) > selected.Max(x => Math.Abs(x - spot))))
            { proven = true; break; }
        }
        if (!proven) throw new IbOptionGatewayException("NO_CONTRACTS", $"未能验证 ATM 上下各 {eachSide} 档、共 {strikeCount} 档完整合约（最多检查 128 个候选）；不缩小范围或更换到期日。");
        var result = new List<ProbeContract>();
        foreach (var c in verified.Where(x => selected.Contains(x.StrikeUsd)).DistinctBy(x => x.ConId))
        {
            var segment = IbTradingHoursParser.Parse(c.TradingHours, c.TradingTimeZoneId,
                profile.IncludeGlobalTradingHours).FirstOrDefault(x => x.Contains(utc));
            if (segment == default) throw new IbOptionGatewayException("CLOSED", "IB 合约时段当前未开放或无法确认；不进行后台等待。");
            var session = NyseTradingCalendar.IsRegularTradingHours(utc) ? "RTH" : "GTH";
            result.Add(new(c, segment, session, spot, selected[eachSide]));
        }
        if (result.Count != strikeCount * 2 || result.Select(x => (x.Contract.StrikeUsd, x.Contract.Right)).Distinct().Count() != strikeCount * 2)
            throw new IbOptionGatewayException("NO_CONTRACTS", "合约身份不完整或不唯一。");
        return result.ToArray();
    }
}
