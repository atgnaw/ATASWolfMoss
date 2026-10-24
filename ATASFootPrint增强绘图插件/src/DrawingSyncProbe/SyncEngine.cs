namespace WolfMoss.ATAS.DrawingSync;

// Numeric values are persisted; append new kinds without renumbering existing ones.
public enum DrawingKind { HorizontalLine, Rectangle, HorizontalRay, TrendLine }
public abstract record DrawingState
{
    public abstract DrawingKind Kind { get; }
}
public sealed record LineState(decimal Price, uint Color, int Width, int Style,
    string Text, int TextSize, int TextPosition, bool HidePrice) : DrawingState
{
    public override DrawingKind Kind => DrawingKind.HorizontalLine;
}
public sealed record MiddleLineState(bool Enabled, uint Color, int Width, int Style);
public sealed record RayState(DateTime StartTime, LineState Line) : DrawingState
{
    public override DrawingKind Kind => DrawingKind.HorizontalRay;
}
public sealed record FontState(int Size, string Family, bool Bold, bool Italic, bool Underline, bool Strikeout);
public sealed record TrendState(DateTime FirstTime, DateTime SecondTime, decimal FirstPrice, decimal SecondPrice,
    uint Color, int Width, int Style, string Text, uint TextBackground, FontState Font,
    bool StartArrow, bool EndArrow, int RenderType) : DrawingState
{
    public override DrawingKind Kind => DrawingKind.TrendLine;
}
public sealed record RectangleState(DateTime FirstTime, DateTime SecondTime, decimal FirstPrice,
    decimal SecondPrice, uint Color, int Width, int Style, uint AreaColor, int AreaOpacity,
    string Text, int VerticalPosition, int HorizontalPosition, bool ExtendLeft, bool ExtendRight,
    MiddleLineState MiddleLine) : DrawingState
{
    public override DrawingKind Kind => DrawingKind.Rectangle;
}

// AllIds includes unsupported/temporarily selected objects. An ineligible object is NOT a deletion.
public sealed record ChartSnapshot(Dictionary<string, DrawingState> Objects, HashSet<string> AllIds,
    HashSet<string>? LocalObjectIds = null);
public sealed record Binding(string Left, string Right, bool GlobalPair = false, DrawingKind Kind = DrawingKind.HorizontalLine);
public enum EditKind { Create, Update, Delete }
public sealed record Edit(bool ToLeft, EditKind Kind, string Id, DrawingState? State);

/// <summary>Two-sided reconciliation. Left is the E-mini; right is the Micro.</summary>
public sealed class SyncEngine
{
    private sealed class Pair(Binding binding)
    {
        public Binding Binding = binding;
        public DrawingState? Last;
        public bool Dormant;
        public bool Suspended;
    }

    private readonly List<Pair> _pairs;
    private readonly Action<IReadOnlyList<Binding>> _persist;
    public int WaitingPairs { get; private set; }
    public int ConflictCount { get; private set; }
    // Active pairs only. Retain a bounded ID history for native Undo, never count it as live.
    public int PairCount { get; private set; }
    public const int MaxLivePairs = 2000;
    public const int UndoHistoryLimit = 256;

    public SyncEngine(IEnumerable<Binding> bindings, Action<IReadOnlyList<Binding>> persist)
    {
        var saved = bindings.ToArray();
        if (saved.Any(x => !Enum.IsDefined(x.Kind) || string.IsNullOrWhiteSpace(x.Left) || string.IsNullOrWhiteSpace(x.Right)) ||
            saved.Select(x => x.Left).Distinct().Count() != saved.Length ||
            saved.Select(x => x.Right).Distinct().Count() != saved.Length)
            throw new InvalidDataException("配对文件存在未知绘图类型、重复或空 ID，已停止同步。");
        _pairs = saved.Select(x => new Pair(x)).ToList();
        _persist = persist;
    }

