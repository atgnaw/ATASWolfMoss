using System.Threading.Channels;

namespace WolfMoss.MarketData;

public sealed record MarketRecordingStatus(bool IsFaulted, bool IsStopped, int Queued,
    long Accepted, long Written, MarketRecordingLoss Loss);

// Explicit opt-in infrastructure. No filesystem, database or dependency on diagnostics.
public sealed class BufferedMarketEventSink : IMarketEventSink, IAsyncDisposable
{
    private readonly Channel<MarketEvent> _queue;
    private readonly IMarketEventBatchWriter _writer;
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval, _shutdownTimeout;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly object _disposeSync = new();
    private Task? _disposal;
    private int _stopped, _faulted;
    private long _accepted, _written, _congested, _captureFailed, _writeFailed, _abandoned, _lossRevision, _lastLossTicks;
    public Guid RecordingRunId { get; } = Guid.NewGuid();
    internal bool CanRecord => Volatile.Read(ref _faulted) == 0 && Volatile.Read(ref _stopped) == 0;

    public BufferedMarketEventSink(IMarketEventBatchWriter writer, int capacity = 1024, int batchSize = 64,
        TimeSpan? flushInterval = null, TimeSpan? shutdownTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _flushInterval = flushInterval ?? TimeSpan.FromMilliseconds(100);
        _shutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(2);
        if (capacity is < 1 or > 65536 || batchSize < 1 || batchSize > capacity
            || _flushInterval <= TimeSpan.Zero || _flushInterval > TimeSpan.FromSeconds(10)
            || _shutdownTimeout <= TimeSpan.Zero || _shutdownTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _writer = writer; _batchSize = batchSize;
        _queue = Channel.CreateBounded<MarketEvent>(new BoundedChannelOptions(capacity)
            { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
        _worker = Task.Run(RunAsync);
    }

    public MarketEventAcceptance TryPublish(MarketEvent value)
    {
        if (Volatile.Read(ref _faulted) != 0) return MarketEventAcceptance.Faulted;
        if (Volatile.Read(ref _stopped) != 0) return MarketEventAcceptance.Disabled;
        if (value.RecordingRunId != RecordingRunId || value.SchemaVersion != 1 || value.ReceiveSequence <= 0
            || value.ReceivedUtc.Kind != DateTimeKind.Utc || value.SourceUtc is { Kind: not DateTimeKind.Utc }
            || string.IsNullOrWhiteSpace(value.Ticker) || value.Ticker.Length > 32
            || value.OpenInterest is < 0 || value.Contract is { ConId: <= 0 }
            || value.Cumulative is { TotalVolume: < 0 } or { Vwap: < 0 } or { Multiplier: <= 0 })
        { CaptureFailed(); return MarketEventAcceptance.Faulted; }
        if (_queue.Writer.TryWrite(value)) { Interlocked.Increment(ref _accepted); return MarketEventAcceptance.Accepted; }
        if (Volatile.Read(ref _stopped) != 0) return MarketEventAcceptance.Disabled;
        MarkLoss(ref _congested, 1);
        return MarketEventAcceptance.Congested;
    }

    private void MarkLoss(ref long counter, long count)
    {
        if (count == 0) return;
        Interlocked.Add(ref counter, count);
        Interlocked.Exchange(ref _lastLossTicks, DateTime.UtcNow.Ticks);
        Interlocked.Increment(ref _lossRevision);
    }
    internal void CaptureFailed() => MarkLoss(ref _captureFailed, 1);
    private MarketRecordingLoss Loss()
    {
        var ticks = Interlocked.Read(ref _lastLossTicks);
        return new(Interlocked.Read(ref _lossRevision), Interlocked.Read(ref _congested), Interlocked.Read(ref _captureFailed),
            Interlocked.Read(ref _writeFailed), Interlocked.Read(ref _abandoned), ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc));
    }
    public MarketRecordingStatus Status => new(Volatile.Read(ref _faulted) != 0, Volatile.Read(ref _stopped) != 0,
        _queue.Reader.Count, Interlocked.Read(ref _accepted), Interlocked.Read(ref _written), Loss());

    private async Task RunAsync()
    {
        long recordedRevision = 0;
        try
        {
            while (true)
            {
                if (_queue.Reader.Count == 0 && Interlocked.Read(ref _lossRevision) == recordedRevision)
                {
                    if (Volatile.Read(ref _stopped) != 0) break;
                    await Task.Delay(_flushInterval, _stop.Token).ConfigureAwait(false);
                    continue;
                }
                var rows = new List<MarketEvent>(_batchSize);
                while (rows.Count < _batchSize && _queue.Reader.TryRead(out var value)) rows.Add(value);
                var loss = Loss();
                if (rows.Count != 0 || loss.Revision != recordedRevision)
                {
                    var batch = new MarketRecordingBatch(RecordingRunId, Array.AsReadOnly(rows.ToArray()), loss);
                    try
                    {
                        // Callback never executes writer code. Async writers must not synchronously
                        // block before returning their ValueTask, and must cooperate with cancellation.
                        await _writer.WriteAsync(batch, _stop.Token).AsTask().WaitAsync(_stop.Token).ConfigureAwait(false);
                        Interlocked.Add(ref _written, rows.Count); recordedRevision = loss.Revision;
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                    { MarkLoss(ref _abandoned, rows.Count); break; }
                    catch { MarkLoss(ref _writeFailed, rows.Count); Interlocked.Exchange(ref _faulted, 1); break; }
                }
                if (Volatile.Read(ref _stopped) != 0 && _queue.Reader.Count == 0
                    && Interlocked.Read(ref _lossRevision) == recordedRevision) break;
                if (_queue.Reader.Count == 0) await Task.Delay(_flushInterval, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch { Interlocked.Exchange(ref _faulted, 1); }
        finally
        {
            Interlocked.Exchange(ref _stopped, 1); _queue.Writer.TryComplete();
            var remainder = 0;
            while (_queue.Reader.TryRead(out _)) remainder++;
            MarkLoss(ref _abandoned, remainder);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync) return new ValueTask(_disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
        { _queue.Writer.TryComplete(); _stop.CancelAfter(_shutdownTimeout); }
        await _worker.ConfigureAwait(false);
        if (!_stop.IsCancellationRequested) _stop.Cancel();
        _stop.Dispose();
    }
}
