using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Input;
using System.Windows.Media;

namespace WolfMoss.ATAS.DrawingSync;

/// <summary>Version-gated adapter. No native runtime type is linked into the indicator's public API.</summary>
internal sealed class NativeBridge : ISyncBridge
{
    private const string CoreName = "OFT.Platform.ViewModels.Charting.ChartCore";
    private const string LineName = "OFT.Platform.DrawingObjects.HLine";
    private const string SupportedVersion = "8.0.15.304";
    private readonly object _core;
    private readonly object _data;
    private readonly object _objects;
    private readonly object _store;
    private readonly object _instrument;
    private readonly Type _lineType;
    private readonly int _thread;
    private readonly string _fingerprint;
    public InstrumentKey Instrument { get; }
    public int ChartId { get; }
    public int ThreadId => _thread;
    public object Core => _core;

    internal static object FindCore(object chart)
    {
        var found = new List<object>();
        for (var type = chart.GetType(); type is not null; type = type.BaseType)
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (field.FieldType.FullName == CoreName && field.GetValue(chart) is { } value) found.Add(value);
        return found.Count == 1 ? found[0] : throw new NotSupportedException("未找到唯一 ChartCore；停止适配。");
    }

    public static void Post(object core, Action callback) => R.Call(core, "ParentGuiAsync", callback);

    public NativeBridge(object core)
    {
        _core = core;
        _thread = Environment.CurrentManagedThreadId;
        ValidateAssembly(core.GetType().Assembly);
        _lineType = core.GetType().Assembly.GetType(LineName, throwOnError: true)!;
        _data = R.Get(core, "Data");
        _objects = R.Get(_data, "DrawingObjects");
        _store = FindStore(_objects);
        R.Method(_store.GetType(), "Get", 0);
        _instrument = R.Get(_data, "Instrument");
        ChartId = R.Value<int>(_data, "Uid");
        Instrument = ReadInstrument();
        _fingerprint = Instrument.Identity + "|" + Instrument.Month + Instrument.GroupSuffix;
        ValidateLineShape(_lineType);
        if (_lineType.GetConstructor([core.GetType()]) is null) throw new MissingMethodException("HLine(ChartCore)");
        // Resolve mutation entry points before the group can change any object.
        R.Method(_objects.GetType(), "GetAllObjects", 1);
        R.Method(_objects.GetType(), "AddGlobalDrawingObject", 3);
        R.Method(_objects.GetType(), "SaveDrawingObjects", 1);
        R.Method(_objects.GetType(), "AddCache", 1);
        R.Method(_objects.GetType(), "Delete", 1, _lineType);
        R.Method(_data.GetType(), "RaiseUpdateDrawingObjectsState", 0);
    }

