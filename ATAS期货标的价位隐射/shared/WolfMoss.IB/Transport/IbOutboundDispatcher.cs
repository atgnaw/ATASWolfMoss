// Source-shared module: compiled privately into each consuming plugin.
namespace WolfMoss.ATAS.PriceMapping.Core;

using System.Diagnostics;
using System.Threading.Channels;

// One writer owns all paced socket requests. Admission is bounded and never sleeps
// under a caller's lock. Cancellation of a waiter cannot cancel another request.
internal sealed class IbOutboundDispatcher : IAsyncDisposable
{
    private sealed record Work(Action Send, Func<bool>? IsCurrent, CancellationToken Token,
        TaskCompletionSource Completion);
    private readonly Channel<Work> _queue;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly Action _onSend;
    private readonly int _perSecond;
    private int _disposed;
    private int _resourcesDisposed;

    public IbOutboundDispatcher(Action onSend, int capacity = 2048, int perSecond = 45)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(perSecond);
        ArgumentNullException.ThrowIfNull(onSend);
        _onSend = onSend;
        _perSecond = perSecond;
        _queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(capacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _worker = Task.Run(RunAsync);
    }

    public Task Enqueue(Action send, CancellationToken token = default, Func<bool>? isCurrent = null)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(new Work(send, isCurrent, token, completion)))
            completion.TrySetException(new InvalidOperationException("IB outbound queue unavailable"));
        return completion.Task;
    }

    public bool IsRunning => !_worker.IsCompleted;

    private async Task RunAsync()
    {
        var sent = new Queue<long>();
        try
        {
            await foreach (var work in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                try
                {
                    work.Token.ThrowIfCancellationRequested();
                    if (work.IsCurrent?.Invoke() == false) { work.Completion.TrySetResult(); continue; }
                    while (sent.Count >= _perSecond)
                    {
                        var remaining = TimeSpan.FromSeconds(1) - Stopwatch.GetElapsedTime(sent.Peek());
                        if (remaining > TimeSpan.Zero)
                            await Task.Delay(remaining, _stop.Token).ConfigureAwait(false);
                        else sent.Dequeue();
                    }
                    _stop.Token.ThrowIfCancellationRequested();
                    work.Token.ThrowIfCancellationRequested();
                    if (work.IsCurrent?.Invoke() != false)
                    {
                        sent.Enqueue(Stopwatch.GetTimestamp());
                        _onSend();
                        work.Send();
                    }
                    work.Completion.TrySetResult();
                }
                catch (OperationCanceledException) { work.Completion.TrySetCanceled(); }
                catch (Exception ex) { work.Completion.TrySetException(ex); }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            while (_queue.Reader.TryRead(out var work)) work.Completion.TrySetCanceled();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        { _queue.Writer.TryComplete(); _stop.Cancel(); }
        await _worker.ConfigureAwait(false);
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) == 0) _stop.Dispose();
    }
}
