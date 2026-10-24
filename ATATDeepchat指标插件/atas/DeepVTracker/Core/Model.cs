using System.ComponentModel.DataAnnotations;

namespace WolfMoss.ATAS.DeepVTracker;

public enum SignalStrength { [Display(Name = "弱")] Weak, [Display(Name = "中")] Medium, [Display(Name = "强")] Strong }
public enum LevelPosition { [Display(Name = "保守")] Conservative, [Display(Name = "激进")] Aggressive, [Display(Name = "中间")] Medium }

internal sealed record TrackerConfig
{
    public SignalStrength Acceleration { get; init; } = SignalStrength.Medium;
    public SignalStrength Exhaustion { get; init; } = SignalStrength.Medium;
    public SignalStrength Slowdown { get; init; } = SignalStrength.Medium;
    public SignalStrength Absorption { get; init; } = SignalStrength.Medium;
    public LevelPosition LevelMode { get; init; } = LevelPosition.Conservative;
    public int ProjectionBars { get; init; } = 10;
    public bool EnableAcceleration { get; init; } = true;
    public bool EnableExhaustion { get; init; } = true;
    public bool EnableSlowdown { get; init; } = true;
    public bool EnableLevels { get; init; } = true;
}

internal enum TradeSide { Unknown, Buy, Sell }
internal enum SignalKind { Acceleration = 1, ExhaustionVolume, ExhaustionCount, Slowdown, Control, Extreme }
internal readonly record struct PriceLevel(long Price, long Ask, long Bid, long Total);
// Time is DateTime ticks (100 ns) throughout this port; never use wall-clock timestamps.
internal readonly record struct TradeTick(long Time, long Price, long Volume, TradeSide Side);
internal sealed record FlowBar(int Index, long Time, long EndTime, long Open, long High, long Low, long Close,
    long Ask, long Bid, long Volume, long AskCount, long BidCount, PriceLevel[] Profile,
    bool Known = true, bool NewSession = false);
internal sealed record Signal
{
    public long Id { get; init; }
    public SignalKind Kind { get; init; }
    public int Start { get; init; }
    public int End { get; init; }
    public int Created { get; init; }
    public long Lower { get; init; }
    public long Upper { get; init; }
    public long Stop { get; init; }
    public int Lifetime { get; init; }
    public double StartOffset { get; init; } = 1;
    public double EndOffset { get; init; } = 1;
    public bool Up { get; init; }
    public bool Ask { get; init; }
    public bool Absorption { get; init; }
    public bool StopsAbove { get; init; }
    public bool Active { get; init; }
    public bool Visible { get; init; } = true;
    public static long Key(SignalKind kind, long sequence) => checked(((long)kind << 56) + sequence);
}
internal readonly record struct Visibility(bool Active, int End, int Lookback, long Low, long High);

internal sealed class Samples(int capacity)
{
    private Queue<double> _values = new();
    private double _sum, _squared;
    public long Added { get; private set; }
    public double Mean => _values.Count == 0 ? 0 : _sum / _values.Count;
    public double Deviation => _values.Count < 2 ? 0 : Math.Sqrt(Math.Abs(_squared) / (_values.Count - 1));
    public double Threshold(double k) => Mean == 0 ? 0 : Mean + k * Deviation;
    public void Add(double value)
    {
        if (_values.Count == capacity)
        {
            var oldMean = Mean;
            var removed = _values.Dequeue();
            _sum -= removed;
            _squared = _values.Count == 0 ? 0 : _squared - (removed - oldMean) * (removed - Mean);
        }
        var sample = Math.Abs(value);
        var before = Mean;
        _values.Enqueue(sample); _sum += sample;
        _squared += (sample - before) * (sample - Mean);
        Added++;
    }
    public Samples Clone() { var copy = (Samples)MemberwiseClone(); copy._values = new(_values); return copy; }
}
