namespace WolfMoss.MarketData;

// Source-compiled per plugin; not an account-wide recorder. No worker exists by default.
internal sealed class MarketEventRecorder(BufferedMarketEventSink sink)
{
    private long _sequence;
    public Guid RunId => sink.RecordingRunId;
    public bool IsEnabled => sink.CanRecord;
    public long NextSequence() => Interlocked.Increment(ref _sequence);
    public MarketEventAcceptance Publish(MarketEvent value) => sink.TryPublish(value);
    public void CaptureFailed() => sink.CaptureFailed();
}
internal static class MarketEventHub
{
    private static MarketEventRecorder? _current;
    public static MarketEventRecorder? Current => Volatile.Read(ref _current) is { IsEnabled: true } recorder ? recorder : null;
    public static IAsyncDisposable Attach(BufferedMarketEventSink sink)
    {
        var recorder = new MarketEventRecorder(sink);
        if (Interlocked.CompareExchange(ref _current, recorder, null) != null)
            throw new InvalidOperationException("A recorder is already attached to this plugin");
        return new Attachment(recorder, sink);
    }
    private sealed class Attachment(MarketEventRecorder recorder, BufferedMarketEventSink sink) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Interlocked.CompareExchange(ref _current, null, recorder);
            await sink.DisposeAsync().ConfigureAwait(false);
        }
    }
}
