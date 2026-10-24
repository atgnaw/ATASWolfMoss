using System.Collections.Concurrent;
using System.Reflection;
using System.Windows.Media;

namespace WolfMoss.ATAS.DrawingSync;

/// <summary>One exact native type per adapter. State consists only of immutable values.</summary>
internal abstract class DrawingAdapter(Type nativeType, DrawingKind kind)
{
    public Type NativeType { get; } = nativeType;
    public DrawingKind Kind { get; } = kind;
    public virtual bool HasAnchors(object obj) => true;
    public abstract DrawingState Read(object obj);
    public abstract void Write(object obj, DrawingState state);
    public virtual void Prepare(object obj) { }
    public virtual void Validate()
    {
        Require(NativeType, "Id", "ChartId", "InstrumentName", "InstrumentUniqName", "IsGlobal", "Selected",
            "Color", "Width", "Style", "Text", "UseApproximationAlert");
        foreach (var name in new[] { "IsUnderCursor", "Owner" })
            if (NativeType.GetField(name) is null) throw new MissingFieldException(NativeType.FullName, name);
        R.Method(NativeType, "Init", 1);
    }
    protected static void Require(Type type, params string[] names)
    {
        foreach (var name in names)
        {
            var p = type.GetProperty(name);
            if (p is null || !p.CanRead || !p.CanWrite) throw new MissingMemberException(type.FullName, name);
        }
    }
    protected static uint Pack(Color c) => ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
    protected static Color Unpack(uint c) => Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);
}

internal sealed class HorizontalLineAdapter(Type type) : DrawingAdapter(type, DrawingKind.HorizontalLine)
{
    public override void Validate() => NativeBridge.ValidateLineShape(NativeType);
    public override DrawingState Read(object obj) => NativeBridge.ReadLine(obj);
    public override void Write(object obj, DrawingState state) => NativeBridge.WriteLine(obj, (LineState)state);
}

internal sealed class RectangleAdapter(Type type) : DrawingAdapter(type, DrawingKind.Rectangle)
{
    public override void Validate() => NativeBridge.ValidateRectangleShape(NativeType);
    public override bool HasAnchors(object obj) => R.Value<DateTime>(obj, "_firstTime") != default && R.Value<DateTime>(obj, "_secondTime") != default;
    public override DrawingState Read(object obj) => NativeBridge.ReadRectangle(obj);
    public override void Write(object obj, DrawingState state) => NativeBridge.WriteRectangle(obj, (RectangleState)state);
    public override void Prepare(object obj) => R.Set(obj, "AutoSetSecondPos", false);
}

internal sealed class HorizontalRayAdapter(Type type) : DrawingAdapter(type, DrawingKind.HorizontalRay)
{
    public override void Validate() { NativeBridge.ValidateLineShape(NativeType); Require(NativeType, "_firstTime"); }
    // OneCoordinateDrawingObject clamps an unset date to UnixEpoch.
    public override bool HasAnchors(object obj) => R.Value<DateTime>(obj, "_firstTime") > DateTime.UnixEpoch;
    public override DrawingState Read(object obj) => new RayState(R.Value<DateTime>(obj, "_firstTime"), NativeBridge.ReadLine(obj));
    public override void Write(object obj, DrawingState state)
    {
        var ray = (RayState)state;
        if (ray.StartTime <= DateTime.UnixEpoch) throw new InvalidOperationException("射线时间锚点尚未就绪。");
        R.Set(obj, "_firstTime", ray.StartTime);
        NativeBridge.WriteLine(obj, ray.Line);
    }
}

