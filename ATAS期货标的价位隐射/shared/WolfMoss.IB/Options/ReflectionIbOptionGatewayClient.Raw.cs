using System.Diagnostics;
using System.Globalization;
using WolfMoss.ATAS.PriceMapping.Core;

namespace WolfMoss.ATAS.PriceMapping;

// Market-data-only ingress record. No account callbacks or unfiltered error text.
internal sealed record IbRawMarketEvent(long Sequence, DateTime ReceivedUtc, long MonotonicTicks,
    string Kind, int RequestId, OptionContractDescriptor? Contract, int? Field = null,
    string? Value = null, bool? CanAutoExecute = null, bool? PastLimit = null,
    bool? PreOpen = null, int? MarketDataType = null, int? ErrorCode = null);

internal sealed partial class ReflectionIbOptionGatewayClient
{
    private Action<IbRawMarketEvent>? _rawObserver;
    private readonly object _rawObservationSync = new();
    private long _rawSequence;
    private long _rawObservationFailures;
    public long RawObservationFailures => Interlocked.Read(ref _rawObservationFailures);

    // One opt-in observer per private client. The ordinary plugin never sets it.
    internal void SetRawObserver(Action<IbRawMarketEvent>? observer)
        => Volatile.Write(ref _rawObserver, observer);

    internal void ObserveRawCallback(string kind, object?[] args)
    {
        var observer = Volatile.Read(ref _rawObserver);
        if (observer == null) return; // No record allocations on the disabled path.
        if (kind is not ("tickPrice" or "tickSize" or "tickString" or "marketDataType"
            or "error" or "connectionClosed" or "nextValidId")) return;
        var utc = DateTime.UtcNow;
        var monotonic = Stopwatch.GetTimestamp();
        try
        {
            var request = -1;
            int? field = null, dataType = null, error = null;
            string? value = null;
            bool? auto = null, past = null, pre = null;
            if (kind == "error")
            {
                if (!IbErrorCallbackParser.TryParse(args, out var parsed)) return;
                request = parsed.RequestId;
                error = parsed.ErrorCode; // Never copy Message (may contain account identifiers).
            }
            else if (kind is "tickPrice" or "tickSize" or "tickString" or "marketDataType")
            {
                if (args.Length < 2 || !TryInt32(args[0], out request) || !TryInt32(args[1], out var f)) return;
                if (kind == "marketDataType") dataType = f;
                else
                {
                    if (args.Length < 3) return;
                    // Quotes, quote sizes and the two RT trade scopes only.
                    if (kind == "tickPrice" && f is not (1 or 2 or 66 or 67)
                        || kind == "tickSize" && f is not (0 or 3 or 69 or 70)
                        || kind == "tickString" && f is not (48 or 77)) return;
                    field = f;
                    value = Convert.ToString(args[2], CultureInfo.InvariantCulture);
                    if (kind == "tickPrice" && args.Length > 3 && args[3] is { } attributes)
                    {
                        auto = ReadBoolean(attributes, "CanAutoExecute");
                        past = ReadBoolean(attributes, "PastLimit");
                        pre = ReadBoolean(attributes, "PreOpen");
                    }
                }
            }
            var contract = _subscriptionsByRequest.TryGetValue(request, out var subscription)
                ? (OptionContractDescriptor?)subscription.Contract : null;
            lock (_rawObservationSync)
                observer(new(++_rawSequence, utc, monotonic, kind,
                    request, contract, field, value, auto, past, pre, dataType, error));
        }
        catch { Interlocked.Increment(ref _rawObservationFailures); }
    }
}
