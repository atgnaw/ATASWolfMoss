// Source-shared module: compiled privately into each consuming plugin.
namespace WolfMoss.ATAS.PriceMapping.Core;

using System.Collections.Concurrent;

internal sealed class SharedAsyncRequests<TKey, TValue> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Lazy<Task<TValue>>> _requests = new();

    public async Task<TValue> GetAsync(TKey key, Func<Task<TValue>> factory, CancellationToken waiter)
    {
        waiter.ThrowIfCancellationRequested();
        var entry = _requests.GetOrAdd(key, _ => new Lazy<Task<TValue>>(factory));
        Task<TValue> task;
        try { task = entry.Value; }
        catch
        {
            _requests.TryRemove(new KeyValuePair<TKey, Lazy<Task<TValue>>>(key, entry));
            throw;
        }
        try { return await task.WaitAsync(waiter).ConfigureAwait(false); }
        finally
        {
            if (task.IsFaulted || task.IsCanceled)
                _requests.TryRemove(new KeyValuePair<TKey, Lazy<Task<TValue>>>(key, entry));
            else if (!task.IsCompleted)
                _ = RemoveFailedAsync(key, entry, task);
        }
    }

    private async Task RemoveFailedAsync(TKey key, Lazy<Task<TValue>> entry, Task<TValue> task)
    {
        try { await task.ConfigureAwait(false); }
        catch { _requests.TryRemove(new KeyValuePair<TKey, Lazy<Task<TValue>>>(key, entry)); }
    }
}
