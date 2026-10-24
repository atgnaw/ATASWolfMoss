namespace WolfMoss.ATAS.DeepMEffort.Core;

[Flags]
internal enum InputIssue { None = 0, Volume = 1, Time = 2, TickGrid = 4 }

internal readonly record struct CandleInput(decimal Open, decimal High, decimal Low, decimal Close,
    decimal Ask, decimal Bid, decimal Volume, decimal Betweens, DateTime Time, DateTime LastTime)
{
    public EffortBar ToBar(int index, decimal tickSize, DateTime nextTime, out InputIssue issues)
    {
        issues = InputIssue.None;
        var validVolume = Integral(Ask) && Integral(Bid) && Integral(Volume)
            && Bid <= Volume && Ask <= Volume - Bid && Ask + Bid == Volume && Betweens == 0;
        if (!validVolume) issues |= InputIssue.Volume;
        var validTime = Time != default && nextTime != default && nextTime >= Time
            && (LastTime == default || (LastTime >= Time && nextTime >= LastTime));
        if (!validTime) issues |= InputIssue.Time;
        var validOpen = TryTicks(Open, tickSize, out var openTicks);
        var validClose = TryTicks(Close, tickSize, out var closeTicks);
        if (!validOpen || !validClose || Math.Abs((decimal)closeTicks - openTicks) >= long.MaxValue)
        {
            issues |= InputIssue.TickGrid;
            openTicks = closeTicks = 0;
        }
        // DateTime ticks are 100 ns. Keep the source's microsecond, then integer-second truncation.
        var elapsed = validTime ? (nextTime.Ticks - Time.Ticks) / 10 : 0;
        return new(index, (double)Open, (double)High, (double)Low, (double)Close, openTicks, closeTicks,
            validVolume ? (long)Ask : 0, validVolume ? (long)Bid : 0, validVolume ? (long)Volume : 0,
            elapsed, validVolume && (issues & InputIssue.TickGrid) == 0, validTime);
    }

    private static bool Integral(decimal value) => value >= 0 && value <= long.MaxValue && decimal.Truncate(value) == value;

    private static bool TryTicks(decimal price, decimal tickSize, out long ticks)
    {
        ticks = 0;
        if (tickSize <= 0) return false;
        try
        {
            var value = price / tickSize;
            if (value < long.MinValue || value > long.MaxValue || decimal.Truncate(value) != value) return false;
            ticks = (long)value;
            return true;
        }
        catch (OverflowException) { return false; }
    }
}
