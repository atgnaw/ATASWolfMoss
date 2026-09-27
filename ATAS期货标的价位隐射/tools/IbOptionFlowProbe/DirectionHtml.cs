using System.Text.Json;
namespace IbOptionFlowProbe;

internal static class DirectionHtml
{
    internal static string Resource(string name)
    {
        using var stream = typeof(DirectionHtml).Assembly.GetManifestResourceStream("IbOptionFlowProbe." + name)
            ?? throw new InvalidOperationException("Missing embedded report asset: " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    public static string Render(DirectionReport report, DirectionReport? outside = null)
    {
        // The downloadable JSON and trace retain full evidence. Avoid embedding redundant
        // per-scenario pressure arrays and positive-reason dictionaries in the browser page.
        var payload = new { report.Settings, report.Diagnostics, Totals = report.Totals.Select(ViewRow),
            Buckets = report.Buckets.Select(ViewRow), Reasons = UnknownReasons.All,
            Outside = outside == null ? null : new { Totals = outside.Totals.Select(ViewRow), Buckets = outside.Buckets.Select(ViewRow) } };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        return "<style>" + Resource("ReportView.css") + "</style>" + Resource("ReportView.html")
            + "<script id=\"robust-data\" type=\"application/json\">" + json + "</script>"
            + "<script type=\"text/javascript\">" + Resource("ReportView.js") + "</script>";
    }
    private static object ViewAmount(DirectionAmounts a)
    {
        var m = a.Metrics;
        return new { a.Buy, a.Sell, a.Mid, a.Unknown, a.UnknownPremiums, a.UnknownCounts, a.UnresolvedGroups,
            Metrics = new { m.Total, m.Tendency, m.Coverage, m.Lower, m.Upper, m.ErrorTolerance } };
    }
    private static object ViewRow(DirectionBucket r) => new
    {
        r.Contract, r.Scope, r.Session, r.IntervalMinutes, r.StartUtc, r.EndUtc, r.Events, r.Assessable,
        r.HighestPassedPressure, r.Quality, Primary = ViewAmount(r.Primary),
        Scenarios = r.Scenarios.ToDictionary(x => x.Key, x => ViewAmount(x.Value)),
        Consensus = r.Consensus.ToDictionary(x => x.Key, x => ViewAmount(x.Value)), r.TimeConflictPremiums,
        r.LargestFraction, r.TopFiveFraction, r.ReconciliationVolumeCoverage, r.UnexplainedVolume, r.PremiumDifference
    };
}