    internal static void ValidateAssembly(Assembly assembly)
    {
        var version = FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion;
        if (version != SupportedVersion)
            throw new NotSupportedException($"仅适配 ATAS {SupportedVersion}，当前 {version}；已停止同步。");
    }

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
        CheckReady();
        // Wait until the drag ends; never save hundreds of intermediate drag states into native undo history.
        if (Mouse.LeftButton == MouseButtonState.Pressed) return null;
        var snapshot = Snapshot(Objects(), _lineType);
        // A legacy or de-globalized peer can live on a chart without an indicator.
        // Do not mistake an invisible local ID for a deleted object or reuse its ID.
        foreach (var obj in ((IEnumerable)R.Call(_store, "Get")!).Cast<object>())
        {
            var id = R.Value<string>(obj, "Id");
            snapshot.AllIds.Add(id);
            if (IsLocalLine(obj, _lineType)) snapshot.LocalLineIds!.Add(id);
        }
        return snapshot;
    }

    internal static bool IsLocalLine(object obj, Type lineType) => obj.GetType() == lineType &&
        !R.Value<bool>(obj, "IsGlobal") && R.GetNullable(obj, "Owner") is null && R.Value<bool>(obj, "IsCompleted");

    internal static ChartSnapshot Snapshot(object[] all, Type lineType)
    {
        if (all.Length > 2000) throw new InvalidOperationException("验证版对象数量上限为 2000。");
        var ids = new HashSet<string>();
        var lines = new Dictionary<string, LineState>();
        var localIds = new HashSet<string>();
        foreach (var obj in all)
        {
            var id = R.Value<string>(obj, "Id");
            if (!ids.Add(id)) throw new InvalidOperationException("图表存在重复对象 ID。");
            if (IsLocalLine(obj, lineType)) localIds.Add(id);
            if (obj.GetType() != lineType || !R.Value<bool>(obj, "IsGlobal") || R.GetNullable(obj, "Owner") is not null) continue;
            // A global object belongs to the shared native store, not to the viewing chart.
            if (!R.Value<bool>(obj, "IsCompleted")) continue;
            lines.Add(id, ReadLine(obj));
        }
        return new(lines, ids, localIds);
    }

    internal static LineState ReadLine(object obj)
    {
        var c = R.Value<Color>(obj, "Color");
        return new(R.Value<decimal>(obj, "TruePrice"), ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B,
            R.Value<int>(obj, "Width"), Convert.ToInt32(R.Get(obj, "Style")), R.Value<string>(obj, "Text") ?? "",
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

    public void Apply(Edit edit)
    {
        CheckReady();
        var existing = Objects().SingleOrDefault(x => R.Value<string>(x, "Id") == edit.Id);
        if (edit.Kind == EditKind.Create)
        {
            if (existing is not null || StoreIds(_store).Contains(edit.Id)) throw new InvalidOperationException("待创建 ID 已存在，停止重复写入。");
            var obj = Activator.CreateInstance(_lineType, _core)!;
            R.Set(obj, "IsUnderCursor", false);
            R.Set(obj, "IsGlobal", true);
            R.Set(obj, "Selected", false);
            R.Set(obj, "Id", edit.Id);
            R.Set(obj, "ChartId", ChartId);
            R.Set(obj, "InstrumentName", R.Get(_instrument, "UniqueClass"));
            R.Set(obj, "InstrumentUniqName", R.Get(_instrument, "UniqueName"));
            R.Call(obj, "Init", _core);
            R.Set(obj, "UseCrossAlert", false);
            R.Set(obj, "UseApproximationAlert", false);
            WriteLine(obj, edit.State!);
            R.Call(_objects, "AddGlobalDrawingObject", obj, false, false);
        }
        else
        {
            if (existing is null || existing.GetType() != _lineType || !R.Value<bool>(existing, "IsGlobal") || R.GetNullable(existing, "Owner") is not null)
                throw new InvalidOperationException("目标对象已变化，停止写入。");
            if (edit.Kind == EditKind.Delete)
                R.Method(_objects.GetType(), "Delete", 1, _lineType).Invoke(_objects, [existing]);
            else
            {
                R.Call(_objects, "AddCache", existing);
                WriteLine(existing, edit.State!);
                var array = Array.CreateInstance(_lineType, 1);
                array.SetValue(existing, 0);
                R.Call(_objects, "SaveDrawingObjects", array);
            }
        }
        R.Call(_data, "RaiseUpdateDrawingObjectsState");
        var remaining = Objects().SingleOrDefault(x => R.Value<string>(x, "Id") == edit.Id);
        if (edit.Kind == EditKind.Delete ? remaining is not null : remaining is null || !R.Value<bool>(remaining, "IsGlobal") || ReadLine(remaining) != edit.State)
            throw new InvalidOperationException("原生对象写入后校验失败；已停止同步。");
    }
}

internal static class R
{
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
    internal static MethodInfo Method(Type type, string name, int count, Type? argument = null)
    {
        var methods = type.GetMethods().Where(m => m.Name == name && m.GetParameters().Length == count);
        if (argument is not null) methods = methods.Where(m => m.GetParameters()[0].ParameterType.IsAssignableFrom(argument));
        return methods.Single();
    }
    internal static object? Call(object obj, string name, params object[] args) => Method(obj.GetType(), name, args.Length).Invoke(obj, args);
}
