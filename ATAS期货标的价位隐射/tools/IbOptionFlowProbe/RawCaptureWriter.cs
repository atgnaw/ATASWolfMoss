using System.Text.Json;
using System.Threading.Channels;
using WolfMoss.ATAS.PriceMapping;

namespace IbOptionFlowProbe;

internal sealed class RawCaptureWriter : IAsyncDisposable
{
    private static readonly byte[] Newline = [(byte)'\n'];
    private readonly Channel<IbRawMarketEvent> _queue;
    private readonly Guid _run;
    private readonly string _directory;
    private readonly long _rotateBytes, _maxBytes;
    private readonly Func<string, Stream> _open;
    private readonly Task _worker;
    private long _accepted, _written, _dropped, _pending, _peak, _bytes;
    private int _closed;
    public readonly TaskCompletionSource<string> StopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string? Failure { get; private set; }
    public long Accepted => Interlocked.Read(ref _accepted);
    public long Written => Interlocked.Read(ref _written);
    public long Dropped => Interlocked.Read(ref _dropped);
    public long QueuePeak => Interlocked.Read(ref _peak);
    public long Bytes => Interlocked.Read(ref _bytes);

    public RawCaptureWriter(string directory, Guid run, int capacity = 65536,
        long rotateBytes = 128L * 1024 * 1024, long maxBytes = 2L * 1024 * 1024 * 1024,
        Func<string, Stream>? open = null)
    {
        if (capacity <= 0 || rotateBytes <= 0 || maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _directory = directory; _run = run; _rotateBytes = rotateBytes; _maxBytes = maxBytes;
        _open = open ?? (path => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan));
        _queue = Channel.CreateBounded<IbRawMarketEvent>(new BoundedChannelOptions(capacity)
            { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
        _worker = Task.Run(WriteAsync);
    }
    public void Publish(IbRawMarketEvent item)
    {
        if (Volatile.Read(ref _closed) != 0) return;
        var pending = Interlocked.Increment(ref _pending);
        if (!_queue.Writer.TryWrite(item))
        {
            Interlocked.Decrement(ref _pending);
            Interlocked.Increment(ref _dropped);
            return;
        }
        Interlocked.Increment(ref _accepted);
        long peak;
        do { peak = Interlocked.Read(ref _peak); }
        while (pending > peak && Interlocked.CompareExchange(ref _peak, pending, peak) != peak);
    }
    private async Task WriteAsync()
    {
        Stream? stream = null;
        var part = 0;
        long partBytes = 0;
        var sinceFlush = 0;
        try
        {
            await foreach (var raw in _queue.Reader.ReadAllAsync())
            {
                Interlocked.Decrement(ref _pending);
                var data = JsonSerializer.SerializeToUtf8Bytes(RawEventParser.Convert(_run, raw), ProbeJson.Options);
                var size = data.Length + 1L;
                if (Bytes + size > _maxBytes)
                {
                    Interlocked.Increment(ref _dropped);
                    StopRequested.TrySetResult("SIZE_LIMIT");
                    break;
                }
                if (stream == null || partBytes > 0 && partBytes + size > _rotateBytes)
                {
                    if (stream != null) await stream.DisposeAsync();
                    stream = _open(Path.Combine(_directory, $"events-{++part:00000}.jsonl"));
                    partBytes = 0;
                }
                await stream.WriteAsync(data);
                await stream.WriteAsync(Newline);
                partBytes += size;
                Interlocked.Add(ref _bytes, size);
                Interlocked.Increment(ref _written);
                if (++sinceFlush >= 256 || !_queue.Reader.TryPeek(out _))
                { await stream.FlushAsync(); sinceFlush = 0; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { Failure = "IO_FAILURE"; StopRequested.TrySetResult(Failure); }
        catch { Failure = "WRITER_FAILURE"; StopRequested.TrySetResult(Failure); }
        finally
        {
            Interlocked.Exchange(ref _closed, 1);
            _queue.Writer.TryComplete();
            while (_queue.Reader.TryRead(out _)) { Interlocked.Decrement(ref _pending); Interlocked.Increment(ref _dropped); }
            if (stream != null)
                try { await stream.DisposeAsync(); }
                catch { Failure = "IO_FAILURE"; StopRequested.TrySetResult(Failure); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _closed, 1);
        _queue.Writer.TryComplete();
        await _worker;
    }
}
