using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Input;
using System.Windows.Media;

namespace WolfMoss.ATAS.DrawingSync;

/// <summary>Capability-checked adapter; unfamiliar versions warn without blocking synchronization.</summary>
internal sealed class NativeBridge : ISyncBridge
{
    private const string CoreName = "OFT.Platform.ViewModels.Charting.ChartCore";
    private const string LineName = "OFT.Platform.DrawingObjects.HLine";
    internal static string? VersionWarning(string? version) => version is "8.0.15.304" or "8.0.15.306" ? null
        : $"版本警告：ATAS {version ?? "未知"} 尚未经本插件验证，继续同步；如接口不兼容将停止";
    public string? CompatibilityWarning { get; }
    private readonly object _core;
    private readonly object _data;
    private readonly object _objects;
    private readonly object _store;
    private readonly object _instrument;
    private readonly DrawingAdapters _adapters;
    private Dictionary<string, object> _visible = [];
    private HashSet<string> _allIds = [];
    private readonly List<Edit> _writes = [];
    private readonly int _thread;
    private readonly string _fingerprint;
    public InstrumentKey Instrument { get; }
    public int ChartId { get; }
    public int ThreadId => _thread;
    public object Core => _core;
    public bool SharesInstrumentSnapshot => true;
    private static readonly ConcurrentDictionary<Type, FieldInfo> CoreFields = new();

    internal static object FindCore(object chart)
    {
        var field = CoreFields.GetOrAdd(chart.GetType(), chartType =>
        {
            var found = new List<FieldInfo>();
            for (var type = chartType; type is not null; type = type.BaseType)
                found.AddRange(type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(f => f.FieldType.FullName == CoreName));
            return found.Count == 1 ? found[0] : throw new NotSupportedException("未找到唯一 ChartCore；停止适配。");
        });
        return field.GetValue(chart) ?? throw new InvalidOperationException("ChartCore 尚未就绪。");
    }

    public static void Post(object core, Action callback) => R.Call(core, "ParentGuiAsync", callback);

    public NativeBridge(object core)
    {
        _core = core;
        _thread = Environment.CurrentManagedThreadId;
        CompatibilityWarning = GetVersionWarning(core.GetType().Assembly);
        _adapters = DrawingAdapters.For(core.GetType().Assembly);
        _data = R.Get(core, "Data");
        _objects = R.Get(_data, "DrawingObjects");
        _store = FindStore(_objects);
        R.Method(_store.GetType(), "Get", 0);
        _instrument = R.Get(_data, "Instrument");
        ChartId = R.Value<int>(_data, "Uid");
        Instrument = ReadInstrument();
        _fingerprint = Instrument.Identity + "|" + Instrument.Month + Instrument.GroupSuffix;
        foreach (var adapter in _adapters.All)
            if (adapter.NativeType.GetConstructor([core.GetType()]) is null) throw new MissingMethodException(adapter.NativeType.Name + "(ChartCore)");
        // Resolve mutation entry points before the group can change any object.
        R.Method(_objects.GetType(), "GetAllObjects", 1);
        R.Method(_objects.GetType(), "AddGlobalDrawingObject", 3);
        R.Method(_objects.GetType(), "SaveDrawingObjects", 1);
        R.Method(_objects.GetType(), "AddCache", 1);
        foreach (var adapter in _adapters.All) R.Method(_objects.GetType(), "Delete", 1, adapter.NativeType);
        R.Method(_data.GetType(), "RaiseUpdateDrawingObjectsState", 0);
    }

    internal static string? GetVersionWarning(Assembly assembly) =>
        VersionWarning(FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion);

    internal static object FindStore(object collection)
    {
        var fields = collection.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(f => f.FieldType.FullName == "OFT.Platform.DrawingObjects.IGlobalTradingObjectsStore").ToArray();
        return fields.Length == 1 && fields[0].GetValue(collection) is { } store
            ? store : throw new NotSupportedException("未找到唯一原生全局对象存储。");
    }

    internal static HashSet<string> StoreIds(object store) => ((IEnumerable)R.Call(store, "Get")!)
        .Cast<object>().Select(x => R.Value<string>(x, "Id")).ToHashSet();

