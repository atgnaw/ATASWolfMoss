namespace WolfMoss.ATAS.PriceMapping.Core;

// A canceled waiter does not own a shared operation's eventual failure. Attach an
// observer at creation; awaiting the original task still propagates its exception.
// Never use a process-wide UnobservedTaskException handler in a host plugin.
internal static class IbTaskOwnership
{
    public static T Own<T>(T task) where T : Task
    {
        _ = task.ContinueWith(static failed => { _ = failed.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }

    public static TaskCompletionSource Completion()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Own(source.Task);
        return source;
    }

    public static TaskCompletionSource<T> Completion<T>()
    {
        var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Own(source.Task);
        return source;
    }
}
