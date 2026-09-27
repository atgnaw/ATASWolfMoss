using System.Text.Json;

namespace IbOptionFlowProbe;

internal sealed record Inspection(long Records, long InvalidRecords, bool TruncatedTail,
    bool AbnormalEnd, long ParseErrors);
internal static class CaptureInspector
{
    // Streaming and bounded-memory. A malformed non-terminal line is corruption,
    // not silently treated as an interrupted trailing write.
    public static async Task<Inspection> InspectAsync(string directory)
    {
        var files = Directory.GetFiles(directory, "events-*.jsonl").Order(StringComparer.Ordinal).ToArray();
        long records = 0, invalid = 0, parseErrors = 0;
        Guid? run = null;
        long lastSequence = 0;
        var truncated = false;
        for (var i = 0; i < files.Length; i++)
        {
            using var reader = new StreamReader(files[i]);
            var line = await reader.ReadLineAsync();
            while (line != null)
            {
                var next = await reader.ReadLineAsync();
                try
                {
                    var item = JsonSerializer.Deserialize<ProbeEvent>(line, ProbeJson.Options);
                    if (item == null || item.Raw == null || item.SchemaVersion != 1) invalid++;
                    else
                    {
                        run ??= item.RunId;
                        if (item.RunId != run || item.Raw.Sequence != lastSequence + 1) invalid++;
                        lastSequence = item.Raw.Sequence;
                        records++;
                        if (item.ParseError != null) parseErrors++;
                    }
                }
                catch (JsonException)
                {
                    if (i == files.Length - 1 && next == null) truncated = true;
                    else invalid++;
                }
                line = next;
            }
        }
        var summaryFile = Path.Combine(directory, "capture-summary.json");
        CaptureSummary? summary = null;
        if (File.Exists(summaryFile))
            try { summary = JsonSerializer.Deserialize<CaptureSummary>(await File.ReadAllTextAsync(summaryFile), ProbeJson.Options); }
            catch (JsonException) { invalid++; }
        return new(records, invalid, truncated, summary == null || !summary.Complete || truncated || invalid > 0
            || records != summary.Written || run.HasValue && run.Value != summary.RunId, parseErrors);
    }
}
