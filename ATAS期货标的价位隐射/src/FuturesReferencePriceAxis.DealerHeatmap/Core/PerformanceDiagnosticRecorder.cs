namespace WolfMoss.ATAS.PriceMapping.Core;

using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

// Only explicitly allowlisted configuration is accepted: never a settings object / exception.
public sealed record PerformanceRunMetadata(string BuildId, string BucketMode, int IntervalMinutes,
    int StrikeLevels, int LineBudget, bool ShowOi, bool ShowFlow)
{
    public int SchemaVersion => 5;
    public string TradeScope { get; init; } = "UNKNOWN";
}

public sealed class PerformanceDiagnosticRecorder : IAsyncDisposable
{
    private static readonly SemaphoreSlim FileGate = new(1, 1);
    private static readonly ConcurrentDictionary<string, byte> ActivePrefixes = new(StringComparer.OrdinalIgnoreCase);
    public const long FileLimit = 10 * 1024 * 1024;
    public const long DirectoryLimit = 100 * 1024 * 1024;
    private readonly Channel<PerformanceSnapshot> _pending = Channel.CreateBounded<PerformanceSnapshot>(
        new BoundedChannelOptions(120) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task _writer;
    private long _dropped;
    private string _state = "STARTING";
    public long Dropped => Interlocked.Read(ref _dropped);
    public string State => Volatile.Read(ref _state);
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "WolfMoss", "FuturesReferencePriceAxis", "diagnostics");
    private static readonly JsonSerializerOptions JsonOptions = new()
    { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, Converters = { new JsonStringEnumConverter() } };

    public PerformanceDiagnosticRecorder(string directory, PerformanceRunMetadata metadata)
        => _writer = Task.Run(() => RunAsync(directory, metadata));

    public bool TryRecord(PerformanceSnapshot snapshot)
    {
        if (State == "WRITE_ERROR" || !_pending.Writer.TryWrite(snapshot))
        { Interlocked.Increment(ref _dropped); return false; }
        return true;
    }

    private async Task RunAsync(string directory, PerformanceRunMetadata metadata)
    {
        string? activePrefix = null;
        var bufferedRows = 0L;
        try
        {
            Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Diagnostic directory must not be a link.");
            var runId = $"perf-{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}";
            activePrefix = Path.Combine(Path.GetFullPath(directory), runId);
            ActivePrefixes.TryAdd(activePrefix, 0);
            var metaPath = Path.Combine(directory, runId + ".json");
            await FileGate.WaitAsync().ConfigureAwait(false);
            try
            {
                Prune(directory, DateTime.UtcNow);
                var json = JsonSerializer.Serialize(metadata, JsonOptions);
                EnsureCapacity(directory, Encoding.UTF8.GetByteCount(json));
                await File.WriteAllTextAsync(metaPath, json);
            }
            finally { FileGate.Release(); }
            var part = 0;
            var activePath = Path.Combine(directory, $"{runId}-{part:D4}.csv");
            var lastFlush = Stopwatch.GetTimestamp();
            var buffer = new StringBuilder();
            Volatile.Write(ref _state, "RECORDING");
            while (await _pending.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_pending.Reader.TryRead(out var snapshot)) { buffer.AppendLine(ToCsv(snapshot)); bufferedRows++; }
                if (Stopwatch.GetElapsedTime(lastFlush) < TimeSpan.FromSeconds(10) && !_pending.Reader.Completion.IsCompleted)
                {
                    // Timer also flushes when there is no next market/diagnostic sample.
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(1,
                        (TimeSpan.FromSeconds(10) - Stopwatch.GetElapsedTime(lastFlush)).TotalMilliseconds)));
                    var timerExpired = false;
                    try { await _pending.Reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { timerExpired = true; }
                    // Timer rounding can wake slightly before 10s. Never go back to
                    // an untimed read with buffered rows after the timer has fired.
                    if (!timerExpired && !_pending.Reader.Completion.IsCompleted
                        && Stopwatch.GetElapsedTime(lastFlush) < TimeSpan.FromSeconds(10)) continue;
                }
                await FlushAsync().ConfigureAwait(false);
            }
            await FlushAsync().ConfigureAwait(false);
            Volatile.Write(ref _state, "STOPPED");

