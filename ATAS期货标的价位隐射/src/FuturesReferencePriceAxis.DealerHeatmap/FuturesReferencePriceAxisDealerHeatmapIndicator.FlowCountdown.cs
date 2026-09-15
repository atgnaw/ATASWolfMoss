namespace WolfMoss.ATAS.PriceMapping;

using WolfMoss.ATAS.PriceMapping.Core;

public sealed partial class FuturesReferencePriceAxisDealerHeatmapIndicator
{
    private FlowCountdownContext? _flowCountdownContext;
    private readonly object _countdownLifecycleSync = new();
    private CancellationTokenSource? _countdownCancellation;
    private Task _countdownTask = Task.CompletedTask;

    // Called only by the scheduled data owner. Segment arrays are immutable after publication.
    private void UpdateFlowCountdownContext(string? ticker, DateOnly? expiration,
        IReadOnlyList<OptionContractDescriptor> contracts)
    {
        if (!_showOptionPremiumFlow || ticker == null || !expiration.HasValue || contracts.Count == 0)
        { Volatile.Write(ref _flowCountdownContext, null); return; }
        var segments = GetCachedFlowSegments(ticker, contracts);
        var current = Volatile.Read(ref _flowCountdownContext);
        if (current?.Ticker == ticker && current.Expiration == expiration && ReferenceEquals(current.Segments, segments)) return;
        Volatile.Write(ref _flowCountdownContext, new FlowCountdownContext(ticker, expiration.Value, segments));
    }

    private void RestartFlowCountdownClock()
    {
        if (!IsIndicatorInitialized) return;
        lock (_countdownLifecycleSync)
        {
            _countdownCancellation?.Cancel();
            _countdownCancellation?.Dispose();
            _countdownCancellation = null;
            if (!_showOptionPremiumFlow) return;
            var source = _countdownCancellation = new CancellationTokenSource();
            var token = source.Token;
            var previous = _countdownTask;
            _countdownTask = IbTaskOwnership.Own(Task.Run(async () =>
            {
                try
                {
                    try { await previous.ConfigureAwait(false); } catch (OperationCanceledException) { }
                    using var tracked = _performance.TrackTask();
                    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                    while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                    {
                        token.ThrowIfCancellationRequested();
                        RequestRedraw(); // UI-only wakeup even when diagnostics are Off or IB is reconnecting.
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            }));
        }
    }

    private void StopFlowCountdownClock()
    {
        lock (_countdownLifecycleSync)
        {
            _countdownCancellation?.Cancel();
            _countdownCancellation?.Dispose();
            _countdownCancellation = null;
        }
    }
}
