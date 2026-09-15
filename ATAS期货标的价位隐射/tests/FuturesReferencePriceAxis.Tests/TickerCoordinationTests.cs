using System.Reflection;
using WolfMoss.ATAS.PriceMapping;
using WolfMoss.ATAS.PriceMapping.Core;

internal static class TickerCoordinationTests
{
    private static readonly DateTime Start = new(2026, 9, 8, 13, 30, 0, DateTimeKind.Utc);
    private static readonly DateOnly Expiration = new(2026, 9, 8);
    private static void Check(bool condition, string text) { if (!condition) throw new Exception(text); }
    public static void Cadence()
    {
        var c = new OptionTickerCoordinator();
        OptionTickerCoordinator.Plan Update(long owner, int minutes, DateTime now, decimal spot = 710)
            => c.Update(owner, "QQQ", Expiration, spot, now, minutes, true, now, Start);
        var first = Update(1, 5, Start);
        var three = Update(2, 3, Start.AddMinutes(1), 720);
        Check(three.CadenceMinutes == 3 && three.NextSwitchUtc == Start.AddMinutes(3) && three.Spot == 710, "3/5 share next 3-minute boundary, not an immediate move");
        var one = Update(3, 1, Start.AddMinutes(1).AddSeconds(10));
        Check(one.NextSwitchUtc == Start.AddMinutes(2), "Shorter cadence takes next future boundary");
        c.Remove(3);
        var removed = Update(1, 5, Start.AddMinutes(1).AddSeconds(20));
        Check(removed.CadenceMinutes == 3 && removed.NextSwitchUtc == Start.AddMinutes(3), "Removing shortest owner reschedules without a new lock");
        Update(1, 5, Start.AddMinutes(3).AddSeconds(-1), 712);
        var boundary = Update(2, 3, Start.AddMinutes(3), 750);
        Check(boundary.Spot == 712 && boundary.NextSwitchUtc == Start.AddMinutes(6), "Follower cannot choose its own ATM");
        Check(!c.Commit(first, () => throw new Exception("stale")), "Old discovery cannot commit after an epoch change");
    }
    public static void MasterAndIsolation()
    {
        var c = new OptionTickerCoordinator();
        var first = c.Update(1, "QQQ", Expiration, 710, Start, 1, true, Start, Start);
        var backup = c.Update(2, "QQQ", Expiration, 720, Start.AddSeconds(31), 5, true, Start.AddSeconds(31), Start);
        Check(backup.Master == 2 && backup.Spot == 710, "Stale master failover does not move locked ATM immediately");
        var moved = c.Update(2, "QQQ", Expiration, 721, Start.AddMinutes(1), 5, true, Start.AddMinutes(1), Start);
        Check(moved.Spot == 721, "Fresh backup supplies next common boundary");
        var stale = c.Update(2, "QQQ", Expiration, 999, Start.AddMinutes(1), 5, true, Start.AddMinutes(2), Start);
        Check(stale.MasterStale && stale.Spot == 721, "Polling cannot freshen an old quote");
        var spx = c.Update(3, "SPX", Expiration, 7700, Start, 10, true, Start, Start);
        Check(spx.Spot == 7700 && spx.CadenceMinutes == 10, "Ticker isolation");
        c.Remove(1); c.Remove(2);
        var newTarget = c.Update(4, "QQQ", Expiration.AddDays(1), 730, Start, 5, true, Start, Start);
        Check(newTarget.Spot == 730 && !c.Commit(first, () => { }), "Last release and expiration invalidate old state");
        foreach (var minutes in new[] { 1, 3, 5, 10 })
            Check(OptionTickerCoordinator.NextBoundary(Start.AddMinutes(minutes), Start, minutes) == Start.AddMinutes(minutes * 2), "Strict future alignment");
        var replacement = new OptionTickerCoordinator().Update(5, "QQQ", Expiration, 720, Start, 1, true, Start, Start);
        Check(replacement.Revision > newTarget.Revision, "Socket replacement must not roll shared Flow range revisions backwards");
    }
    public static void Budget()
    {
        var b = new OptionFlowLineBudget();
        b.Update(1, "QQQ", 21, 84); b.Update(2, "QQQ", 5, 84); b.Update(3, "SPX", 21, 84);
        Check(b.AllocatedLevels("QQQ") == 21 && b.AllocatedLevels("SPX") == 21, "Small chart cannot shrink shared range");
        b.Remove(1);
        Check(b.AllocatedLevels("QQQ") == 5 && b.AllocatedLevels("SPX") == 21, "Largest owner release shrinks demand");
        b.Update(2, "QQQ", 21, 42);
        Check(b.AllocatedLevels("QQQ") == 9 && b.AllocatedLevels("SPX") == 9, "Symmetric budget reduction");
    }
    public static void FencedSubscriptions() => FencedAsync().GetAwaiter().GetResult();
    private static async Task FencedAsync()
    {
        var (client, socket) = IbCoordinationTests.Connected();
        await using var cleanup = client;
        OptionContractDescriptor Contract(int id) => new(id, "QQQ", Expiration, 700 + (id - 1) / 2,
            id % 2 == 1 ? OptionRight.Call : OptionRight.Put, "QQQ", "SMART", 100, "");
        var old = Enumerable.Range(1, 42).Select(Contract).ToArray();
        var moved = Enumerable.Range(3, 42).Select(Contract).ToArray();
        var exits = 0;
        void Receive(IbOptionMarketDataUpdate update) { if (update.ErrorCode == "RANGE_CHANGED") exits++; }
        client.ApplyTickerRange("QQQ", Expiration, old);
        using var a = await client.SubscribeAsync(old, new(true, true, false), Receive, 42, default);
        using var b = await client.SubscribeAsync(old, new(true, true, false), Receive, 42, default);
        client.ApplyTickerRange("QQQ", Expiration, moved);
        Check(exits == 4 && client.ActiveMarketDataLines == 40, "All owners notified and old edge reservations freed");
        await a.UpdateAsync(moved, new(true, true, false), 42, default);
        Check(socket.MaximumLines == 42 && client.ActiveMarketDataLines == 42, "New range fits even while follower holds stale IDs");
        try { await b.UpdateAsync(old, new(true, true, false), 42, default); throw new Exception("Expected stale range rejection"); }
        catch (IbOptionGatewayException error) { Check(error.Code == "RANGE_CHANGED", "Stale follower not misclassified as account limit"); }
        await b.UpdateAsync(moved, new(true, true, false), 42, default);
        a.Dispose();
        Check(client.ActiveMarketDataLines == 42, "One owner release preserves the other");
    }
}
