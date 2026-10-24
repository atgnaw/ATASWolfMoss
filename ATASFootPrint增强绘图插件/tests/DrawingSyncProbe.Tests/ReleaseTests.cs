using System.Collections;
using System.Diagnostics;
using System.Reflection;
using WolfMoss.ATAS.DrawingSync;

internal static partial class Program
{
    private static readonly RayState Ray = new(Rect.FirstTime, A);
    private static readonly TrendState Trend = new(Rect.FirstTime, Rect.SecondTime, Rect.FirstPrice, Rect.SecondPrice,
        0xFF88CC44, 3, 1, "趋势", 0x66443322, new(14, "Arial", true, false, true, false), true, false, 0);

    private static void ReleaseEngineTests()
    {
        var w = new World();
        DrawingState[] states = [A, Rect, Ray, Trend];
        foreach (var state in states) w.L.Add(state.Kind.ToString(), state);
        w.Step();
        Check(w.Engine.PairCount == 4 && w.Saved.Select(x => x.Kind).Distinct().Count() == 4, "four native drawing kinds coexist in one group");
        foreach (var state in new DrawingState[] { Ray, Trend })
        {
            var pair = w.Saved.Single(x => x.Kind == state.Kind);
            DrawingState changed = state is RayState ray ? ray with { StartTime = ray.StartTime.AddMinutes(-30), Line = B }
                : Trend with { FirstTime = Trend.SecondTime, SecondTime = Trend.FirstTime, FirstPrice = 22050,
                    RenderType = 1, EndArrow = true, Font = Trend.Font with { Bold = false, Italic = true } };
            w.R[pair.Right] = changed; w.Step();
            Check(w.L[pair.Left] == changed, state.Kind + " reverse geometry style and ray-mode changes propagate");
            w.Step(); Check(w.Edits.Count == 0, state.Kind + " converges without feedback");
            w.R.Remove(pair.Right); w.Engine = w.New(); w.Step();
            Check(w.R[pair.Right] == changed, state.Kind + " reconnect restores missing peer with exact anchors and kind");
            w.R.Remove(pair.Right); w.Step();
            Check(!w.L.ContainsKey(pair.Left), state.Kind + " observed micro deletion removes mini peer");
            w.L[pair.Left] = changed; w.Step();
            Check(w.R[pair.Right] == changed, state.Kind + " undo restores peer with existing ID");
            w.L.Remove(pair.Left); w.LocalL.Add(pair.Left); w.Step();
            Check(!w.R.ContainsKey(pair.Right) && !w.Saved.Contains(pair), state.Kind + " Global off preserves local and forgets mirror");
        }
        Check(w.Engine.PairCount == 2 && w.L.Values.Contains(A) && w.L.Values.Contains(Rect), "ray and trend unlink leave horizontal and rectangle pairs intact");
        Throws(() => new SyncEngine([new("a", "b", true, (DrawingKind)99)], _ => { }), "unknown persisted drawing kind rejected");

        foreach (var count in new[] { 100, 1000, 2000 })
        {
            Binding[] saved = [];
            var writes = 0; var applications = 0;
            var source = Enumerable.Range(0, count).ToDictionary(x => "s" + x, x => states[x % states.Length]);
            var target = new Dictionary<string, DrawingState>();
            var engine = new SyncEngine([], b => { saved = b.ToArray(); writes++; });
            void Apply(Edit edit)
            {
                if (!saved.Any(b => b.Right == edit.Id)) throw new Exception("native create before journal");
                applications++; target.Add(edit.Id, edit.State!);
            }
            engine.Step(new(source, source.Keys.ToHashSet()), new(target, []), Apply);
            Check(writes == 1 && applications == count && target.Count == count, $"{count} mixed creations use one write-ahead journal commit");
            var left = new ChartSnapshot(source, source.Keys.ToHashSet());
            var right = new ChartSnapshot(target, target.Keys.ToHashSet());
            for (var i = 0; i < 5; i++) engine.Step(left, right, _ => throw new Exception("unchanged write"));
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < 100; i++) engine.Step(left, right, _ => throw new Exception("unchanged write"));
            watch.Stop();
            Check(writes == 1 && engine.PairCount == count, $"{count} stable mixed pairs generate zero native writes and zero extra journal commits");
            Console.WriteLine($"BENCH engine-only stable {count} pairs: {watch.Elapsed.TotalMilliseconds / 100:F3} ms/cycle (100 cycles; excludes native GUI and disk).");
        }

