using System.Reflection;
using System.Runtime.Loader;
using WolfMoss.ATAS.DrawingSync;

internal static partial class Program
{
    private static int _passed;
    private static readonly LineState A = new(22000m, 0xFFCCAA44, 2, 0, "test", 12, 0, false);
    private static readonly LineState B = A with { Price = 22001.25m, Text = "edited", Width = 3 };
    private static readonly RectangleState Rect = new(new DateTime(2026, 10, 16, 14, 30, 0),
        new DateTime(2026, 10, 16, 15, 45, 0), 22000m, 22025.25m, 0xFF00AAFF, 2, 0,
        0x553399FF, 6, "区域", 1, 1, false, true, new(true, 0xFFAA66CC, 3, 1));

    [STAThread]
    private static int Main()
    {
        try
        {
            EngineTests();
            UnlinkTests();
            RectangleTests();
            ReleaseEngineTests();
            JournalTests();
            HubTests();
            SchedulerTests();
            NativeTests();
            Console.WriteLine($"RESULT {_passed} checks passed. No running ATAS chart was modified.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL " + name);
        _passed++;
        Console.WriteLine("PASS " + name);
    }

    private static void Throws(Action action, string name)
    {
        try { action(); } catch { Check(true, name); return; }
        throw new Exception("FAIL expected failure: " + name);
    }

    private sealed class World
    {
        public Dictionary<string, DrawingState> L = [];
        public Dictionary<string, DrawingState> R = [];
        public HashSet<string> ExtraL = [];
        public HashSet<string> ExtraR = [];
        public HashSet<string> LocalL = [];
        public HashSet<string> LocalR = [];
        public Binding[] Saved = [];
        public List<Edit> Edits = [];
        public SyncEngine Engine;
        public World() { Engine = New(); }
        public SyncEngine New() => new(Saved, b => Saved = b.ToArray());
        public void Step()
        {
            Edits.Clear();
            Engine.Step(new(new(L), L.Keys.Concat(ExtraL).Concat(LocalL).ToHashSet(), new(LocalL)),
                new(new(R), R.Keys.Concat(ExtraR).Concat(LocalR).ToHashSet(), new(LocalR)), edit =>
            {
                Edits.Add(edit);
                var dictionary = edit.ToLeft ? L : R;
                if (edit.Kind == EditKind.Delete) dictionary.Remove(edit.Id);
                else if (edit.Kind == EditKind.Create) dictionary.Add(edit.Id, edit.State!);
                else dictionary[edit.Id] = edit.State!;
            });
        }
    }

    private static void EngineTests()
    {
        var w = new World();
        w.L.Add("a", A); w.Step();
        Check(w.R.Count == 1 && w.R.Values.Single() == A && w.Saved.Length == 1, "new mini line creates native counterpart mapping");
        var microId = w.R.Keys.Single();
        w.Step(); Check(w.Edits.Count == 0 && w.Engine.PairCount == 1, "unchanged frames never echo or duplicate");
        w.R[microId] = B; w.Step(); Check(w.L["a"] == B && w.Edits.Single().ToLeft, "micro edit propagates to mini");
        w.L["a"] = A; w.Step(); Check(w.R[microId] == A, "mini edit propagates to micro");
        w.L["a"] = B; w.R[microId] = A with { Price = 999 }; w.Step();
        Check(w.R[microId] == B && w.Engine.ConflictCount == 1, "simultaneous edits use documented mini priority");
        w.L.Remove("a"); w.Step(); Check(w.R.Count == 0 && w.Edits.Single().Kind == EditKind.Delete && w.Engine.PairCount == 0, "observed mini deletion propagates and active count drops immediately");
        w.Step(); Check(w.Edits.Count == 0, "deleted lines stay absent without resurrection");
        w.L.Add("a", B); w.Step(); Check(w.R[microId] == B, "native undo restoring mini ID restores paired micro ID");
        w.R.Remove(microId); w.Step(); Check(w.L.Count == 0, "micro deletion propagates to mini");
        w.R.Add(microId, A); w.Step(); Check(w.L["a"] == A, "native undo restores in reverse direction");
        w.Engine = w.New(); w.Step(); Check(w.Edits.Count == 0 && w.Engine.PairCount == 1, "restart resumes saved IDs without duplicate creation");
        w.R.Remove(microId); w.Engine = w.New(); w.Step();
        Check(w.L.Count == 1 && w.R[microId] == A && w.Edits.Single().Kind == EditKind.Create && w.Engine.PairCount == 1, "missing counterpart after reconnect repaired from survivor using saved ID");
        w.Step(); Check(w.Edits.Count == 0, "repaired counterpart resumes without duplicates");
        w.R.Remove(microId); w.ExtraR.Add(microId); w.Step();
        Check(w.L.Count == 1 && w.Edits.Count == 0 && w.Engine.PairCount == 0, "unsupported or unfinished line never mutates peer or counts active");
        w.ExtraR.Clear(); w.Step(); Check(w.Edits.Count == 0 && w.L.Count == 1, "removing a previously excluded object does not cascade delete");
        w.R.Add(microId, B); w.Step(); Check(w.L["a"] == B, "return to eligibility preserves reverse-edit baseline");

        var merge = new World(); merge.L.Add("l", A); merge.R.Add("r", A); merge.Step();
        Check(merge.L.Count == 2 && merge.R.Count == 2 && merge.Saved.Length == 2, "same-price independent originals are not accidentally deduplicated");
        merge.Step(); Check(merge.Edits.Count == 0, "initial two-sided merge converges");
        var reverse = new World(); reverse.R.Add("r", B); reverse.Step();
        Check(reverse.L.Values.Single() == B, "micro-origin new line supported");
        var noApply = false;
        var failing = new SyncEngine([], _ => throw new IOException("disk full"));
        Throws(() => failing.Step(new(new() { ["a"] = A }, ["a"]), new([], []), _ => noApply = true), "journal failure aborts creation");
        Check(!noApply, "no native write before durable mapping");
        Throws(() => new SyncEngine([new("x", "y"), new("x", "z")], _ => { }), "duplicate persisted IDs rejected");

        var history = new World();
        for (var i = 0; i < 2100; i++)
        {
            history.L["line-" + i] = A;
            history.Step();
            history.L.Clear(); history.Step();
        }
        Check(history.Engine.PairCount == 0 && history.R.Count == 0, "2100 create/delete cycles do not accumulate active pairs or hit lifetime capacity");
        Check(history.Saved.Length <= SyncEngine.UndoHistoryLimit, "deleted ID journal is bounded to 256 undo mappings");
        history.Engine = history.New(); history.Step();
        Check(history.Edits.Count == 0 && history.Engine.PairCount == 0, "restart does not resurrect deleted history");

        var upgrade = new World { Saved = [new("old-mini", "old-micro")] };
        upgrade.LocalL.Add("old-mini"); upgrade.LocalR.Add("old-micro");
        upgrade.Engine = upgrade.New(); upgrade.Step();
        Check(upgrade.Edits.Count == 0 && upgrade.Engine.PairCount == 0 && upgrade.Engine.WaitingPairs == 0, "v0.1 local pairs ignored on upgrade");
        upgrade.L["old-mini"] = A; upgrade.LocalL.Clear(); upgrade.Step();
        Check(upgrade.Edits.Count == 0 && upgrade.Engine.WaitingPairs == 1, "one legacy line promoted never overwrites its non-global peer");
        upgrade.R["old-micro"] = A; upgrade.LocalR.Clear(); upgrade.Step();
        Check(upgrade.Engine.PairCount == 1 && upgrade.Edits.Count == 0, "both legacy lines promoted reuse mapping without duplicate mirrors");

        var nq = InstrumentKey.Create("NQ", new(2026, 12, 18), "NQ.CME", "NQZ6", false, false);
        var mnq = InstrumentKey.Create("MNQ", new(2026, 12, 18), "MNQ.CME", "MNQZ6", false, false);
        var es = InstrumentKey.Create("ES", new(2026, 12, 18), "ES.CME", "ESZ6", false, false);
        Check(nq.Family == mnq.Family && nq.Month == mnq.Month && !nq.IsMicro && mnq.IsMicro, "NQ/MNQ explicit month grouping");
        Check(es.Family != nq.Family, "ES and NQ groups isolated");
        Check(InstrumentKey.Create("MES", new(2026, 12, 18), "MES.CME", "MESZ6", false, false).Family == es.Family, "ES/MES pairing");
        Throws(() => InstrumentKey.Create("NQ", new(2026, 12, 18), "CME", "NQ", true, false), "continuous contracts blocked");
        Throws(() => InstrumentKey.Create("NQ", new(2026, 12, 18), "CME", "NQ", false, true), "replay blocked");
        Throws(() => InstrumentKey.Create("NQ", default, "CME", "NQ", false, false), "unknown expiration blocked");
        Throws(() => InstrumentKey.Create("NQM", new(2026, 12, 18), "CME", "NQ", false, false), "ambiguous symbol blocked");
        var continuousEs = InstrumentKey.Create("ES", new(2026, 12, 18), "ES.CME", "ESZ6", true, false, "#ESZ6");
        var continuousMes = InstrumentKey.Create("MES", new(2026, 12, 18), "MES.CME", "MESZ6", true, false, "#MESZ6");
        Check(continuousEs.Month == "202612" && continuousEs.ContinuousHistory, "regression: screenshot #ESZ6 with continuous history is accepted");
        Check(continuousMes.Family == continuousEs.Family && continuousMes.Month == continuousEs.Month && continuousMes.GroupSuffix == continuousEs.GroupSuffix,
            "regression: screenshot #MESZ6 pairs with #ESZ6");
        Check(continuousEs.GroupSuffix != es.GroupSuffix, "spliced and single-month charts use isolated synchronization groups");
        Check(InstrumentKey.Create("NQ", new(2026, 12, 18), "NQ.CME", "NQZ6", true, false, "NQZ6").ContinuousHistory,
            "continuous chart flag accepted even when underlying native code has no hash prefix");
        Check(InstrumentKey.Create("MNQ", new(2027, 3, 19), "MNQ.CME", "MNQH27", true, false, "MNQH27").Month == "202703", "two-digit year uses native expiration");
        Check(InstrumentKey.Create("ES", new(2026, 12, 18), "ES.CME", "ESZ2026", true, false, "ESZ2026").Month == "202612", "four-digit year supported");
        Throws(() => InstrumentKey.Create("ES", new(2026, 12, 18), "ES.CME", "ES", true, false, "#ES"), "undated continuous contract stays blocked");
        Throws(() => InstrumentKey.Create("ES", new(2026, 12, 18), "ES.CME", "ES", true, false, "ESU6"), "symbol and expiration month mismatch blocked");
        Throws(() => InstrumentKey.Create("ES", new(2026, 12, 18), "ES.CME", "ES", true, false, "ESZ7"), "symbol and expiration year mismatch blocked");
        Throws(() => InstrumentKey.Create("ES", new(2026, 12, 18), "ES.CME", "ES", true, false, "MESZ6"), "different root cannot masquerade as matching dated contract");
        Throws(() => InstrumentKey.Create("ES", new(2026, 12, 18), "ES.CME", "ES", true, true, "#ESZ6"), "dated continuous chart never bypasses replay guard");
        try { InstrumentKey.Create("ES", new(2026, 12, 18), "ES.CME", "ES", false, true, "ESZ6"); }
        catch (InvalidOperationException ex) { Check(ex.Message.Contains("Market Replay"), "replay diagnostic names actual blocking condition"); }
        // Runtime shape from the user's 0.1.1 screenshot: Name=ES/MES, expiry=0001-01-01,
        // instrument.Continuous=true, data.IsContinuous=false, replay=false.
        var mainEs = InstrumentKey.Create("ES", DateTime.MinValue, "ES.CME", "ES", true, false, "ES", true);
        var mainMes = InstrumentKey.Create("MES", DateTime.MinValue, "MES.CME", "MES", true, false, "MES", true);
        Check(mainEs.IsUndatedContinuous && mainEs.Month == "CONTINUOUS", "actual screenshot ES main contract accepts missing expiration without guessing month");
        Check(mainMes.IsMicro && mainEs.PairingKey == mainMes.PairingKey, "actual screenshot ES and MES main contracts share production pairing key");
        Check(mainEs.PairingKey != es.PairingKey && mainEs.PairingKey != continuousEs.PairingKey, "undated main contracts never pair with dated single or spliced contracts");
        Check(mainEs.PeriodLabel.Contains("主连") && !mainEs.PeriodLabel.Contains("2026"), "main contract status does not claim a verified expiry");
        var mainNq = InstrumentKey.Create("NQ", default, "NQ.CME", "NQ", true, false, "NQ", true);
        var mainMnq = InstrumentKey.Create("MNQ", default, "MNQ.CME", "MNQ", true, false, "MNQ", true);
        Check(mainNq.PairingKey == mainMnq.PairingKey && mainNq.PairingKey != mainEs.PairingKey, "NQ/MNQ main-contract pair isolated from ES/MES");
        Check(InstrumentKey.Create("ES", default, "ES.CME", "ES", true, false, "#ES", true).PairingKey == mainEs.PairingKey,
            "native main-contract code with hash prefix accepted");
        Throws(() => InstrumentKey.Create("ES", default, "ES.CME", "ES", true, false, "ES", false), "history splicing alone cannot authorize an undated main contract");
        Throws(() => InstrumentKey.Create("ES", default, "ES.CME", "ES", false, false, "ES", true), "inconsistent continuity flags rejected");
        Throws(() => InstrumentKey.Create("ES", default, "ES.CME", "ES", true, true, "ES", true), "main-contract handling cannot bypass replay guard");
        Throws(() => InstrumentKey.Create("ES", default, "ES.CME", "ES", true, false, "MES", true), "undated root/code mismatch rejected");
        Throws(() => InstrumentKey.Create("ES", default, "ES.CME", "", true, false, "ES", true), "main contract still requires unique identity");
        Throws(() => InstrumentKey.Create("ES", default, "ES.CME", "ESZ6", true, false, "ESZ6", true), "dated code without expiry cannot silently become an undated main contract");
    }

    private static void UnlinkTests()
    {
        foreach (var fromMini in new[] { true, false })
        {
            var w = new World(); w.L["original"] = A; w.Step();
            var binding = w.Saved.Single();
            var keptId = fromMini ? binding.Left : binding.Right;
            var local = fromMini ? w.LocalL : w.LocalR;
            (fromMini ? w.L : w.R).Remove(keptId); local.Add(keptId);
            w.Step();
            Check(local.Contains(keptId) && w.L.Count == 0 && w.R.Count == 0 &&
                w.Edits.Single() is { Kind: EditKind.Delete } edit && edit.ToLeft == !fromMini,
                $"Global off from {(fromMini ? "mini" : "micro")} preserves local original and deletes only opposite mirror");
            Check(w.Saved.Length == 0 && w.Engine.PairCount == 0 && w.Engine.WaitingPairs == 0,
                "unlink removes mapping and clears active/waiting counts");
            w.Engine = w.New(); w.Step();
            Check(w.Edits.Count == 0 && w.L.Count == 0 && w.R.Count == 0, "unlinked local line cannot resurrect mirror after restart");
            local.Remove(keptId); (fromMini ? w.L : w.R)[keptId] = B; w.Step();
            Check(w.L.Count == 1 && w.R.Count == 1 && w.L.Values.Single() == B && w.R.Values.Single() == B,
                "Global re-enabled establishes exactly one new pair");
        }
        var reconnect = new World(); reconnect.L["source"] = A; reconnect.Step();
        var saved = reconnect.Saved.Single();
        reconnect.L.Clear(); reconnect.LocalL.Add("source"); reconnect.Engine = reconnect.New(); reconnect.Step();
        Check(reconnect.R.Count == 0 && reconnect.Saved.Length == 0, "persisted global confirmation detects Global off across reconnect");

        var both = new World(); both.L["source"] = A; both.Step();
        both.LocalL.Add("source"); both.LocalR.Add(both.R.Keys.Single()); both.L.Clear(); both.R.Clear(); both.Step();
        Check(both.Edits.Count == 0 && both.Saved.Length == 0 && both.LocalL.Count == 1 && both.LocalR.Count == 1,
            "both sides explicitly localized before scan retain both user local objects without conflict");

        var localSnapshot = new ChartSnapshot([], [saved.Left], [saved.Left]);
        var globalSnapshot = new ChartSnapshot(new() { [saved.Right] = A }, [saved.Right], []);
        Binding[] journal = [saved];
        var failedDelete = new SyncEngine(journal, b => journal = b.ToArray());
        Throws(() => failedDelete.Step(localSnapshot, globalSnapshot, _ => throw new IOException("native delete failed")),
            "failed native unlink deletion stops the group");
        Check(journal.Single() == saved, "mapping retained until mirror deletion is confirmed");
        var retried = new SyncEngine(journal, b => journal = b.ToArray());
        var deletions = 0;
        retried.Step(localSnapshot, globalSnapshot, _ => deletions++);
        Check(deletions == 1 && journal.Length == 0, "reconnect retries failed unlink rather than orphaning mirror");

        var persistFailure = new SyncEngine([saved], _ => throw new IOException("disk full"));
        Throws(() => persistFailure.Step(localSnapshot, globalSnapshot, _ => { }), "failed unlink journal write stops after confirmed deletion");
        var recover = new SyncEngine([saved], b => journal = b.ToArray());
        recover.Step(localSnapshot, new([], [], []), _ => throw new Exception("unexpected resurrection"));
        Check(journal.Length == 0, "old journal after unlink persistence failure cleans up without recreating mirror");
    }

    private static void RectangleTests()
    {
        var w = new World(); w.L["line"] = A; w.L["rect"] = Rect; w.Step();
        var rectPair = w.Saved.Single(x => x.Kind == DrawingKind.Rectangle);
        Check(w.Engine.PairCount == 2 && w.R[rectPair.Right] == Rect, "rectangle and horizontal line coexist with distinct persisted kinds");
        w.Step(); Check(w.Edits.Count == 0, "unchanged rectangles never echo");
        var middleEdited = Rect with { MiddleLine = new(false, 0xFF44AA22, 5, 2) };
        w.R[rectPair.Right] = middleEdited; w.Step();
        Check(w.L["rect"] == middleEdited && w.Edits.Single().ToLeft,
            "middle-line-only micro changes propagate enable color width and style without moving rectangle");
        w.L["rect"] = Rect; w.Step();
        Check(w.R[rectPair.Right] == Rect, "middle-line-only mini change restores enabled state in reverse direction");
        w.Step(); Check(w.Edits.Count == 0, "middle line synchronization converges without echo");
        var edited = Rect with { FirstTime = Rect.FirstTime.AddMinutes(-5), SecondTime = Rect.SecondTime.AddMinutes(15),
            FirstPrice = 21990m, SecondPrice = 22030m, AreaColor = 0x4400FF00, Text = "调整", ExtendRight = false };
        w.R[rectPair.Right] = edited; w.Step();
        Check(w.L["rect"] == edited && w.L["line"] == A, "reverse rectangle move resize fill and text edit leaves horizontal line untouched");
        w.Engine = w.New(); w.R.Remove(rectPair.Right); w.Step();
        Check(w.R[rectPair.Right] == edited && w.Edits.Single().State is RectangleState, "reconnect repairs rectangle with its saved kind and exact time anchors");
        w.L.Remove("rect"); w.LocalL.Add("rect"); w.Step();
        Check(!w.R.ContainsKey(rectPair.Right) && w.Engine.PairCount == 1 && w.Engine.WaitingPairs == 0 && w.LocalL.Contains("rect"),
            "rectangle Global off removes only its mirror and preserves horizontal pair");
        w.LocalL.Clear(); w.L["rect"] = edited; w.Step();
        var newPair = w.Saved.Single(x => x.Kind == DrawingKind.Rectangle);
        Check(w.R[newPair.Right] == edited && w.Engine.PairCount == 2, "rectangle Global re-enabled creates one new mirror");
        w.R.Remove(newPair.Right); w.Step();
        Check(!w.L.ContainsKey("rect") && w.Engine.PairCount == 1, "micro rectangle deletion propagates without deleting horizontal lines");
        w.R[newPair.Right] = edited; w.Step();
        Check(w.L["rect"] == edited, "rectangle undo restores matching type");
        w.R[newPair.Right] = A; w.Step();
        Check(w.Edits.Count == 0 && w.Engine.WaitingPairs == 1 && w.L["rect"] == edited, "unexpected rectangle-to-line ID type change is suspended rather than overwritten");
    }

    private static void JournalTests()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        using (var journal = new PairJournal("scope", directory))
        {
            Check(journal.Load().Count == 0, "new journal starts empty");
            journal.Save([new("a", "b")]);
            Check(journal.Load().Single() == new Binding("a", "b"), "durable mapping round trip");
            Throws(() => { using var duplicate = new PairJournal("scope", directory); }, "second process/session cannot claim same journal");
            journal.Save([new("a", "b"), new("c", "d")]);
            Check(journal.Load().Count == 2, "atomic journal replacement");
            journal.Save([new("a", "b", true, DrawingKind.Rectangle), new("c", "d")]);
            Check(journal.Load()[0].GlobalPair && !journal.Load()[1].GlobalPair, "global-pair confirmation persists alongside legacy bindings");
            Check(journal.Load()[0].Kind == DrawingKind.Rectangle && journal.Load()[1].Kind == DrawingKind.HorizontalLine,
                "rectangle kind round-trips while old bindings remain horizontal lines");
            journal.Save([.. journal.Load(), new("ray", "ray-peer", true, DrawingKind.HorizontalRay), new("trend", "trend-peer", true, DrawingKind.TrendLine)]);
            Check(journal.Load().Select(b => b.Kind).Distinct().Count() == 4, "all four drawing kind IDs survive actual JSON journal round trip");
        }
        using (var journal = new PairJournal("scope", directory))
        {
            Check(journal.Load().Count == 4, "journal can reopen after clean release");
            File.WriteAllText(Directory.GetFiles(directory, "*.json").Single(), "{broken");
            Throws(() => journal.Load(), "corrupt journal fails closed");
        }
        foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
        Directory.Delete(directory);
    }

