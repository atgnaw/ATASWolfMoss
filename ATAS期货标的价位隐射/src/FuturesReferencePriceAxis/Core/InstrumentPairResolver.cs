namespace WolfMoss.ATAS.PriceMapping.Core;

public static class InstrumentPairResolver
{
    private static readonly InstrumentPair NqQqq =
        new("NQ", "QQQ", "QQQ", 2);

    private static readonly InstrumentPair MnqQqq =
        new("MNQ", "QQQ", "QQQ", 2);

    private static readonly InstrumentPair EsSpx =
        new("ES", "SPX", "^GSPC", 2);

    private static readonly InstrumentPair MesSpx =
        new("MES", "SPX", "^GSPC", 2);

    public static bool TryResolve(PairMode mode, string? instrument, out InstrumentPair pair)
    {
        var normalized = Normalize(instrument);

        if (mode == PairMode.NqQqq)
        {
            pair = HasRoot(normalized, "MNQ") ? MnqQqq : NqQqq;
            return true;
        }

        if (mode == PairMode.EsSpx)
        {
            pair = HasRoot(normalized, "MES") ? MesSpx : EsSpx;
            return true;
        }

        if (HasRoot(normalized, "MNQ"))
        {
            pair = MnqQqq;
            return true;
        }

        if (HasRoot(normalized, "NQ"))
        {
            pair = NqQqq;
            return true;
        }

        if (HasRoot(normalized, "MES"))
        {
            pair = MesSpx;
            return true;
        }

        if (HasRoot(normalized, "ES"))
        {
            pair = EsSpx;
            return true;
        }

        pair = default;
        return false;
    }

    private static string Normalize(string? instrument)
    {
        var value = (instrument ?? string.Empty).Trim().ToUpperInvariant();
        return value.StartsWith('/') ? value[1..] : value;
    }

    private static bool HasRoot(string value, string root)
    {
        if (!value.StartsWith(root, StringComparison.Ordinal))
            return false;

        if (value.Length == root.Length)
            return true;

        var next = value[root.Length];
        return char.IsDigit(next)
            || char.IsWhiteSpace(next)
            || next is '@' or '-' or '_' or '.'
            || IsFuturesMonthCode(next);
    }

    private static bool IsFuturesMonthCode(char value)
        => value is 'F' or 'G' or 'H' or 'J' or 'K' or 'M'
            or 'N' or 'Q' or 'U' or 'V' or 'X' or 'Z';
}
