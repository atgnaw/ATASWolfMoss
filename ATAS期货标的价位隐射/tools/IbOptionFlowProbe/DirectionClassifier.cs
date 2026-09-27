namespace IbOptionFlowProbe;

internal static class DirectionClassifier
{
    public static bool IsMid(string reason) => reason is "LOCKED_QUOTE" or "MIDPOINT_UNKNOWN" or "MID_TIME_AGREE";
    public static (int Sign, string Reason) Classify(AlignmentEvidence evidence, decimal price, string method, int maximumAgeMs, int tickSign, string tickUnknown = "TICK_NO_PREVIOUS", bool classifyOutside = false)
    {
        var q = evidence.Quote;
        var error = evidence.Error ?? (q == null ? "MISSING_QUOTE_HISTORY" : q.DataType != 1 ? "NOT_CONFIRMED_REALTIME"
            : q.Bid == null || q.Ask == null ? "MISSING_QUOTE"
            : q.Bid.Invalid || q.Ask.Invalid ? (q.Bid.Price > 0 && q.Ask.Price > 0 ? "QUOTE_FLAGGED" : "INVALID_QUOTE")
            : q.BidSize <= 0 || q.AskSize <= 0 ? "INVALID_QUOTE_SIZE"
            : q.Bid.Price == q.Ask.Price ? "LOCKED_QUOTE"
            : q.Bid.Price > q.Ask.Price ? "CROSSED_QUOTE"
            : evidence.BidAgeMs is null or < 0 || evidence.AskAgeMs is null or < 0 ? "INVALID_ARRIVAL_CLOCK"
            : maximumAgeMs >= 0 && (evidence.BidAgeMs > maximumAgeMs || evidence.AskAgeMs > maximumAgeMs) ? "QUOTE_TOO_OLD" : null);
        if (error != null) return (0, error);
        if (price == q!.Ask!.Price) return (1, "AT_ASK");
        if (price == q.Bid!.Price) return (-1, "AT_BID");
        if (price < q.Bid.Price || price > q.Ask.Price)
            return classifyOutside ? (price > q.Ask.Price ? 1 : -1, price > q.Ask.Price ? "ABOVE_ASK_OVERRIDE" : "BELOW_BID_OVERRIDE") : (0, "OUTSIDE_QUOTE");
        if (method == "AtQuote") return (0, "INSIDE_SPREAD");
        var middle = q.Bid.Price + (q.Ask.Price - q.Bid.Price) / 2;
        if (price > middle) return (1, "INSIDE_ABOVE_MIDPOINT");
        if (price < middle) return (-1, "INSIDE_BELOW_MIDPOINT");
        if (method == "MidpointTick") return tickSign != 0 ? (tickSign, "MIDPOINT_TICK") : (0, tickUnknown);
        return (0, "MIDPOINT_UNKNOWN");
    }
}
