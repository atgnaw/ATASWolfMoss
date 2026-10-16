namespace WolfMoss.ATAS.DrawingSync;

internal interface ISyncBridge
{
    InstrumentKey Instrument { get; }
    int ChartId { get; }
    int ThreadId { get; }
    object Core { get; }
    void CheckReady();
    ChartSnapshot? Read();
    void Apply(Edit edit);
}

internal sealed class SyncHub(string? journalDirectory = null)
{
    internal static SyncHub Shared { get; } = new();
    internal sealed record Endpoint(Guid Id, ISyncBridge Bridge, string Group, Func<bool> Active,
        Action<string> Status, Action Redraw);
    private sealed record Entry(Endpoint Endpoint, long Heartbeat);
    private sealed class Session : IDisposable
    {
        public required HashSet<Guid> Members;
        public required PairJournal Journal;
        public required SyncEngine Engine;
        public string? Fault;
        public void Dispose() => Journal.Dispose();
    }
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly Dictionary<string, Session> _sessions = [];
    private static string Key(Endpoint e) => e.Group + "|" + e.Bridge.Instrument.PairingKey;

    private static bool HasBothSides(IEnumerable<Endpoint> members) =>
        members.Any(x => x.Bridge.Instrument.IsMicro) && members.Any(x => !x.Bridge.Instrument.IsMicro);

    private static string Describe(IEnumerable<Endpoint> members) => string.Join("；", members.GroupBy(Key).Select(group =>
    {
        var first = group.First();
        var instrument = first.Bridge.Instrument;
        var charts = group.DistinctBy(x => x.Bridge.Core, ReferenceEqualityComparer.Instance);
        return $"组「{first.Group}」 {instrument.PeriodLabel}/{(instrument.ContinuousHistory ? "连续历史" : "单月历史")} [" +
            string.Join(", ", charts.Select(x => $"{x.Bridge.Instrument.Root} 图#{x.Bridge.ChartId}")) + "]";
    }));

    internal void Remove(Guid id)
    {
        lock (_gate) RemoveLocked(id);
    }

    private void DropSession(string key)
    {
        if (_sessions.Remove(key, out var session)) session.Dispose();
    }

    private void RemoveLocked(Guid id)
    {
        if (_entries.Remove(id, out var entry)) DropSession(Key(entry.Endpoint));
    }

    // Same native Global object appears in every chart. Merge IDs, never make a copy per chart.
    private static ChartSnapshot? ReadSide(Endpoint[] endpoints)
    {
        var lines = new Dictionary<string, LineState>();
        var ids = new HashSet<string>();
        var localIds = new HashSet<string>();
        foreach (var endpoint in endpoints)
        {
            var snapshot = endpoint.Bridge.Read();
            if (snapshot is null) return null;
            ids.UnionWith(snapshot.AllIds);
            if (snapshot.LocalLineIds is { } locals) localIds.UnionWith(locals);
            foreach (var (id, state) in snapshot.Lines)
            {
                if (lines.TryGetValue(id, out var previous) && previous != state)
                    throw new InvalidOperationException("同一全局对象在不同图表中的状态不一致。");
                lines[id] = state;
            }
        }
        if (localIds.Overlaps(lines.Keys)) throw new InvalidOperationException("全局属性正在变化，请重新启用同步。");
        return new(lines, ids, localIds);
    }

    internal void Tick(Endpoint endpoint)
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;
            foreach (var id in _entries.Where(x => !x.Value.Endpoint.Active() || now - x.Value.Heartbeat > 3000).Select(x => x.Key).ToArray())
                RemoveLocked(id);
            if (_entries.TryGetValue(endpoint.Id, out var previous) && Key(previous.Endpoint) != Key(endpoint)) RemoveLocked(endpoint.Id);
            _entries[endpoint.Id] = new(endpoint, now);
            var key = Key(endpoint);
            var members = _entries.Values.Select(x => x.Endpoint).Where(x => Key(x) == key).ToArray();
            void Pause(string message)
            {
                DropSession(key); // Never infer deletion across a membership/readiness gap.
                foreach (var member in members) member.Status(message);
            }

            var otherMembers = _entries.Values.Select(x => x.Endpoint).Where(x => Key(x) != key &&
                x.Bridge.Instrument.Family == endpoint.Bridge.Instrument.Family).ToArray();

