using System.Globalization;
using WolfMoss.ATAS.PriceMapping;

namespace IbOptionFlowProbe;

internal static class RawEventParser
{
    public static ProbeEvent Convert(Guid run, IbRawMarketEvent raw)
    {
        if (raw.Kind != "tickString") return new(1, run, 1, raw, null, null);
        var parts = raw.Value?.Split(';');
        if (parts is not { Length: 6 }
            || !Number(parts[0], out var price) || price <= 0
            || !Number(parts[1], out var size) || size < 0
            || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
            || !Number(parts[3], out var total) || total < 0
            || !Number(parts[4], out var vwap) || vwap < 0
            || !bool.TryParse(parts[5], out var single))
            return new(1, run, 1, raw, null, "INVALID_RT_FIELDS");
        try { return new(1, run, 1, raw, new(price, size, ms,
            DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime, total, vwap, single), null); }
        catch (ArgumentOutOfRangeException) { return new(1, run, 1, raw, null, "INVALID_SOURCE_TIME"); }
    }
    private static bool Number(string value, out decimal number)
        => decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
}