    // The caller must discard this engine and stop the group on ANY persistence or application failure.
    public void Step(ChartSnapshot left, ChartSnapshot right, Action<Edit> apply, Action? verify = null)
    {
        var original = _pairs.Select(x => x.Binding).ToArray();
        var edits = new List<Edit>();
        Plan(left, right, edits.Add);
        var final = _pairs.Select(x => x.Binding).ToArray();
        if (original.SequenceEqual(final))
        {
            foreach (var edit in edits) apply(edit);
            if (edits.Count > 0) verify?.Invoke();
            return;
        }
        var remaining = final.ToDictionary(x => x.Left);
        var oldIds = original.Select(x => x.Left).ToHashSet();
        // Persist creations/promotions before native writes, but retain removed mappings until
        // all native deletes are verified. At most two durable writes for a whole batch.
        var writeAhead = original.Select(x => remaining.GetValueOrDefault(x.Left, x))
            .Concat(final.Where(x => !oldIds.Contains(x.Left))).ToArray();
        if (!original.SequenceEqual(writeAhead)) _persist(writeAhead);
        foreach (var edit in edits) apply(edit);
        if (edits.Count > 0) verify?.Invoke();
        if (!writeAhead.SequenceEqual(final)) _persist(final);
    }

    private void Plan(ChartSnapshot left, ChartSnapshot right, Action<Edit> apply)
    {
        WaitingPairs = 0;
        PairCount = 0;
        var leftKnown = _pairs.Select(x => x.Binding.Left).ToHashSet();
        var rightKnown = _pairs.Select(x => x.Binding.Right).ToHashSet();
        var unlinked = new HashSet<Pair>();
        foreach (var pair in _pairs)
        {
            var b = pair.Binding;
            left.Objects.TryGetValue(b.Left, out var l);
            right.Objects.TryGetValue(b.Right, out var r);
            if ((l is not null && l.Kind != b.Kind) || (r is not null && r.Kind != b.Kind))
            {
                pair.Suspended = true;
                WaitingPairs++;
                continue;
            }
            var localLeft = left.LocalObjectIds?.Contains(b.Left) == true;
            var localRight = right.LocalObjectIds?.Contains(b.Right) == true;
            if (b.GlobalPair && (localLeft || localRight))
            {
                // Only a confirmed completed supported main-chart object becoming local means unlink.
                // Keep the journal until deletion succeeds so a retry cannot orphan a mirror.
                if ((!localLeft && l is null && left.AllIds.Contains(b.Left)) ||
                    (!localRight && r is null && right.AllIds.Contains(b.Right)))
                {
                    WaitingPairs++;
                    continue;
                }
                if (l is not null) apply(new(true, EditKind.Delete, b.Left, null));
                if (r is not null) apply(new(false, EditKind.Delete, b.Right, null));
                unlinked.Add(pair);
                continue;
            }
            if ((l is null && left.AllIds.Contains(b.Left)) || (r is null && right.AllIds.Contains(b.Right)))
            {
                // Legacy local pairs and unsupported/unfinished objects are not deletion commands.
                pair.Suspended = true;
                pair.Dormant = false;
                if (l is not null || r is not null) WaitingPairs++;
                continue;
            }
            if (l is null && r is null)
            {
                pair.Dormant = true;
                pair.Last = null;
                continue;
            }
            pair.Dormant = false;
            if (l is null || r is null)
            {
                if (pair.Suspended) { WaitingPairs++; continue; }
                if (pair.Last is not null)
                {
                    apply(new(l is not null, EditKind.Delete, l is not null ? b.Left : b.Right, null));
                    pair.Last = null;
                    pair.Dormant = true;
                }
                else
                {
                    // Reconnect or Undo: a surviving global line repairs its missing peer.
                    // An offline absence is never evidence of user deletion.
                    var restored = l ?? r!;
                    if (!b.GlobalPair)
                    {
                        pair.Binding = b with { GlobalPair = true };
                    }
                    apply(new(l is null, EditKind.Create, l is null ? b.Left : b.Right, restored));
                    pair.Last = restored;
                    PairCount++;
                }
                continue;
            }
            if (!b.GlobalPair)
            {
                pair.Binding = b with { GlobalPair = true };
            }
            var winner = l;
            if (l != r)
            {
                if (pair.Last is not null && l == pair.Last) winner = r;
                else if (pair.Last is not null && r != pair.Last) ConflictCount++;
                apply(new(winner == r, EditKind.Update, winner == r ? b.Left : b.Right, winner));
            }
            pair.Last = winner;
            pair.Suspended = false;
            PairCount++;
        }
        if (unlinked.Count > 0) _pairs.RemoveAll(unlinked.Contains);
        var expired = _pairs.Where(x => x.Dormant).Reverse().Skip(UndoHistoryLimit).ToHashSet();
        if (expired.Count > 0)
        {
            _pairs.RemoveAll(expired.Contains);
        }
        var additions = left.Objects.Count(x => !leftKnown.Contains(x.Key)) + right.Objects.Count(x => !rightKnown.Contains(x.Key));
        if (_pairs.Count(x => !x.Dormant) + additions > MaxLivePairs)
            throw new InvalidOperationException("有效配对达到 2000 上限，请先清理不用的全局绘图。");
        foreach (var entry in left.Objects.Where(x => !leftKnown.Contains(x.Key)))
            Add(entry.Key, entry.Value, fromLeft: true, apply);
        foreach (var entry in right.Objects.Where(x => !rightKnown.Contains(x.Key)))
            Add(entry.Key, entry.Value, fromLeft: false, apply);
    }