            // Ignore a duplicate indicator on the same chart for counting and native reads.
            var charts = members.DistinctBy(x => x.Bridge.Core, ReferenceEqualityComparer.Instance).ToArray();
            var left = charts.Where(x => !x.Bridge.Instrument.IsMicro).ToArray();
            var right = charts.Where(x => x.Bridge.Instrument.IsMicro).ToArray();
            if (left.Length == 0 || right.Length == 0)
            {
                var message = $"等待：组「{endpoint.Group}」缺少同合约类型的{(left.Length == 0 ? "大合约" : "微型合约")}图 | 本图 {endpoint.Bridge.Instrument.Root} 图#{endpoint.Bridge.ChartId}";
                if (otherMembers.Length > 0) message += " | 检测到其他配置：" + Describe(otherMembers) + "；如需加入，请统一组名及合约类型";
                Pause(message);
                return;
            }
            // A one-sided group cannot write mirrors, so it must not stop a complete group.
            // Two complete engines would still copy each other's Global mirrors indefinitely.
            var overlaps = otherMembers.GroupBy(Key).Where(HasBothSides).SelectMany(x => x).ToArray();
            if (overlaps.Length > 0)
            {
                var message = "暂停：全局同步冲突（多个完整配对组） | " + Describe(members.Concat(overlaps)) +
                    "；请统一组名及合约类型，或关闭多余组的同步";
                Pause(message);
                foreach (var other in overlaps) { DropSession(Key(other)); other.Status(message); }
                return;
            }
            if (left.Select(x => x.Bridge.Instrument.Identity).Distinct().Count() != 1 ||
                right.Select(x => x.Bridge.Instrument.Identity).Distinct().Count() != 1)
            {
                Pause("暂停：同侧图表的平台品种身份不同，请使用相同数据源和合约");
                return;
            }
            if (charts.Any(x => x.Bridge.ThreadId != Environment.CurrentManagedThreadId))
            {
                Pause("暂停：图表不在同一个 GUI 线程，验证版不支持");
                return;
            }
            Session? session = null;
            try
            {
                if (members.Any(x => !x.Active())) { Pause("等待图表重新连接"); return; }
                foreach (var chart in charts) chart.Bridge.CheckReady();
                if (_sessions.TryGetValue(key, out session) && !session.Members.SetEquals(members.Select(x => x.Id)))
                {
                    DropSession(key);
                    session = null;
                }
                if (session is null)
                {
                    // Keep v0.1 scope so existing IDs can be promoted to Global without duplicates.
                    var scope = string.Join("\n", key, left[0].Bridge.Instrument.Identity, right[0].Bridge.Instrument.Identity);
                    var journal = new PairJournal(scope, journalDirectory);
                    try
                    {
                        session = new() { Members = members.Select(x => x.Id).ToHashSet(), Journal = journal, Engine = new(journal.Load(), journal.Save) };
                        _sessions.Add(key, session);
                    }
                    catch { journal.Dispose(); throw; }
                }
                if (session.Fault is not null)
                {
                    foreach (var member in members) member.Status(session.Fault);
                    return;
                }
                var l = ReadSide(left);
                var r = ReadSide(right);
                if (l is null || r is null) return;
                var changed = false;
                session.Engine.Step(l, r, edit =>
                {
                    if (members.Any(x => !x.Active())) throw new InvalidOperationException("同步设置已变化。");
                    (edit.ToLeft ? left[0] : right[0]).Bridge.Apply(edit);
                    changed = true;
                });
                var status = $"同步中 组「{endpoint.Group}」 {left[0].Bridge.Instrument.Root} ↔ {right[0].Bridge.Instrument.Root} {left[0].Bridge.Instrument.PeriodLabel} | 配对 {session.Engine.PairCount} | 图表 {left.Length}+{right.Length} | 仅全局水平线";
                if (session.Engine.WaitingPairs > 0) status += $" | 暂挂 {session.Engine.WaitingPairs}（配对另一侧未开启全局或对象未完成）";
                if (session.Engine.ConflictCount > 0) status += $" | 同时编辑冲突 {session.Engine.ConflictCount}，大合约优先";
                foreach (var member in members)
                {
                    member.Status(status);
                    if (changed) member.Redraw();
                }
            }
            catch (Exception ex)
            {
                var message = "停止：" + ex.GetBaseException().Message + "；关闭并重新启用同步后重试";
                if (session is not null) session.Fault = message;
                else DropSession(key);
                foreach (var member in members) member.Status(message);
            }
        }
    }
}