    private sealed class FakeBridge(InstrumentKey instrument, Dictionary<string, DrawingState> shared) : ISyncBridge
    {
        private static int _nextChartId;
        public int ChartId { get; } = Interlocked.Increment(ref _nextChartId);
        public InstrumentKey Instrument => instrument;
        public int ThreadId => Environment.CurrentManagedThreadId;
        public object Core { get; } = new();
        public int Writes;
        public int Reads;
        public bool SharesInstrumentSnapshot { get; set; }
        public bool Ready = true;
        public bool Reading = true;
        public HashSet<string> LocalIds = [];
        public void CheckReady() { if (!Ready) throw new InvalidOperationException("loading"); }
        public ChartSnapshot? Read() { Reads++; return Reading ? new(new(shared), shared.Keys.Concat(LocalIds).ToHashSet(), new(LocalIds)) : null; }
        public void Apply(Edit edit)
        {
            Writes++;
            if (edit.Kind == EditKind.Create) shared.Add(edit.Id, edit.State!);
            else if (edit.Kind == EditKind.Update) shared[edit.Id] = edit.State!;
            else shared.Remove(edit.Id);
        }
    }

    private static void HubTests()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        var hub = new SyncHub(directory, intervalMilliseconds: 0);
        var l = new Dictionary<string, DrawingState>();
        var r = new Dictionary<string, DrawingState>();
        var mini = InstrumentKey.Create("ES", default, "ES.CME", "ES", true, false, "ES", true);
        var micro = InstrumentKey.Create("MES", default, "MES.CME", "MES", true, false, "MES", true);
        var statuses = new Dictionary<Guid, string>();
        var endpoints = new List<SyncHub.Endpoint>();
        SyncHub.Endpoint Add(bool isMicro, string group = "验证1")
        {
            var bridge = new FakeBridge(isMicro ? micro : mini, isMicro ? r : l);
            var id = Guid.NewGuid();
            var endpoint = new SyncHub.Endpoint(id, bridge, group, () => true, s => statuses[id] = s, () => { });
            endpoints.Add(endpoint);
            hub.Tick(endpoint);
            return endpoint;
        }
        void TickAll() { foreach (var e in endpoints.ToArray()) hub.Tick(e); }
        void Remove(SyncHub.Endpoint e) { hub.Remove(e.Id); endpoints.Remove(e); }
        try
        {
            var orphanAtStartup = Add(false, "A");
            var es1 = Add(false);
            var mes1 = Add(true);
            var mes2 = Add(true);
            l["original"] = A; TickAll();
            var peerId = r.Keys.Single();
            Check(r.Count == 1 && statuses[mes2.Id].Contains("图表 1+2"), "one ES and two MES share a single mirror");
            Check(statuses[orphanAtStartup.Id].StartsWith("等待") && statuses[es1.Id].StartsWith("同步中"),
                "orphan group registered before complete group does not block initial pairing");
            Remove(orphanAtStartup);
            var es2 = Add(false);
            var mes3 = Add(true); TickAll();
            Check(l.Count == 1 && r.Count == 1 && statuses[es2.Id].Contains("图表 2+3"), "two ES and three MES join existing global pair without duplicates");
            Check(endpoints.Sum(x => ((FakeBridge)x.Bridge).Writes) == 1, "multiple chart heartbeats produce exactly one native creation");
            r[peerId] = B; hub.Tick(mes3);
            Check(l["original"] == B && statuses.Values.Count(s => s.Contains("配对 1")) == 5, "edit from a non-representative micro chart updates shared mini line");
            Remove(mes1); TickAll();
            Check(r[peerId] == B && statuses[es1.Id].Contains("图表 2+2"), "closing representative chart retains lines and elects remaining chart");
            Remove(mes2); Remove(mes3); hub.Tick(es1);
            Check(l.Count == 1 && statuses[es1.Id].Contains("等待"), "closing all micro charts never deletes mini survivor");
            r.Clear(); // Simulate old chart-local mirror disappearing while the side is disconnected.
            var reopened = Add(true); TickAll();
            Check(r.Count == 1 && r[peerId] == B && statuses[reopened.Id].Contains("配对 1"), "reopened micro chart repairs old missing target ID from saved mapping");
            Remove(reopened);
            var reopenedAgain = Add(true); TickAll();
            Check(r.Count == 1 && r[peerId] == B, "reopening with native Global survivor produces no duplicate");
            l.Clear(); TickAll();
            Check(r.Count == 0 && statuses[reopenedAgain.Id].Contains("配对 0"), "multi-chart deletion propagates and active status becomes zero");
            l["new-global"] = A; TickAll();
            var newPeer = r.Keys.Single();
            var duplicate = es2 with { Id = Guid.NewGuid(), Status = s => statuses[es2.Id] = s };
            endpoints.Add(duplicate); hub.Tick(duplicate);
            Check(r.Count == 1 && statuses[es2.Id].Contains("图表 2+1"), "duplicate indicator on same chart does not duplicate chart count or mirrors");
            Remove(duplicate);
            var otherGroup = Add(true, "another"); TickAll();
            Check(statuses[es1.Id].StartsWith("同步中") && statuses[otherGroup.Id].StartsWith("等待") && r.Count == 1,
                "one-sided foreign group waits without blocking complete group");
            Check(statuses[otherGroup.Id].Contains("组「another」") && statuses[otherGroup.Id].Contains("组「验证1」") &&
                statuses[otherGroup.Id].Contains($"图#{es1.Bridge.ChartId}"), "waiting diagnostic identifies both group names and actual chart IDs");
            l["new-global"] = B; TickAll();
            Check(r[newPeer] == B, "complete group continues native edits while foreign side waits");
            var otherMini = Add(false, "another"); TickAll();
            Check(statuses[es1.Id].Contains("全局同步冲突") && statuses[otherGroup.Id].Contains("全局同步冲突") && r.Count == 1,
                "overlapping global groups pause before copying each other's mirrors");
            Check(statuses[es1.Id].Contains("组「another」") && statuses[es1.Id].Contains("组「验证1」") &&
                statuses[es1.Id].Contains($"图#{otherMini.Bridge.ChartId}"), "conflict diagnostic identifies complete conflicting groups and charts");
            Remove(otherMini); TickAll();
            Check(r[newPeer] == B && statuses[es1.Id].StartsWith("同步中") && statuses[otherGroup.Id].StartsWith("等待"),
                "complete group automatically resumes when rival loses one side");
            Remove(otherGroup);
            l["new-global"] = A; TickAll();
            Check(r[newPeer] == A && statuses[es1.Id].StartsWith("同步中"), "unified group resumes existing journal without duplicates");
            ((FakeBridge)es2.Bridge).Reading = false;
            l["new-global"] = B; TickAll();
            Check(r[newPeer] == A, "incomplete snapshot from any chart defers the whole group");
            ((FakeBridge)es2.Bridge).Reading = true; TickAll();
            Check(r[newPeer] == B, "complete snapshots resume deferred edit");
            l.Remove("new-global"); ((FakeBridge)es2.Bridge).LocalIds.Add("new-global");
            TickAll();
            Check(r.Count == 0 && ((FakeBridge)es2.Bridge).LocalIds.Contains("new-global") &&
                statuses[es1.Id].Contains("配对 0") && !statuses[es1.Id].Contains("暂挂"),
                "Global off on non-representative chart propagates unlink through merged multi-chart snapshots");
            l["rect"] = Rect; TickAll();
            Check(r.Values.Single() == Rect && statuses[es1.Id].Contains("配对 1"), "multi-chart group creates one rectangle mirror");
            var rectId = r.Keys.Single(); r[rectId] = Rect with { SecondPrice = 22100m }; TickAll();
            Check(l["rect"] == r[rectId], "multi-chart rectangle resize propagates in reverse");
            r.Clear(); ((FakeBridge)reopenedAgain.Bridge).LocalIds.Add(rectId); TickAll();
            Check(!l.ContainsKey("rect") && statuses[es1.Id].Contains("配对 0"), "multi-chart rectangle unlink removes shared opposite rectangle");
        }
        finally
        {
            foreach (var e in endpoints) hub.Remove(e.Id);
            if (Directory.Exists(directory))
            {
                foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
                Directory.Delete(directory);
            }
        }
    }

    private static void NativeTests()
    {
        var directory = Environment.GetEnvironmentVariable("ATAS_INSTALL_DIR") ?? @"C:\Program Files (x86)\ATAS Platform";
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(directory, "OFT.Platform.Core.dll"));
        NativeBridge.ValidateAssembly(assembly);
        Check(true, "installed native assembly version matches adapter");
        var line = assembly.GetType("OFT.Platform.DrawingObjects.HLine", true)!;
        NativeBridge.ValidateLineShape(line);
        Check(true, "actual native HLine has all required mutable members");
        var core = assembly.GetType("OFT.Platform.ViewModels.Charting.ChartCore", true)!;
        Check(line.GetConstructor([core]) is not null, "actual native HLine accepts target ChartCore");
        var coordinates = assembly.GetType("Advanced_Time_And_Sales.Chartingg.ChartCoordinates", true)!;
        Check(coordinates.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Count(f => f.FieldType == core) == 1,
            "unambiguous private ChartCore access by type rather than obfuscated name");
        var collection = assembly.GetType("OFT.Platform.DrawingObjects.DrawingObjectsCollection", true)!;
        Check(R.Method(collection, "Delete", 1, line).GetParameters()[0].ParameterType.Name == "DrawingObject", "delete overload selects individual drawing only");
        Check(R.Method(collection, "AddGlobalDrawingObject", 3).GetParameters()[2].Name == "saveToClipboard", "native add supports avoiding clipboard writes");
        Check(R.Method(collection, "SaveDrawingObjects", 1) is not null, "native save endpoint exists");
        Check(R.Method(core, "ParentGuiAsync", 1).GetParameters()[0].ParameterType == typeof(Action), "native chart GUI dispatch endpoint exists");
        var data = core.GetProperty("Data")!.PropertyType;
        Check(data.GetField("IsContinuous") is not null && data.GetProperty("Uid") is not null, "native chart identity and continuous flag shape");
        var view = core.GetProperty("ParentViewModel")!.PropertyType;
        Check(view.GetProperty("ReplayStateListener")?.PropertyType.GetProperty("IsConnected") is not null, "native replay guard metadata");
        var nativeLine = Activator.CreateInstance(line)!;
        R.Set(nativeLine, "IsUnderCursor", false);
        NativeBridge.WriteLine(nativeLine, A);
        Check(NativeBridge.ReadLine(nativeLine) == A, "actual native HLine property round trip without live chart");
        NativeBridge.WriteLine(nativeLine, B);
        Check(NativeBridge.ReadLine(nativeLine) == B, "actual native HLine price text and style update");
        var storeType = assembly.GetType("OFT.Platform.DrawingObjects.GlobalTradingObjectsStore", true)!;
        var managerType = storeType.GetConstructors().Single().GetParameters()[0].ParameterType;
        var manager = DispatchProxy.Create(managerType, typeof(EmptyProxy));
        var store = Activator.CreateInstance(storeType, manager)!;
        var nativeCollection = Activator.CreateInstance(collection, store, 12345)!;
        Check(ReferenceEquals(NativeBridge.FindStore(nativeCollection), store), "production adapter discovers actual shared native store by interface type");
        R.Set(nativeLine, "Id", "probe-test-id");
        R.Set(nativeLine, "InstrumentName", "NQ");
        R.Set(nativeLine, "InstrumentUniqName", "NQZ6-test");
        R.Call(nativeCollection, "AddGlobalDrawingObject", nativeLine, false, false);
        var stored = ((System.Collections.IEnumerable)R.Call(store, "Get", "NQZ6-test", "NQ", 12345)!).Cast<object>().ToArray();
        Check(stored.Length == 1 && ReferenceEquals(stored[0], nativeLine), "native collection inserts completed local HLine into actual store");
        Check(!((System.Collections.IEnumerable)R.Call(store, "Get", "MNQZ6-test", "MNQ", 54321)!).Cast<object>().Any(), "native storage keeps instrument/chart identities isolated");
        Check(NativeBridge.Snapshot(stored, line).Objects.Count == 0, "production filter excludes native non-global horizontal line");
        Check(NativeBridge.StoreIds(store).Contains("probe-test-id"), "global existence guard recognizes local peer even when its chart is not participating");
        R.Set(nativeLine, "IsGlobal", true);
        var otherChart = ((System.Collections.IEnumerable)R.Call(store, "Get", "NQZ6-test", "NQ", 98765)!).Cast<object>().ToArray();
        Check(otherChart.Length == 1 && ReferenceEquals(otherChart[0], nativeLine), "actual native Global object visible on a second chart without cloning");
        Check(NativeBridge.Snapshot(otherChart, line).Objects.Count == 1, "production filter accepts Global owned by a different chart ID");
        R.Set(nativeLine, "IsUnderCursor", true);
        Check(NativeBridge.Snapshot(otherChart, line).Objects.Count == 0 && NativeBridge.Snapshot(otherChart, line).AllIds.Contains("probe-test-id"), "unfinished Global stays identifiable but cannot be copied or treated as deletion");
        R.Set(nativeLine, "IsUnderCursor", false);
        R.Call(nativeCollection, "AddCache", nativeLine);
        NativeBridge.WriteLine(nativeLine, A);
        var lineArray = Array.CreateInstance(line, 1); lineArray.SetValue(nativeLine, 0);
        R.Call(nativeCollection, "SaveDrawingObjects", lineArray);
        Check(NativeBridge.ReadLine(stored[0]) == A, "native cache/save retains edited line state");
        R.Call(store, "DeleteChart", 12345);
        var reopenedChart = ((System.Collections.IEnumerable)R.Call(store, "Get", "NQZ6-test", "NQ", 22222)!).Cast<object>().ToArray();
        Check(reopenedChart.Length == 1 && NativeBridge.Snapshot(reopenedChart, line).Objects.Count == 1, "actual native Global survives closing its originating chart and is readable after reopen");
        var nativeMirror = Activator.CreateInstance(line)!;
        R.Set(nativeMirror, "Id", "probe-micro-id");
        R.Set(nativeMirror, "IsUnderCursor", false);
        R.Set(nativeMirror, "IsGlobal", true);
        R.Set(nativeMirror, "InstrumentName", "MNQ");
        R.Set(nativeMirror, "InstrumentUniqName", "MNQZ6-test");
        NativeBridge.WriteLine(nativeMirror, A);
        var microCollection = Activator.CreateInstance(collection, store, 33333)!;
        R.Call(microCollection, "AddGlobalDrawingObject", nativeMirror, false, false);
        // Native SetGlobalSelected(false) changes IsGlobal and moves ChartId to the active chart.
        R.Set(nativeLine, "IsGlobal", false); R.Set(nativeLine, "ChartId", 22222);
        object[] NativeRead(string symbol, string root, int chartId) =>
            ((System.Collections.IEnumerable)R.Call(store, "Get", symbol, root, chartId)!).Cast<object>().ToArray();
        var unlink = new SyncEngine([new("probe-test-id", "probe-micro-id", true)], _ => { });
        unlink.Step(NativeBridge.Snapshot(NativeRead("NQZ6-test", "NQ", 22222), line),
            NativeBridge.Snapshot(NativeRead("MNQZ6-test", "MNQ", 33333), line),
            edit =>
            {
                if (edit.ToLeft || edit.Kind != EditKind.Delete || edit.Id != "probe-micro-id")
                    throw new Exception("unlink tried to mutate retained native local line");
                R.Method(collection, "Delete", 1, line).Invoke(microCollection, [nativeMirror]);
            });
        Check(NativeRead("NQZ6-test", "NQ", 22222).Single() == nativeLine && NativeBridge.ReadLine(nativeLine) == A &&
            NativeRead("NQZ6-test", "NQ", 98765).Length == 0 && NativeRead("MNQZ6-test", "MNQ", 33333).Length == 0 &&
            NativeRead("MNQZ6-test", "MNQ", 44444).Length == 0 && unlink.PairCount == 0,
            "real native store unlink preserves current-chart local line and removes all other-chart views and micro mirror");
        R.Set(nativeLine, "IsUnderCursor", true);
        Check(!NativeBridge.IsLocalObject(nativeLine, line), "unfinished local HLine cannot trigger mirror deletion");
        R.Set(nativeLine, "IsUnderCursor", false);
        R.Method(collection, "Delete", 1, line).Invoke(nativeCollection, [nativeLine]);
        Check(!((System.Collections.IEnumerable)R.Call(store, "Get", "NQZ6-test", "NQ", 12345)!).Cast<object>().Any(), "native individual delete removes only test object");
        NativeRectangleTests(assembly, store, collection, line);
        NativeReleaseTests(assembly, store, collection);
        Throws(() => NativeBridge.ValidateAssembly(typeof(Program).Assembly), "unsupported assembly version blocked");
    }

    private static void NativeRectangleTests(Assembly assembly, object store, Type collection, Type line)
    {
        var rectangle = assembly.GetType("OFT.Platform.DrawingObjects.RectangleObj", true)!;
        NativeBridge.ValidateRectangleShape(rectangle);
        Check(rectangle.GetConstructor([assembly.GetType("OFT.Platform.ViewModels.Charting.ChartCore", true)!]) is not null,
            "native RectangleObj metadata and ChartCore constructor match adapter");
        object Make(string id, string root, int chartId, DrawingState state)
        {
            var obj = Activator.CreateInstance(rectangle)!;
            R.Set(obj, "IsUnderCursor", false); R.Set(obj, "AutoSetSecondPos", false);
            R.Set(obj, "IsGlobal", true); R.Set(obj, "Id", id);
            R.Set(obj, "InstrumentName", root); R.Set(obj, "InstrumentUniqName", root + "Z6-test");
            NativeBridge.WriteObject(obj, state);
            R.Call(Activator.CreateInstance(collection, store, chartId)!, "AddGlobalDrawingObject", obj, false, false);
            return obj;
        }
        object[] Read(string root, int chart) => ((System.Collections.IEnumerable)R.Call(store, "Get", root + "Z6-test", root, chart)!).Cast<object>().ToArray();
        var original = Make("rectangle-source", "NQ", 100, Rect);
        Check(NativeBridge.ReadObject(original) == Rect, "native rectangle time price border fill text and extension round trip");
        R.Set(original, "FirstPos", 999); R.Set(original, "SecondPos", 1000);
        NativeBridge.WriteObject(original, Rect with { FirstTime = Rect.FirstTime.AddMinutes(-1) });
        Check(R.Value<int>(original, "FirstPos") == 999 && R.Value<int>(original, "SecondPos") == 1000,
            "rectangle adapter uses native timestamps without copying obsolete chart bar indices");
        NativeBridge.WriteObject(original, Rect);
        R.Set(original, "AutoSetSecondPos", true);
        var pending = NativeBridge.Snapshot(Read("NQ", 100), line, rectangle);
        Check(pending.Objects.Count == 0 && pending.AllIds.Contains("rectangle-source"), "rectangle awaiting second corner is suspended without deletion");
        R.Set(original, "AutoSetSecondPos", false);
        R.Set(original, "_firstTime", default(DateTime));
        Check(NativeBridge.Snapshot(Read("NQ", 100), line, rectangle).Objects.Count == 0, "rectangle missing time anchor is not copied");
        NativeBridge.WriteObject(original, Rect);
        Throws(() => NativeBridge.WriteObject(original, A), "native rectangle cannot be overwritten by horizontal line payload");
        Binding[] saved = [];
        var engine = new SyncEngine([], b => saved = b.ToArray());
        void Apply(Edit edit)
        {
            var root = edit.ToLeft ? "NQ" : "MNQ";
            var chartId = edit.ToLeft ? 100 : 200;
            var nativeCollection = Activator.CreateInstance(collection, store, chartId)!;
            if (edit.Kind == EditKind.Create) { Make(edit.Id, root, chartId, edit.State!); return; }
            var obj = Read(root, chartId).Single(x => R.Value<string>(x, "Id") == edit.Id);
            if (edit.Kind == EditKind.Delete) R.Method(collection, "Delete", 1, rectangle).Invoke(nativeCollection, [obj]);
            else
            {
                R.Call(nativeCollection, "AddCache", obj); NativeBridge.WriteObject(obj, edit.State!);
                var array = Array.CreateInstance(rectangle, 1); array.SetValue(obj, 0);
                R.Call(nativeCollection, "SaveDrawingObjects", array);
            }
        }
        void Step() => engine.Step(NativeBridge.Snapshot(Read("NQ", 100), line, rectangle),
            NativeBridge.Snapshot(Read("MNQ", 200), line, rectangle), Apply);
        Step(); var mirrored = Read("MNQ", 200).Single();
        Check(NativeBridge.ReadObject(mirrored) == Rect && Read("MNQ", 201).Single() == mirrored && Read("NQ", 101).Single() == original,
            "real native store shares exactly one rectangle per instrument across multiple charts");
        var originalFilter = R.Get(original, "MiddleLine");
        var mirrorFilter = R.Get(mirrored, "MiddleLine");
        Check(!ReferenceEquals(originalFilter, mirrorFilter) && !ReferenceEquals(R.Get(originalFilter, "Value"), R.Get(mirrorFilter, "Value")),
            "mirrored rectangles never share mutable native middle-line settings");
        var frozen = NativeBridge.ReadRectangle(mirrored);
        var changedMiddle = new MiddleLineState(false, 0x8844CC22, 5, 2);
        NativeBridge.WriteMiddleLine(mirrored, changedMiddle);
        Check(frozen.MiddleLine == Rect.MiddleLine && NativeBridge.ReadMiddleLine(original) == Rect.MiddleLine,
            "editing native middle line leaves frozen snapshot and opposite object unchanged until reconciliation");
        Step();
        Check(NativeBridge.ReadRectangle(original) == Rect with { MiddleLine = changedMiddle } && saved.Length == 1,
            "real native middle-line-only edit synchronizes all four properties without recreating rectangle");
        NativeBridge.WriteMiddleLine(original, changedMiddle with { Enabled = true }); Step();
        Check(NativeBridge.ReadMiddleLine(mirrored) == changedMiddle with { Enabled = true },
            "real native middle line toggle from mini synchronizes back to micro");
        var disabled = changedMiddle with { Enabled = false, Color = 0xFFEE1155, Width = 2, Style = 0 };
        NativeBridge.WriteMiddleLine(original, disabled); Step();
        Check(NativeBridge.ReadMiddleLine(mirrored) == disabled, "disabled middle-line style remains synchronized for later re-enable");
        NativeBridge.WriteObject(mirrored, Rect with { SecondPrice = 22123.25m, AreaOpacity = 3, Text = "micro edited" }); Step();
        Check(NativeBridge.ReadObject(original) == NativeBridge.ReadObject(mirrored), "native rectangle resize and style save propagate in reverse");
        R.Set(mirrored, "IsGlobal", false); R.Set(mirrored, "ChartId", 200); Step();
        Check(Read("NQ", 100).Length == 0 && Read("MNQ", 201).Length == 0 && Read("MNQ", 200).Single() == mirrored && saved.Length == 0,
            "native rectangle Global off preserves local rectangle and removes all other chart views");
        R.Method(collection, "Delete", 1, rectangle).Invoke(Activator.CreateInstance(collection, store, 200), [mirrored]);
    }
}

public class EmptyProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var type = targetMethod!.ReturnType;
        if (type == typeof(void)) return null;
        if (type.IsArray) return Array.CreateInstance(type.GetElementType()!, 0);
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}



