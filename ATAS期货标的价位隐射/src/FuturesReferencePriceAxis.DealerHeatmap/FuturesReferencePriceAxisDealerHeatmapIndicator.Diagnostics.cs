namespace WolfMoss.ATAS.PriceMapping;

using System.ComponentModel.DataAnnotations;
using System.Globalization;
using global::ATAS.Indicators;
using OFT.Rendering.Context;
using WolfMoss.ATAS.PriceMapping.Core;

public sealed partial class FuturesReferencePriceAxisDealerHeatmapIndicator
{
    private readonly PerformanceCollector _performance = new();
    private PerformanceDiagnosticsMode _performanceDiagnosticsMode;
    private CancellationTokenSource? _performanceCancellation;
    private Task _performanceTask = Task.CompletedTask;
    private readonly object _performanceLifecycleSync = new();
    private string[] _performanceLines = Array.Empty<string>();
    private string[]? _cachedPerformanceLines;

    [Display(Name = "Performance diagnostics / 性能诊断",
        Description = "Off / Summary / Record; Record writes local aggregate statistics only.",
        GroupName = "Diagnostics / 诊断", Order = 500)]
    public PerformanceDiagnosticsMode PerformanceDiagnosticsMode
    {
        get => _performanceDiagnosticsMode;
        set
        {
            var normalized = Enum.IsDefined(value) ? value : Core.PerformanceDiagnosticsMode.Off;
            if (normalized == _performanceDiagnosticsMode) return;
            _performanceDiagnosticsMode = normalized;
            RestartPerformanceDiagnostics();
            RequestRedraw();
        }
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        using var measurement = _performance.Measure(PerformanceMetric.Render);
        base.OnRender(context, layout);
    }

    private void RestartPerformanceDiagnostics()
    {
        lock (_performanceLifecycleSync)
        {
            _performanceCancellation?.Cancel();
            _performanceCancellation?.Dispose();
            _performanceCancellation = null;
            _performance.Enabled = false;
            Volatile.Write(ref _performanceLines, Array.Empty<string>());
            if (!IsIndicatorInitialized || _performanceDiagnosticsMode == Core.PerformanceDiagnosticsMode.Off) return;
            var cancellation = new CancellationTokenSource();
            _performanceCancellation = cancellation;
            var token = cancellation.Token;
            var previous = _performanceTask;
            var mode = _performanceDiagnosticsMode;
            _performanceTask = Task.Run(async () =>
            {
                try
                {
                    await previous.ConfigureAwait(false);
                    lock (_performanceLifecycleSync)
                    {
                        token.ThrowIfCancellationRequested();
                        _performance.Enabled = true;
                    }
                    await RunPerformanceDiagnosticsAsync(mode, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch
                {
                    lock (_performanceLifecycleSync)
                        if (!token.IsCancellationRequested)
                            Volatile.Write(ref _performanceLines, new[] { "PERF: DIAGNOSTIC_ERROR / 诊断停止，业务不受影响" });
                }
                finally { _performance.Enabled = false; }
            });
        }
    }

    private void StopPerformanceDiagnostics()
    {
        lock (_performanceLifecycleSync)
        {
            _performance.Enabled = false;
            _performanceCancellation?.Cancel();
            _performanceCancellation?.Dispose();
            _performanceCancellation = null;
            Volatile.Write(ref _performanceLines, Array.Empty<string>());
        }
    }

    private async Task RunPerformanceDiagnosticsAsync(PerformanceDiagnosticsMode mode, CancellationToken cancellationToken)
    {
        using var tracked = _performance.TrackTask();
        PerformanceDiagnosticRecorder? recorder = null;
        PerformanceRunMetadata? recordedConfiguration = null;
        PerformanceRunMetadata Configuration() => new(
            typeof(FuturesReferencePriceAxisDealerHeatmapIndicator).Assembly.ManifestModule.ModuleVersionId.ToString("N"),
            _optionFlowBucketMode.ToString(), _optionFlowIntervalMinutes, _optionStrikeLevels,
            _ibOptionMarketDataLineBudget, _showOptionOpenInterest, _showOptionPremiumFlow);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var configuration = Configuration();
                if (mode == Core.PerformanceDiagnosticsMode.Record && configuration != recordedConfiguration)
                {
                    if (recorder != null) await recorder.DisposeAsync().ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    recordedConfiguration = configuration;
                    recorder = new PerformanceDiagnosticRecorder(PerformanceDiagnosticRecorder.DefaultDirectory, configuration);
                }
                IbPerformanceSnapshot? gateway = null;
                try { gateway = Volatile.Read(ref _optionGatewayLease)?.Client.PerformanceSnapshot; }
                catch (ObjectDisposedException) { }
                var snapshot = _performance.Sample(DateTime.UtcNow, gateway, _ibOptionMarketDataLineBudget,
                    recorder?.Dropped ?? 0, recorder?.State ?? "SUMMARY");
                if (recorder?.State is "STARTING" or "RECORDING")
                    snapshot = snapshot with { BackgroundTasks = snapshot.BackgroundTasks + 1 };
                recorder?.TryRecord(snapshot);
                var lines = new[]
                {
                    FormattableString.Invariant($"PERF 绘制 ms: avg {snapshot.Render.MeanMs:F3} / P95≤{snapshot.Render.P95UpperMs:F3}"),
                    FormattableString.Invariant($"PERF 快照 ms: avg {snapshot.Publish.MeanMs:F3} / P95≤{snapshot.Publish.P95UpperMs:F3}"),
                    FormattableString.Invariant($"PERF 回调 ms: avg {snapshot.Callback.MeanMs:F3} / 事件 {snapshot.EventsPerSecond:F1}/s"),
                    FormattableString.Invariant($"PERF IB共享: 发送尝试 {snapshot.SendsPerSecond:F1}/s；取消 {snapshot.CancelsPerSecond:F1}/s"),
                    $"PERF 行情线 {snapshot.Lines}/{snapshot.Budget}；消费者 {snapshot.Consumers}；连接替换 {snapshot.GatewayReplacements}",
                    $"PERF 样本 {snapshot.CachedSamples}；OI {snapshot.OiValues}；受监测循环 {snapshot.BackgroundTasks}",
                    "PERF 队列/行情缺口: N/A（当前同步回调，尚不可测）",
                    $"PERF 记录: {snapshot.RecorderState}；诊断丢样 {snapshot.DiagnosticSamplesDropped}；错误 {snapshot.LastErrorCode}"
                };
                lock (_performanceLifecycleSync)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    Volatile.Write(ref _performanceLines, lines);
                }
                RequestRedraw();
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (recorder != null) await recorder.DisposeAsync().ConfigureAwait(false);
        }
    }
}
