namespace WolfMoss.ATAS.PriceMapping.Core;

public static class MinuteCloseMatcher
{
    public static bool TryFindClose(
        IEnumerable<MarketTradeSample> trades,
        DateTime minuteStart,
        out decimal close)
    {
        var minuteEnd = minuteStart.AddMinutes(1);
        var found = false;
        var lastTime = DateTime.MinValue;
        close = 0m;

        foreach (var trade in trades)
        {
            if (trade.Price <= 0m
                || trade.MarketTime < minuteStart
                || trade.MarketTime >= minuteEnd)
            {
                continue;
            }

            if (found && trade.MarketTime < lastTime)
                continue;

            found = true;
            lastTime = trade.MarketTime;
            close = trade.Price;
        }

        return found;
    }
}