            async Task FlushAsync()
            {
                if (buffer.Length == 0) return;
                await FileGate.WaitAsync().ConfigureAwait(false);
                try
                {
                Prune(directory, DateTime.UtcNow);
                if (File.Exists(activePath) && new FileInfo(activePath).Length + Encoding.UTF8.GetByteCount(buffer.ToString()) > FileLimit)
                    activePath = Path.Combine(directory, $"{runId}-{++part:D4}.csv");
                var isNew = !File.Exists(activePath);
                var bytes = Encoding.UTF8.GetByteCount(buffer.ToString()) + (isNew ? Encoding.UTF8.GetByteCount(Header) + 2 : 0);
                if (bytes > FileLimit) throw new IOException("Diagnostic batch limit.");
                EnsureCapacity(directory, bytes);
                // Keep the file open only during a bounded batch write; other runs cannot delete it mid-write.
                await using (var stream = new FileStream(activePath, FileMode.Append, FileAccess.Write, FileShare.Read))
                await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    if (isNew) await writer.WriteLineAsync(Header);
                    await writer.WriteAsync(buffer.ToString());
                }
                buffer.Clear();
                bufferedRows = 0;
                lastFlush = Stopwatch.GetTimestamp();
                Prune(directory, DateTime.UtcNow, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { activePath, metaPath });
                }
                finally { FileGate.Release(); }
            }
        }
        catch
        {
            Interlocked.Add(ref _dropped, bufferedRows);
            Volatile.Write(ref _state, "WRITE_ERROR");
            _pending.Writer.TryComplete();
            while (_pending.Reader.TryRead(out _)) Interlocked.Increment(ref _dropped);
        }
        finally { if (activePrefix != null) ActivePrefixes.TryRemove(activePrefix, out _); }
    }

    private static void EnsureCapacity(string directory, int bytes)
    {
        Prune(directory, DateTime.UtcNow, reservedBytes: bytes);
        var used = new DirectoryInfo(directory).EnumerateFiles("perf-*")
            .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0 && IsOwnedName(f.Name)).Sum(f => f.Length);
        if (used + bytes > DirectoryLimit) throw new IOException("Diagnostic directory limit.");
    }

    public const string Header = "utc,render_count,render_mean_ms,render_p95_upper_ms,publish_count,publish_mean_ms,publish_p95_upper_ms,callback_mean_ms,callback_p95_upper_ms,events_per_sec,gateway_sends_per_sec,gateway_cancels_per_sec,lines,budget,consumers,cached_samples,oi_values,gateway_replacements,background_tasks,diagnostic_samples_dropped,last_error_code,event_queue_length,market_data_gaps,atas_utc,atas_offset_seconds,source_utc,received_utc,source_age_seconds,bucket_start_utc,bucket_end_utc,coverage_start_utc,clock_jumps,clock_adjustment_seconds,flow_accepted,flow_rejected,flow_future,flow_before_coverage,flow_outside_ladder,flow_outside_session,flow_out_of_order,flow_invalid,flow_rolling_boundary,flow_late,last_disposition,ib_invalid_samples,last_error_origin,last_raw_error_code,last_error_utc,flow_ticker,flow_expiration,flow_duplicates,flow_observation_gaps,last_future_source_utc,last_future_received_utc,last_future_lead_ms,max_future_lead_ms"
        + ",flow_buffered,flow_buffer_overflow,future_pending,future_buffered,future_released,future_discarded,future_buffer_max_lead_ms";
    private static string Stamp(DateTime? utc) => utc?.ToString("O", CultureInfo.InvariantCulture) ?? "";
    public static string ToCsv(PerformanceSnapshot s) => string.Join(",", new object?[]
    { s.Utc.ToString("O", CultureInfo.InvariantCulture), s.Render.Count, s.Render.MeanMs, s.Render.P95UpperMs,
      s.Publish.Count, s.Publish.MeanMs, s.Publish.P95UpperMs, s.Callback.MeanMs, s.Callback.P95UpperMs,
      s.EventsPerSecond, s.SendsPerSecond, s.CancelsPerSecond, s.Lines, s.Budget, s.Consumers,
      s.CachedSamples, s.OiValues, s.GatewayReplacements, s.BackgroundTasks, s.DiagnosticSamplesDropped, s.LastErrorCode, "", "",
      Stamp(s.AtasUtc), (s.AtasUtc - s.Utc)?.TotalSeconds, Stamp(s.Reception?.SourceUtc), Stamp(s.Reception?.ReceivedUtc),
      (s.Utc - s.Reception?.SourceUtc)?.TotalSeconds, Stamp(s.BucketStartUtc), Stamp(s.BucketEndUtc), Stamp(s.CoverageStartUtc),
      s.ClockJumps, s.ClockAdjustmentSeconds, s.Reception?.Accepted, s.Reception?.Rejected, s.Reception?.Future,
      s.Reception?.BeforeCoverage, s.Reception?.OutsideLadder, s.Reception?.OutsideSession, s.Reception?.OutOfOrder,
      s.Reception?.Invalid, s.Reception?.RollingBoundary, s.Reception?.Late, s.Reception?.LastDisposition,
      s.InvalidIbSamples, s.LastErrorOrigin, s.LastRawErrorCode, Stamp(s.LastErrorUtc),
      s.FlowTicker is "QQQ" or "SPX" ? s.FlowTicker : "", s.FlowExpiration?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
      s.Reception?.Duplicates, s.Reception?.ObservationGaps,
      Stamp(s.Reception?.LastFutureSourceUtc), Stamp(s.Reception?.LastFutureReceivedUtc),
      s.Reception?.LastFutureLeadMilliseconds, s.Reception?.MaxFutureLeadMilliseconds,
      s.Reception?.Buffered, s.Reception?.BufferOverflow, s.FutureBuffer?.Pending, s.FutureBuffer?.Buffered,
      s.FutureBuffer?.Released, s.FutureBuffer?.Discarded, s.FutureBuffer?.MaxLeadMilliseconds }
        .Select(value => Convert.ToString(value, CultureInfo.InvariantCulture)));

    public static void Prune(string directory, DateTime utc, ISet<string>? protectedPaths = null, int reservedBytes = 0)
    {
        // Enumerate only owned filenames. Never recurse or follow links; unrelated files are untouched.
        var files = new DirectoryInfo(directory).EnumerateFiles("perf-*")
            .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0 && IsOwnedName(f.Name))
            .OrderBy(f => f.LastWriteTimeUtc).ToArray();
        var total = files.Sum(f => f.Length) + reservedBytes;
        foreach (var file in files)
        {
            if (protectedPaths?.Contains(file.FullName) == true) continue;
            if (ActivePrefixes.Keys.Any(prefix => file.FullName.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase)
                || string.Equals(file.FullName, prefix + ".json", StringComparison.OrdinalIgnoreCase))) continue;
            if (utc - file.LastWriteTimeUtc <= TimeSpan.FromDays(7) && total <= DirectoryLimit) continue;
            try { var length = file.Length; file.Delete(); total -= length; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    private static bool IsOwnedName(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var parts = stem.Split('-');
        return parts.Length is 3 or 4 && parts[0] == "perf"
            && DateTime.TryParseExact(parts[1], "yyyyMMddTHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            && Guid.TryParseExact(parts[2], "N", out _)
            && (parts.Length == 3 ? Path.GetExtension(name) == ".json"
                : parts[3].Length == 4 && int.TryParse(parts[3], out _) && Path.GetExtension(name) == ".csv");
    }
    public async ValueTask DisposeAsync()
    { _pending.Writer.TryComplete(); await _writer.ConfigureAwait(false); }
}