    private void Add(string source, DrawingState state, bool fromLeft, Action<Edit> apply)
    {
        var target = Guid.NewGuid().ToString();
        var binding = fromLeft ? new Binding(source, target, true, state.Kind) : new Binding(target, source, true, state.Kind);
        var pair = new Pair(binding);
        _pairs.Add(pair);
        apply(new(!fromLeft, EditKind.Create, target, state));
        pair.Last = state;
        PairCount++;
    }
}

public sealed record InstrumentKey(string Root, string Month, string Identity)
{
    public bool ContinuousHistory { get; init; }
    public bool IsUndatedContinuous => Month == "CONTINUOUS";
    public string PeriodLabel => IsUndatedContinuous ? "主连（平台未提供到期月）" : Month;
    public string GroupSuffix => ContinuousHistory ? "|continuous" : "";
    public bool IsMicro => Root is "MNQ" or "MES";
    public string Family => Root is "MNQ" or "NQ" ? "NQ" : "ES";
    public string PairingKey => Family + "|" + Month + GroupSuffix;
    public static InstrumentKey Create(string root, DateTime expiration, string uniqueClass,
        string uniqueName, bool continuous, bool replay, string? contractCode = null, bool continuousInstrument = false)
    {
        root = root.Trim().ToUpperInvariant();
        if (root is not ("NQ" or "MNQ" or "ES" or "MES"))
            throw new InvalidOperationException($"不支持的合约代码：{root}（仅 NQ/MNQ/ES/MES）。");
        if (replay) throw new InvalidOperationException("检测到 Market Replay 回放连接；暂不支持回放图表。");
        if (string.IsNullOrWhiteSpace(uniqueName) || string.IsNullOrWhiteSpace(uniqueClass))
            throw new InvalidOperationException("平台未提供品种唯一身份。");
        var normalizedCode = (contractCode ?? "").Trim().ToUpperInvariant();
        // Actual ATAS continuous instruments expose Name=ES/MES, Expiration=MinValue,
        // Continuous=true even when the chart title includes #ESZ6/#MESZ6.
        // Give this documented runtime shape its own namespace; never invent an expiry month.
        if (continuous && continuousInstrument && expiration == DateTime.MinValue &&
            (normalizedCode == root || normalizedCode == "#" + root))
            return new(root, "CONTINUOUS", uniqueClass + "|" + uniqueName) { ContinuousHistory = true };
        if (expiration.Year < 2000 || expiration.Year > 2100)
            throw new InvalidOperationException("无法确认合约月份；且不符合平台主连身份格式。");
        if (continuous)
        {
            // A chart with spliced history can still have an explicit active contract (e.g. #ESZ6).
            // Verify both month and year against native expiration instead of guessing from today's year.
            var match = System.Text.RegularExpressions.Regex.Match(normalizedCode,
                "^#?" + root + "([FGHJKMNQUVXZ])([0-9]{1,2}|[0-9]{4})$");
            if (!match.Success)
                throw new InvalidOperationException("连续图未提供可核实的具体月份合约代码，例如 ESZ6；暂不支持无月份主连。");
            var month = "FGHJKMNQUVXZ".IndexOf(match.Groups[1].Value[0]) + 1;
            var yearText = match.Groups[2].Value;
            var year = int.Parse(yearText, System.Globalization.CultureInfo.InvariantCulture);
            var expected = yearText.Length switch { 1 => expiration.Year % 10, 2 => expiration.Year % 100, _ => expiration.Year };
            if (month != expiration.Month || year != expected)
                throw new InvalidOperationException("连续图合约代码与平台到期月份不一致，暂停同步。");
        }
        return new(root, expiration.ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture), uniqueClass + "|" + uniqueName)
        { ContinuousHistory = continuous };
    }
}

