namespace WolfMoss.ATAS.PriceMapping.Core;

using System.Globalization;

public static class UiTimeZoneFormatter
{
    public static DateTime ConvertFromUtc(DateTime utcValue, decimal offsetHours)
        => utcValue.AddMinutes((double)(offsetHours * 60m));

    public static string FormatOffsetLabel(decimal offsetHours)
    {
        var sign = offsetHours >= 0m ? "+" : "-";
        var absolute = Math.Abs(offsetHours).ToString(
            "0.##",
            CultureInfo.InvariantCulture);
        return $"UTC{sign}{absolute}";
    }
}