        Binding[] persisted = [new("a", "b", true)];
        var removed = false;
        var unlink = new SyncEngine(persisted, b => persisted = b.ToArray());
        Throws(() => unlink.Step(new([], ["a"], ["a"]), new(new() { ["b"] = A }, ["b"]),
            _ => removed = true, () => throw new Exception("native readback failed")), "native verification failure aborts unlink commit");
        Check(removed && persisted.Length == 1, "unlink mapping survives until batched native readback succeeds");
        var retry = new SyncEngine(persisted, b => persisted = b.ToArray());
        retry.Step(new([], ["a"], ["a"]), new([], []), _ => throw new Exception("unexpected write"));
        Check(persisted.Length == 0, "crash after unlink delete can clean journal without resurrecting global mirror");

        persisted = [new("old", "mirror", true)];
        var commits = new List<Binding[]>();
        var nativeEdits = new List<Edit>();
        var verified = false;
        var mixed = new SyncEngine(persisted, b => { persisted = b.ToArray(); commits.Add(persisted); });
        mixed.Step(new(new() { ["new"] = Trend }, ["new", "old"], ["old"]), new(new() { ["mirror"] = A }, ["mirror"]), edit =>
        {
            if (persisted.Length != 2) throw new Exception("mixed batch lost write-ahead mappings");
            nativeEdits.Add(edit);
        }, () => { verified = persisted.Any(b => b.Left == "old"); });
        Check(commits.Count == 2 && commits[0].Length == 2 && persisted.Single().Left == "new" && verified && nativeEdits.Count == 2,
            "mixed unlink and creation batch uses two commits and retains removed mapping through native verification");
        var overCapacity = Enumerable.Range(0, 2001).ToDictionary(x => "limit" + x, _ => (DrawingState)A);
        var persistenceAttempts = 0; var nativeAttempts = 0;
        Throws(() => new SyncEngine([], _ => persistenceAttempts++)
            .Step(new(overCapacity, overCapacity.Keys.ToHashSet()), new([], []), _ => nativeAttempts++),
            "oversized creation batch is rejected during planning");
        Check(persistenceAttempts == 0 && nativeAttempts == 0, "capacity rejection leaves journal and native drawings unchanged");
    }

    private static void SchedulerTests()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        long now = 10000;
        var hub = new SyncHub(directory, 300, () => now);
        var mini = InstrumentKey.Create("ES", default, "ES.CME", "ES", true, false, "ES", true);
        var micro = InstrumentKey.Create("MES", default, "MES.CME", "MES", true, false, "MES", true);
        var l = new Dictionary<string, DrawingState>();
        var r = new Dictionary<string, DrawingState>();
        var endpoints = new List<SyncHub.Endpoint>();
        var redraws = 0;
        try
        {
            for (var i = 0; i < 20; i++)
            {
                var bridge = new FakeBridge(i % 2 == 0 ? mini : micro, i % 2 == 0 ? l : r) { SharesInstrumentSnapshot = true };
                var e = new SyncHub.Endpoint(Guid.NewGuid(), bridge, "perf", () => true, _ => { }, () => redraws++);
                endpoints.Add(e); hub.Tick(e);
            }
            foreach (var e in endpoints) ((FakeBridge)e.Bridge).Reads = 0;
            l["trend"] = Trend;
            now += 299;
            for (var i = 0; i < 10; i++) foreach (var e in endpoints) hub.Tick(e);
            Check(endpoints.Sum(e => ((FakeBridge)e.Bridge).Reads) == 0 && r.Count == 0, "20 chart heartbeats do not rescan objects inside group interval");
            now++;
            foreach (var e in endpoints) hub.Tick(e);
            Check(endpoints.Sum(e => ((FakeBridge)e.Bridge).Reads) == 2 && r.Values.Single() == Trend,
                "20 charts perform exactly two representative snapshots per due group cycle");
            Check(endpoints.Sum(e => ((FakeBridge)e.Bridge).Writes) == 1 && redraws == 20,
                "one trend creates one global mirror and redraws each chart once");
            now += 300;
            foreach (var e in endpoints) hub.Tick(e);
            Check(endpoints.Sum(e => ((FakeBridge)e.Bridge).Reads) == 4 && redraws == 20,
                "unchanged due cycle reads once per side without redrawing charts");
            var first = endpoints[0]; hub.Remove(first.Id); endpoints.RemoveAt(0);
            r[r.Keys.Single()] = Trend with { RenderType = 2 };
            hub.Tick(endpoints[0]);
            Check(l["trend"] == r.Values.Single() && r.Count == 1, "membership change bypasses timer gate and elects new representative without duplication");
            var loading = (FakeBridge)endpoints[0].Bridge;
            loading.Ready = false;
            now += 300; hub.Tick(endpoints[0]);
            Check(l.Count == 1 && r.Count == 1, "readiness gap never propagates deletion");
            loading.Ready = true;
            r.Clear(); hub.Tick(endpoints[0]);
            Check(r.Count == 1 && l["trend"] == r.Values.Single(), "readiness recovery resets baseline and repairs missing peer automatically");
        }
        finally
        {
            foreach (var e in endpoints) hub.Remove(e.Id);
            foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    private static void NativeReleaseTests(Assembly assembly, object store, Type collection)
    {
        var adapters = DrawingAdapters.For(assembly);
        var core = assembly.GetType("OFT.Platform.ViewModels.Charting.ChartCore", true)!;
        foreach (var state in new DrawingState[] { Ray, Trend })
        {
            var adapter = adapters.ForKind(state.Kind);
            Check(adapter.NativeType.GetConstructor([core]) is not null, state.Kind + " actual native ChartCore constructor and adapter metadata validated");
            object Make(string id, string root, int chartId, DrawingState payload)
            {
                var obj = Activator.CreateInstance(adapter.NativeType)!;
                R.Set(obj, "IsUnderCursor", false); adapter.Prepare(obj);
                R.Set(obj, "IsGlobal", true); R.Set(obj, "Id", id);
                R.Set(obj, "InstrumentName", root); R.Set(obj, "InstrumentUniqName", root + "Z6-test");
                NativeBridge.WriteObject(obj, payload);
                R.Call(Activator.CreateInstance(collection, store, chartId)!, "AddGlobalDrawingObject", obj, false, false);
                return obj;
            }
            object[] Read(string root, int chart) => ((IEnumerable)R.Call(store, "Get", root + "Z6-test", root, chart)!).Cast<object>().ToArray();
            void Delete(object obj, int chart) => R.Method(collection, "Delete", 1, obj.GetType())
                .Invoke(Activator.CreateInstance(collection, store, chart), [obj]);
            var original = Make("release-" + state.Kind, "NQ", 100, state);
            Check(NativeBridge.ReadObject(original) == state && R.Value<bool>(original, "IsCompleted"), state.Kind + " native properties round-trip as completed drawing");
            R.Set(original, "_firstTime", default(DateTime));
            var incomplete = NativeBridge.Snapshot(Read("NQ", 100));
            Check(incomplete.Objects.Count == 0 && incomplete.AllIds.Count == 1, state.Kind + " missing time anchor suspends without treating ID as deletion");
            NativeBridge.WriteObject(original, state);
            R.Set(original, "IsGlobal", false);
            Check(NativeBridge.Snapshot(Read("NQ", 100)).Objects.Count == 0 && NativeBridge.IsLocalObject(original), state.Kind + " local drawing ignored and eligible for unlink");
            R.Set(original, "IsGlobal", true);
            Throws(() => NativeBridge.WriteObject(original, Rect), state.Kind + " rejects mismatched payload before write");
            var engine = new SyncEngine([], _ => { });
            var writes = 0;
            void Apply(Edit edit)
            {
                writes++;
                var root = edit.ToLeft ? "NQ" : "MNQ";
                var chart = edit.ToLeft ? 100 : 200;
                if (edit.Kind == EditKind.Create) { Make(edit.Id, root, chart, edit.State!); return; }
                var obj = Read(root, chart).Single(x => R.Value<string>(x, "Id") == edit.Id);
                if (edit.Kind == EditKind.Delete) Delete(obj, chart);
                else
                {
                    var coll = Activator.CreateInstance(collection, store, chart)!;
                    R.Call(coll, "AddCache", obj);
                    NativeBridge.WriteObject(obj, edit.State!);
                    var array = Array.CreateInstance(adapter.NativeType, 1); array.SetValue(obj, 0);
                    R.Call(coll, "SaveDrawingObjects", array);
                }
            }
            void Step() => engine.Step(NativeBridge.Snapshot(Read("NQ", 100)), NativeBridge.Snapshot(Read("MNQ", 200)), Apply);
            Step(); var mirror = Read("MNQ", 200).Single();
            Check(NativeBridge.ReadObject(mirror) == state && Read("MNQ", 201).Single() == mirror,
                state.Kind + " native store creates one shared counterpart across chart periods");
            Step(); Check(writes == 1, state.Kind + " native snapshot convergence produces no repeated save");
            if (state is TrendState)
            {
                Check(!ReferenceEquals(R.Get(original, "TextStyle"), R.Get(mirror, "TextStyle")), "trend mirrors never share mutable native font settings");
                foreach (var mode in new[] { 1, 2, 0 })
                {
                    var update = Trend with { RenderType = mode, FirstTime = Trend.SecondTime, SecondTime = Trend.FirstTime,
                        Font = Trend.Font with { Italic = true, Strikeout = true, Size = 18 }, EndArrow = true };
                    NativeBridge.WriteObject(mirror, update); Step();
                    Check(NativeBridge.ReadObject(original) == update, $"native trend mode {mode} syncs reverse-directed time anchors arrows and full font");
                }
            }
            else
            {
                var update = Ray with { StartTime = Ray.StartTime.AddHours(-1), Line = B };
                NativeBridge.WriteObject(mirror, update); Step();
                Check(NativeBridge.ReadObject(original) == update, "native horizontal ray anchor price labels and style synchronize back");
            }
            R.Set(mirror, "IsGlobal", false); R.Set(mirror, "ChartId", 200); Step();
            Check(Read("NQ", 100).Length == 0 && Read("MNQ", 201).Length == 0 && Read("MNQ", 200).Single() == mirror,
                state.Kind + " native Global off keeps local drawing and removes all mirrored views");
            Delete(mirror, 200);
        }
        var trendType = adapters.ForKind(DrawingKind.TrendLine).NativeType;
        var unfinished = Activator.CreateInstance(trendType)!;
        R.Set(unfinished, "IsUnderCursor", false); R.Set(unfinished, "IsGlobal", true); R.Set(unfinished, "Id", "unfinished-trend");
        NativeBridge.WriteObject(unfinished, Trend);
        var twoCoordinate = trendType.BaseType!.BaseType!;
        twoCoordinate.GetField("_autoSetSecondPos", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(unfinished, true);
        Check(NativeBridge.Snapshot([unfinished]).Objects.Count == 0 && !NativeBridge.IsLocalObject(unfinished),
            "native trend awaiting second point cannot synchronize or unlink peer");
        Check(adapters.All.Count() == 4, "adapter registry exposes exactly four supported native types");
    }
}