internal sealed class TrendLineAdapter(Type type) : DrawingAdapter(type, DrawingKind.TrendLine)
{
    public override void Validate()
    {
        base.Validate();
        Require(NativeType, "_firstTime", "_secondTime", "TrueFirstPrice", "TrueSecondPrice", "TextBg",
            "StartLineAsArrow", "EndLineAsArrow", "LineRenderType", "UseCrossAlert");
        var font = NativeType.GetProperty("TextStyle")?.PropertyType ?? throw new MissingMemberException("TextStyle");
        Require(font, "Size", "FontFamily", "Bold", "Italic", "Underline", "Strikeout");
    }
    public override bool HasAnchors(object obj) => R.Value<DateTime>(obj, "_firstTime") != default && R.Value<DateTime>(obj, "_secondTime") != default;
    public override DrawingState Read(object obj)
    {
        var font = R.Get(obj, "TextStyle");
        return new TrendState(R.Value<DateTime>(obj, "_firstTime"), R.Value<DateTime>(obj, "_secondTime"),
            R.Value<decimal>(obj, "TrueFirstPrice"), R.Value<decimal>(obj, "TrueSecondPrice"),
            Pack(R.Value<Color>(obj, "Color")), R.Value<int>(obj, "Width"), Convert.ToInt32(R.Get(obj, "Style")),
            (string?)R.GetNullable(obj, "Text") ?? "", Pack(R.Value<Color>(obj, "TextBg")),
            new(R.Value<int>(font, "Size"), R.Value<string>(font, "FontFamily"), R.Value<bool>(font, "Bold"),
                R.Value<bool>(font, "Italic"), R.Value<bool>(font, "Underline"), R.Value<bool>(font, "Strikeout")),
            R.Value<bool>(obj, "StartLineAsArrow"), R.Value<bool>(obj, "EndLineAsArrow"), Convert.ToInt32(R.Get(obj, "LineRenderType")));
    }
    public override void Write(object obj, DrawingState state)
    {
        var trend = (TrendState)state;
        if (trend.FirstTime == default || trend.SecondTime == default || trend.RenderType is < 0 or > 2)
            throw new InvalidOperationException("趋势线锚点或延长模式无效。");
        R.Set(obj, "_firstTime", trend.FirstTime);
        R.Set(obj, "_secondTime", trend.SecondTime);
        R.Set(obj, "TrueFirstPrice", trend.FirstPrice);
        R.Set(obj, "TrueSecondPrice", trend.SecondPrice);
        R.Set(obj, "Color", Unpack(trend.Color));
        R.Set(obj, "Width", trend.Width);
        R.Set(obj, "Style", trend.Style);
        R.Set(obj, "Text", trend.Text);
        R.Set(obj, "TextBg", Unpack(trend.TextBackground));
        var font = R.Get(obj, "TextStyle");
        R.Set(font, "Size", trend.Font.Size);
        R.Set(font, "FontFamily", trend.Font.Family);
        R.Set(font, "Bold", trend.Font.Bold);
        R.Set(font, "Italic", trend.Font.Italic);
        R.Set(font, "Underline", trend.Font.Underline);
        R.Set(font, "Strikeout", trend.Font.Strikeout);
        R.Set(obj, "StartLineAsArrow", trend.StartArrow);
        R.Set(obj, "EndLineAsArrow", trend.EndArrow);
        R.Set(obj, "LineRenderType", trend.RenderType);
    }
}

internal sealed class DrawingAdapters
{
    private static readonly ConcurrentDictionary<Assembly, DrawingAdapters> Cache = new();
    private readonly Dictionary<Type, DrawingAdapter> _types;
    private readonly Dictionary<DrawingKind, DrawingAdapter> _kinds;
    public static DrawingAdapters For(Assembly assembly) => Cache.GetOrAdd(assembly, a => new(a));
    private DrawingAdapters(Assembly assembly)
    {
        Type Type(string name) => assembly.GetType("OFT.Platform.DrawingObjects." + name, true)!;
        DrawingAdapter[] adapters = [new HorizontalLineAdapter(Type("HLine")), new RectangleAdapter(Type("RectangleObj")),
            new HorizontalRayAdapter(Type("HorizontalRay")), new TrendLineAdapter(Type("TrendLine"))];
        foreach (var adapter in adapters) adapter.Validate();
        _types = adapters.ToDictionary(x => x.NativeType);
        _kinds = adapters.ToDictionary(x => x.Kind);
    }
    public IEnumerable<DrawingAdapter> All => _types.Values;
    public bool TryGet(Type type, out DrawingAdapter adapter) => _types.TryGetValue(type, out adapter!);
    public DrawingAdapter ForKind(DrawingKind kind) => _kinds.TryGetValue(kind, out var adapter) ? adapter : throw new NotSupportedException("不支持的绘图类型。");
    public DrawingAdapter ForObject(object obj) => TryGet(obj.GetType(), out var adapter) ? adapter : throw new NotSupportedException("不支持的绘图对象。");
}
