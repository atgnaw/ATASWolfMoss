using WolfMoss.ATAS.PriceMapping;

namespace IbOptionFlowProbe;

// Subscription metadata is distinct from quote state. Only a same-epoch farm
// interruption may retain a previously explicit type=1; hard breaks discard it.
internal sealed class FarmRecovery
{
    private readonly Dictionary<(long Contract, int Request), int> _types = new();
    private readonly HashSet<(long Contract, int Request)> _pending = new();
    public bool Blocked { get; private set; }
    public void Clear() { _types.Clear(); _pending.Clear(); Blocked = false; }
    public void Disconnect()
    {
        Blocked = true;
        foreach (var (key, type) in _types) if (type == 1) _pending.Add(key);
    }
    public bool Reconnect() { var was = Blocked; Blocked = false; return was; }
    public bool Observe(IbRawMarketEvent raw)
    {
        if (raw.ErrorCode is 354 or 10167 or 10168 or 10186)
            foreach (var key in _types.Keys.Where(k => k.Request == raw.RequestId).ToArray())
            { _types.Remove(key); _pending.Remove(key); }
        if (raw.Contract is not { } c) return false;
        var keyNow = (c.ConId, raw.RequestId);
        foreach (var key in _types.Keys.Where(k => k.Contract == c.ConId && k.Request != raw.RequestId).ToArray())
        { _types.Remove(key); _pending.Remove(key); }
        if (raw.Kind == "marketDataType")
        {
            _types[keyNow] = raw.MarketDataType ?? 0;
            _pending.Remove(keyNow);
            if (Blocked && raw.MarketDataType == 1) _pending.Add(keyNow);
            return false;
        }
        if (raw.Kind is "tickPrice" or "tickSize" && raw.Field is 66 or 67 or 69 or 70)
        { _types[keyNow] = 3; _pending.Remove(keyNow); }
        // Restore metadata only on a fresh ordinary price/size callback after 2104.
        return !Blocked && raw.Kind is "tickPrice" or "tickSize"
            && _types.GetValueOrDefault(keyNow) == 1 && _pending.Remove(keyNow);
    }
}
