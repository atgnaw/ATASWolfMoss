namespace WolfMoss.ATAS.PriceMapping;

using System.Diagnostics;
using WolfMoss.ATAS.PriceMapping.Core;

internal sealed partial class ReflectionIbOptionGatewayClient
{
    private Func<OptionContractDescriptor, OptionBuyStrengthCalendar>? _strengthCalendar;
    private readonly Dictionary<long, (OptionContractDescriptor Contract, OptionBuyStrengthState State, DateTime End)> _retiredStrength = new();
    public void ConfigureBuyStrengthCalendar(Func<OptionContractDescriptor, OptionBuyStrengthCalendar> calendar)
        => Volatile.Write(ref _strengthCalendar, calendar);
    public OptionBuyStrength ReadBuyStrength(long conId, DateTime start, DateTime end, DateTime now)
    {
        OptionBuyStrengthState? state;
        lock (_sync) state = _subscriptionsByConId.GetValueOrDefault(conId)?.Strength;
        return state?.Read(start, end, now) ?? OptionBuyStrength.Missing;
    }
    public OptionBuyStrength ReadBuyStrength(string ticker, DateOnly expiry, decimal strike, OptionRight right,
        DateTime start, DateTime end, DateTime now)
    {
        OptionBuyStrengthState? active = null, retired = null;
        bool Matches(OptionContractDescriptor c) => c.Ticker == ticker && c.Expiration == expiry && c.StrikeUsd == strike && c.Right == right;
        lock (_sync)
        {
            foreach (var key in _retiredStrength.Where(p => now - p.Value.End > TimeSpan.FromMinutes(30)).Select(p => p.Key).ToArray()) _retiredStrength.Remove(key);
            foreach (var sub in _subscriptionsByConId.Values) if (Matches(sub.Contract)) { active = sub.Strength; break; }
            foreach (var value in _retiredStrength.Values) if (Matches(value.Contract) && end <= value.End) { retired = value.State; break; }
        }
        var result = active?.Read(start, end, now);
        return result is not null && result != OptionBuyStrength.Missing ? result : retired?.Read(start, end, now) ?? OptionBuyStrength.Missing;
    }
    private void RetireBuyStrength(SharedSubscription sub, DateTime now)
    {
        if (sub.Strength is not { } state) return;
        state.Stop(now);
        _retiredStrength[sub.Contract.ConId] = (sub.Contract, state, now);
        while (_retiredStrength.Count > 512) _retiredStrength.Remove(_retiredStrength.MinBy(p => p.Value.End).Key);
    }

    // Synchronous reception before chart fanout. No raw observer or quote history required.
    private void ObserveBuyStrength(string kind, object?[] args)
    {
        try
        {
            if (kind == "error" && IbErrorCallbackParser.TryParse(args, out var recovery)
                && recovery.ErrorCode is 1102 or 2104)
            {
                foreach (var subscription in _subscriptionsByRequest.Values)
                    if (subscription.Strength?.AwaitingMarketType == true)
                        subscription.StrengthRecoveryAttempts = 0;
                return;
            }
            if (kind == "connectionClosed" || kind == "error" && IbErrorCallbackParser.TryParse(args, out var error)
                && error.ErrorCode is 1100 or 1101 or 1300 or 2103)
            {
                foreach (var subscription in _subscriptionsByRequest.Values)
                    subscription.Strength?.Start(subscription.RequestId, DateTime.UtcNow);
                return;
            }
            if (kind is not ("tickPrice" or "tickSize" or "tickString" or "marketDataType")
                || args.Length < 2 || !TryInt32(args[0], out var request) || !TryInt32(args[1], out var field)
                || !_subscriptionsByRequest.TryGetValue(request, out var sub) || sub.Strength is not { } state) return;
            var now = DateTime.UtcNow; var ticks = Stopwatch.GetTimestamp();
            if (kind == "marketDataType")
            {
                state.MarketType(request, field, now, ticks);
                if (field is >= 1 and <= 4) sub.StrengthRecoveryAttempts = 0;
                return;
            }
            if (args.Length < 3) return;
            if (kind == "tickString")
            {
                if (field == 48 && args[2] is string text) state.Trade(request, text, now, ticks);
                return;
            }
            if (kind == "tickPrice" && field is not (1 or 2 or 66 or 67)
                || kind == "tickSize" && field is not (0 or 3 or 69 or 70)) return;
            var valid = TryDecimal(args[2], out var value);
            var flagged = kind == "tickPrice" && args.Length > 3 && args[3] is { } attributes
                && (ReadBoolean(attributes, "PastLimit") || ReadBoolean(attributes, "PreOpen"));
            state.Quote(request, field, valid ? value : null, flagged, kind == "tickSize", now, ticks);
        }
        catch
        {
            // Never silently continue a supposedly complete bucket after an ingress failure.
            if (args.Length > 0 && TryInt32(args[0], out var id) && _subscriptionsByRequest.TryGetValue(id, out var sub))
                sub.Strength?.Start(id, DateTime.UtcNow);
        }
    }
}
