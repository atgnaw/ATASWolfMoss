using System.ComponentModel;
using ATAS.Indicators;
using WolfMoss.ATAS.DeepMEffort.Core;

namespace WolfMoss.ATAS.DeepMEffort;

[DisplayName("Deep-M Effort / 推进效率")]
[Category("WolfMoss")]
[Description("根据主动买卖量、价格实体和柱耗时识别连续推进区域。默认参数对应 NQ Range 40。区域在连续段被打断且下一根柱出现后确认。")]
public sealed partial class DeepMEffortIndicator : Indicator
{
    private readonly object _gate = new();
    private readonly ValueDataSeries _ema = new("DeepMEffort.Ema", "Effort EMA")
    {
        VisualType = VisualMode.Line, ShowZeroValue = false, Width = 1,
        Color = System.Windows.Media.Color.FromRgb(255, 82, 82), DrawAbovePrice = true,
        IsHidden = true, // Appearance is managed by the indicator's own settings.
    };
    private readonly List<(int Bar, InputIssue Issue)> _issues = [];
    private EffortConfig _config = new();
    private EffortEngine? _engine = new(new());
    private int _lastObserved = -1;
    private string? _configurationError;
    private string? _instrument;
    private decimal _tickSize;

    public DeepMEffortIndicator() : base(true)
    {
        Panel = IndicatorDataProvider.CandlesPanel;
        DenyToChangePanel = true;
        DataSeries[0] = _ema;
        EnableCustomDrawing = true;
        DrawAbovePrice = false;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar < 0 || bar >= CurrentBar) return;
        lock (_gate)
        {
            var tickSize = InstrumentInfo?.TickSize ?? 0;
            var instrument = InstrumentInfo?.Instrument;
            if (bar < _lastObserved || (_lastObserved >= 0 && (_tickSize != tickSize || _instrument != instrument)))
                ResetState();
            _tickSize = tickSize;
            _instrument = instrument;
            if (_engine is null) return;

            // Consume only bars strictly before this callback's index. Historical loading and live
            // ticks therefore follow the same causal path, even when CurrentBar already holds all history.
            while (_engine.NextBarIndex < bar)
            {
                var closedIndex = _engine.NextBarIndex;
                var candle = GetCandle(closedIndex);
                var next = GetCandle(closedIndex + 1);
                var input = new CandleInput(candle.Open, candle.High, candle.Low, candle.Close,
                    candle.Ask, candle.Bid, candle.Volume, candle.Betweens, candle.Time, candle.LastTime);
                var closed = input.ToBar(closedIndex, tickSize, next.Time, out var issues);
                _engine.CloseBar(closed);
                if (issues != InputIssue.None) _issues.Add((closedIndex, issues));
                WriteEma(closedIndex);
                // Same ordering as the C++ adapter: settle old bar, then freeze at the new session.
                // Continuous segments and EMA are intentionally not reset at a session boundary.
                if (IsNewSession(closedIndex + 1)) _engine.Freeze(closedIndex + 1, StopReason.Session);
            }
            WriteEma(bar); // Open bar holds the last finalized EMA; repeated ticks do not advance it.
            _lastObserved = bar;
        }
    }

    private void WriteEma(int bar)
    {
        _ema[bar] = _engine?.Ema is { } value ? (decimal)value : 0;
    }

    private void ResetState()
    {
        _issues.Clear();
        _ema.Clear();
        _lastObserved = -1;
        _instrument = null;
        _tickSize = 0;
        _configurationError = null;
        try { _engine = new EffortEngine(_config); }
        catch (ArgumentOutOfRangeException)
        {
            _engine = null;
            _configurationError = "Deep-M Effort：最小 Delta 占比不能超过最大值（最大值为 0 时关闭上限）。";
        }
    }

    protected override void OnRecalculate()
    {
        lock (_gate) ResetState();
        base.OnRecalculate();
    }

    protected override void OnDataProviderChanged(IIndicatorDataProvider? oldDataProvider, IIndicatorDataProvider? newDataProvider)
    {
        lock (_gate) ResetState();
        base.OnDataProviderChanged(oldDataProvider, newDataProvider);
    }

    protected override void OnFinishRecalculate()
    {
        base.OnFinishRecalculate();
        RedrawChart();
    }

    internal EffortZone[] ZoneSnapshot { get { lock (_gate) return _engine?.Zones.ToArray() ?? []; } }
    internal int ClosedBarCount { get { lock (_gate) return _engine?.NextBarIndex ?? 0; } }
}