    internal static void ValidateLineShape(Type type)
    {
        foreach (var name in new[] { "Id", "TruePrice", "Color", "Width", "Style", "Text", "TextSize", "TextPos", "HidePrice",
                     "ChartId", "InstrumentName", "InstrumentUniqName", "IsGlobal", "Selected", "UseCrossAlert", "UseApproximationAlert" })
        {
            var property = type.GetProperty(name) ?? throw new MissingMemberException(type.FullName, name);
            if (!property.CanRead || !property.CanWrite) throw new MissingMemberException("读写属性缺失：" + name);
        }
        foreach (var field in new[] { "IsUnderCursor", "Owner" })
            if (type.GetField(field) is null) throw new MissingFieldException(type.FullName, field);
        R.Method(type, "Init", 1);
    }

    private InstrumentKey ReadInstrument()
    {
        var listener = R.Get(R.Get(_core, "ParentViewModel"), "ReplayStateListener");
        var code = R.Value<string>(_instrument, "Name");
        var expiration = R.Value<DateTime>(_instrument, "ExpirationTime");
        var instrumentContinuous = R.Value<bool>(_instrument, "Continuous");
        var chartContinuous = R.Value<bool>(_data, "IsContinuous");
        var replay = R.Value<bool>(listener, "IsConnected");
        try
        {
            return InstrumentKey.Create(R.Value<string>(_instrument, "Class"), expiration,
                R.Value<string>(_instrument, "UniqueClass"), R.Value<string>(_instrument, "UniqueName"),
                instrumentContinuous || chartContinuous, replay, code, continuousInstrument: instrumentContinuous);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"{ex.Message} [合约={code}，到期={expiration:yyyy-MM-dd}，合约连续={instrumentContinuous}，图表连续={chartContinuous}，回放={replay}]");
        }
    }

    public void CheckReady()
    {
        if (Environment.CurrentManagedThreadId != _thread) throw new InvalidOperationException("图表线程不一致。");
        if (R.Value<bool>(_core, "IsDisposed") || R.Value<bool>(_core, "IsLoading"))
            throw new InvalidOperationException("图表已关闭或正在重新加载，请重新启用同步。");
        if (!ReferenceEquals(_instrument, R.Get(_data, "Instrument"))) throw new InvalidOperationException("图表品种已变化，请重新启用同步。");
        var current = ReadInstrument();
        if (current.Identity + "|" + current.Month + current.GroupSuffix != _fingerprint) throw new InvalidOperationException("合约身份或连续历史模式已变化，请重新启用同步。");
    }

    private object[] Objects() => ((IEnumerable)R.Call(_objects, "GetAllObjects", _instrument)!).Cast<object>().ToArray();

    public ChartSnapshot? Read()
    {
        // Wait until the drag ends; never save hundreds of intermediate drag states into native undo history.
        if (Mouse.LeftButton == MouseButtonState.Pressed) return null;
        var objects = Objects();
        var snapshot = Snapshot(objects);
        _visible = objects.ToDictionary(x => R.Value<string>(x, "Id"));
        _writes.Clear();
        // A legacy or de-globalized peer can live on a chart without an indicator.
        // Do not mistake an invisible local ID for a deleted object or reuse its ID.
        foreach (var obj in NativeReadScope.Get(_store))
        {
            var id = R.Value<string>(obj, "Id");
            snapshot.AllIds.Add(id);
            if (IsLocalObject(obj)) snapshot.LocalObjectIds!.Add(id);
        }
        _allIds = snapshot.AllIds;
        return snapshot;
    }

    private static bool Supported(object obj, Type[] types) => (types.Length == 0 || types.Contains(obj.GetType())) &&
        obj.GetType().Assembly.GetType(LineName, false) is not null &&
        DrawingAdapters.For(obj.GetType().Assembly).TryGet(obj.GetType(), out _);

    internal static bool IsLocalObject(object obj, params Type[] types) => !R.Value<bool>(obj, "IsGlobal") && Supported(obj, types) &&
        R.GetNullable(obj, "Owner") is null && R.Value<bool>(obj, "IsCompleted");

    internal static ChartSnapshot Snapshot(object[] all, params Type[] types)
    {
        if (all.Length > 2000) throw new InvalidOperationException("单侧图表对象数量上限为 2000。");
        var ids = new HashSet<string>();
        var lines = new Dictionary<string, DrawingState>();
        var localIds = new HashSet<string>();
        foreach (var obj in all)
        {
            var id = R.Value<string>(obj, "Id");
            if (!ids.Add(id)) throw new InvalidOperationException("图表存在重复对象 ID。");
            if (!Supported(obj, types) || R.GetNullable(obj, "Owner") is not null) continue;
            // A global object belongs to the shared native store, not to the viewing chart.
            if (!R.Value<bool>(obj, "IsCompleted")) continue;
            if (!R.Value<bool>(obj, "IsGlobal")) { localIds.Add(id); continue; }
            // Missing time anchors cannot be copied safely across chart periods.
            if (!DrawingAdapters.For(obj.GetType().Assembly).ForObject(obj).HasAnchors(obj)) continue;
            lines.Add(id, ReadObject(obj));
        }
        return new(lines, ids, localIds);
    }

    internal static LineState ReadLine(object obj)
    {
        var c = R.Value<Color>(obj, "Color");
        return new(R.Value<decimal>(obj, "TruePrice"), ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B,
            R.Value<int>(obj, "Width"), Convert.ToInt32(R.Get(obj, "Style")), (string?)R.GetNullable(obj, "Text") ?? "",
            R.Value<int>(obj, "TextSize"), Convert.ToInt32(R.Get(obj, "TextPos")), R.Value<bool>(obj, "HidePrice"));
    }

    internal static void WriteLine(object obj, LineState state)
    {
        R.Set(obj, "TruePrice", state.Price);
        R.Set(obj, "Color", Color.FromArgb((byte)(state.Color >> 24), (byte)(state.Color >> 16), (byte)(state.Color >> 8), (byte)state.Color));
        R.Set(obj, "Width", state.Width);
        R.Set(obj, "Style", state.Style);
        R.Set(obj, "Text", state.Text);
        R.Set(obj, "TextSize", state.TextSize);
        R.Set(obj, "TextPos", state.TextPosition);
        R.Set(obj, "HidePrice", state.HidePrice);
    }

    internal static void ValidateRectangleShape(Type type)
    {
        foreach (var name in new[] { "Id", "ChartId", "InstrumentName", "InstrumentUniqName", "IsGlobal", "Selected",
            "TrueFirstPrice", "TrueSecondPrice", "_firstTime", "_secondTime", "Color", "Width", "Style",
            "AreaColor", "AreaOpacility", "Text", "VerticalPosition", "HorizontalPosition", "ExtendLeft", "ExtendRight",
            "UseApproximationAlert" })
        {
            var property = type.GetProperty(name);
            if (property is null || !property.CanRead || !property.CanWrite) throw new MissingMemberException(type.FullName, name);
        }
        foreach (var name in new[] { "IsUnderCursor", "AutoSetSecondPos", "Owner" })
            if (type.GetField(name) is null) throw new MissingFieldException(type.FullName, name);
        var filter = type.GetProperty("MiddleLine")?.PropertyType ?? throw new MissingMemberException(type.FullName, "MiddleLine");
        var pen = filter.GetProperty("Value")?.PropertyType ?? throw new MissingMemberException(filter.FullName, "Value");
        foreach (var (owner, name, expected) in new[] { (filter, "Enabled", typeof(bool)),
            (pen, "Color", typeof(Color)), (pen, "Width", typeof(int)) })
        {
            var property = owner.GetProperty(name);
            if (property is null || !property.CanRead || !property.CanWrite || property.PropertyType != expected)
                throw new MissingMemberException(owner.FullName, name);
        }
        var style = pen.GetProperty("LineDashStyle");
        if (style is null || !style.CanRead || !style.CanWrite || !style.PropertyType.IsEnum)
            throw new MissingMemberException(pen.FullName, "LineDashStyle");
    }

    private static uint Packed(Color c) => ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
    private static Color Unpacked(uint c) => Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);

    internal static RectangleState ReadRectangle(object obj) => new(
        R.Value<DateTime>(obj, "_firstTime"), R.Value<DateTime>(obj, "_secondTime"),
        R.Value<decimal>(obj, "TrueFirstPrice"), R.Value<decimal>(obj, "TrueSecondPrice"),
        Packed(R.Value<Color>(obj, "Color")), R.Value<int>(obj, "Width"), Convert.ToInt32(R.Get(obj, "Style")),
        Packed(R.Value<Color>(obj, "AreaColor")), R.Value<int>(obj, "AreaOpacility"),
        (string?)R.GetNullable(obj, "Text") ?? "", Convert.ToInt32(R.Get(obj, "VerticalPosition")),
        Convert.ToInt32(R.Get(obj, "HorizontalPosition")), R.Value<bool>(obj, "ExtendLeft"), R.Value<bool>(obj, "ExtendRight"),
        ReadMiddleLine(obj));

    internal static MiddleLineState ReadMiddleLine(object obj)
    {
        var filter = R.Get(obj, "MiddleLine");
        var pen = R.Get(filter, "Value");
        // Copy values, never retain native mutable settings in a reconciliation snapshot.
        return new(R.Value<bool>(filter, "Enabled"), Packed(R.Value<Color>(pen, "Color")),
            R.Value<int>(pen, "Width"), Convert.ToInt32(R.Get(pen, "LineDashStyle")));
    }

    internal static void WriteMiddleLine(object obj, MiddleLineState state)
    {
        var filter = R.Get(obj, "MiddleLine");
        var pen = R.Get(filter, "Value");
        R.Set(pen, "Color", Unpacked(state.Color));
        R.Set(pen, "Width", state.Width);
        R.Set(pen, "LineDashStyle", state.Style);
        R.Set(filter, "Enabled", state.Enabled);
    }

    internal static void WriteRectangle(object obj, RectangleState state)
    {
        if (state.FirstTime == default || state.SecondTime == default)
            throw new InvalidOperationException("矩形时间锚点尚未就绪。");
        // Native RectangleObj.Draw maps these timestamps into each chart using BarsPositions.GetBar.
        // Never copy FirstPos/SecondPos, which are obsolete chart-specific bar indices.
        R.Set(obj, "_firstTime", state.FirstTime);
        R.Set(obj, "_secondTime", state.SecondTime);
        R.Set(obj, "TrueFirstPrice", state.FirstPrice);
        R.Set(obj, "TrueSecondPrice", state.SecondPrice);
        R.Set(obj, "Color", Unpacked(state.Color));
        R.Set(obj, "Width", state.Width);
        R.Set(obj, "Style", state.Style);
        R.Set(obj, "AreaColor", Unpacked(state.AreaColor));
        R.Set(obj, "AreaOpacility", state.AreaOpacity);
        R.Set(obj, "Text", state.Text);
        R.Set(obj, "VerticalPosition", state.VerticalPosition);
        R.Set(obj, "HorizontalPosition", state.HorizontalPosition);
        R.Set(obj, "ExtendLeft", state.ExtendLeft);
        R.Set(obj, "ExtendRight", state.ExtendRight);
        WriteMiddleLine(obj, state.MiddleLine);
    }

    internal static DrawingState ReadObject(object obj) => DrawingAdapters.For(obj.GetType().Assembly).ForObject(obj).Read(obj);

    internal static void WriteObject(object obj, DrawingState state)
    {
        var adapter = DrawingAdapters.For(obj.GetType().Assembly).ForObject(obj);
        if (adapter.Kind != state.Kind) throw new InvalidOperationException("配对对象类型不一致，停止写入。");
        adapter.Write(obj, state);
    }

    public void Apply(Edit edit)
    {
        var existing = _visible.GetValueOrDefault(edit.Id);
        if (edit.Kind == EditKind.Create)
        {
            if (existing is not null || _allIds.Contains(edit.Id)) throw new InvalidOperationException("待创建 ID 已存在，停止重复写入。");
            var adapter = _adapters.ForKind(edit.State!.Kind);
            var type = adapter.NativeType;
            var obj = Activator.CreateInstance(type, _core)!;
            R.Set(obj, "IsUnderCursor", false);
            adapter.Prepare(obj);
            R.Set(obj, "IsGlobal", true);
            R.Set(obj, "Selected", false);
            R.Set(obj, "Id", edit.Id);
            R.Set(obj, "ChartId", ChartId);
            R.Set(obj, "InstrumentName", R.Get(_instrument, "UniqueClass"));
            R.Set(obj, "InstrumentUniqName", R.Get(_instrument, "UniqueName"));
            R.Call(obj, "Init", _core);
            if (type.GetProperty("UseCrossAlert") is not null) R.Set(obj, "UseCrossAlert", false);
            R.Set(obj, "UseApproximationAlert", false);
            WriteObject(obj, edit.State!);
            R.Call(_objects, "AddGlobalDrawingObject", obj, false, false);
            _visible.Add(edit.Id, obj);
            _allIds.Add(edit.Id);
        }
        else
        {
            if (existing is null || !_adapters.TryGet(existing.GetType(), out _) || !R.Value<bool>(existing, "IsGlobal") || R.GetNullable(existing, "Owner") is not null)
                throw new InvalidOperationException("目标对象已变化，停止写入。");
            if (edit.Kind == EditKind.Delete)
            {
                R.Method(_objects.GetType(), "Delete", 1, existing.GetType()).Invoke(_objects, [existing]);
                _visible.Remove(edit.Id);
                _allIds.Remove(edit.Id);
            }
            else
            {
                R.Call(_objects, "AddCache", existing);
                WriteObject(existing, edit.State!);
                var array = Array.CreateInstance(existing.GetType(), 1);
                array.SetValue(existing, 0);
                R.Call(_objects, "SaveDrawingObjects", array);
            }
        }
        _writes.Add(edit);
    }

    public void VerifyWrites()
    {
        if (_writes.Count == 0) return;
        var objects = Objects().ToDictionary(x => R.Value<string>(x, "Id"));
        foreach (var edit in _writes)
        {
            var remaining = objects.GetValueOrDefault(edit.Id);
            if (edit.Kind == EditKind.Delete ? remaining is not null : remaining is null || !R.Value<bool>(remaining, "IsGlobal") || ReadObject(remaining) != edit.State)
                throw new InvalidOperationException("原生对象写入后校验失败；已停止同步。");
        }
        R.Call(_data, "RaiseUpdateDrawingObjectsState");
        _writes.Clear();
    }
}

