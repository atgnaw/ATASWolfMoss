namespace WolfMoss.ATAS.PriceMapping.Core;

using System.Globalization;

public static class OptionPresentation
{
    public const string UnknownDataMarker = "?";
    public const string NoEventsMarker = "·";

    public static decimal GetFillRatio(decimal? value, decimal maximum)
    {
        if (!value.HasValue || value.Value <= 0m || maximum <= 0m)
            return 0m;

        return Math.Clamp(value.Value / maximum, 0m, 1m);
    }

    public static decimal GetOpenInterestMaximum(IReadOnlyList<OptionStrikeRow> rows)
        => rows.SelectMany(static row => new decimal[]
            {
                row.CallOpenInterest ?? 0L,
                row.PutOpenInterest ?? 0L
            })
            .DefaultIfEmpty(0m)
            .Max();

    public static decimal GetPremiumMaximum(IReadOnlyList<OptionStrikeRow> rows)
        => rows.SelectMany(static row => new[]
            {
                row.CallPremium ?? 0m,
                row.PutPremium ?? 0m
            })
            .DefaultIfEmpty(0m)
            .Max();

    public static string FormatCompact(decimal value)
    {
        var absolute = Math.Abs(value);
        var (scaled, suffix) = absolute switch
        {
            >= 1_000_000_000m => (absolute / 1_000_000_000m, "B"),
            >= 1_000_000m => (absolute / 1_000_000m, "M"),
            >= 1_000m => (absolute / 1_000m, "K"),
            _ => (absolute, string.Empty)
        };
        var prefix = value < 0m ? "-" : string.Empty;
        return prefix + scaled.ToString(
            scaled >= 100m ? "0" : scaled >= 10m ? "0.#" : "0.##",
            CultureInfo.InvariantCulture) + suffix;
    }

    public static bool IsFlowDataUnavailable(OptionDataStatus status)
        => status is OptionDataStatus.NoPermission
            or OptionDataStatus.LineLimit
            or OptionDataStatus.Frozen;

    public static string GetFlowMissingMarker(OptionDataStatus status)
        => IsFlowDataUnavailable(status)
            ? UnknownDataMarker
            : NoEventsMarker;

    public static string GetFlowCoverageLabel(
        OptionDataStatus status,
        bool hasValue,
        bool isPartial)
    {
        if (hasValue)
            return isPartial ? "PARTIAL" : "FULL";

        return IsFlowDataUnavailable(status) ? "NO DATA" : "NO EVENTS";
    }
}