internal static class R
{
    private static readonly ConcurrentDictionary<(Type, string, int, Type?), MethodInfo> Methods = new();
    private static readonly ConcurrentDictionary<(Type, string), MemberInfo> Members = new();
    private static MemberInfo Member(Type type, string name) => Members.GetOrAdd((type, name), key =>
        (MemberInfo?)key.Item1.GetProperty(key.Item2) ?? key.Item1.GetField(key.Item2)
        ?? throw new MissingMemberException(key.Item1.FullName, key.Item2));
    internal static object? GetNullable(object obj, string name) => Member(obj.GetType(), name) switch
    {
        PropertyInfo p => p.GetValue(obj), FieldInfo f => f.GetValue(obj), _ => throw new InvalidOperationException()
    };
    internal static object Get(object obj, string name) => GetNullable(obj, name) ?? throw new InvalidOperationException(name + " 尚未就绪。");
    internal static T Value<T>(object obj, string name) => (T)Get(obj, name);
    internal static void Set(object obj, string name, object value)
    {
        var member = Member(obj.GetType(), name);
        var type = member is PropertyInfo p ? p.PropertyType : ((FieldInfo)member).FieldType;
        if (type.IsEnum && value.GetType() != type) value = Enum.ToObject(type, value);
        if (member is PropertyInfo property) property.SetValue(obj, value); else ((FieldInfo)member).SetValue(obj, value);
    }
    internal static MethodInfo Method(Type type, string name, int count, Type? argument = null) => Methods.GetOrAdd((type, name, count, argument), key =>
    {
        var methods = key.Item1.GetMethods().Where(m => m.Name == key.Item2 && m.GetParameters().Length == key.Item3);
        if (key.Item4 is not null) methods = methods.Where(m => m.GetParameters()[0].ParameterType.IsAssignableFrom(key.Item4));
        return methods.Single();
    });
    internal static object? Call(object obj, string name, params object[] args) => Method(obj.GetType(), name, args.Length).Invoke(obj, args);
}

// Lifetime is one GUI reconciliation; never reuse native references across timer ticks.
internal sealed class NativeReadScope : IDisposable
{
    [ThreadStatic] private static NativeReadScope? _current;
    private readonly NativeReadScope? _previous = _current;
    private readonly Dictionary<object, object[]> _stores = new(ReferenceEqualityComparer.Instance);
    public NativeReadScope() => _current = this;
    internal static object[] Get(object store)
    {
        if (_current is null) return Read(store);
        if (!_current._stores.TryGetValue(store, out var objects)) _current._stores.Add(store, objects = Read(store));
        return objects;
    }
    private static object[] Read(object store) => ((IEnumerable)R.Call(store, "Get")!).Cast<object>().ToArray();
    public void Dispose() { _stores.Clear(); _current = _previous; }
}

