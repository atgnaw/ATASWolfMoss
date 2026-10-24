using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using ATAS.Indicators;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var directory = Environment.GetEnvironmentVariable("ATAS_INSTALL_DIR") ?? @"C:\Program Files (x86)\ATAS Platform";
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        try { return Run(args); }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string[] args)
    {
        if (!args.Contains("--describe"))
        {
            CoreTests.Run();
            HostTests.Run();
            Console.WriteLine($"RESULT {CoreTests.Count} checks passed using ATAS {typeof(Indicator).Assembly.GetName().Version}");
            return 0;
        }
        foreach (var type in new[] { typeof(IOnlineDataProvider), typeof(Indicator), typeof(IndicatorCandle), typeof(ICandleCreator), typeof(CumulativeTradesRequest), typeof(CumulativeTrade), typeof(MarketDataArg), typeof(PriceVolumeInfo), typeof(IChart) })
        {
            Console.WriteLine(type.FullName); foreach(var e in type.GetEvents()) Console.WriteLine(" EVENT " + e + " INVOKE " + e.EventHandlerType!.GetMethod("Invoke")); foreach (var f in type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance)) Console.WriteLine(" FIELD " + f);
            foreach (var c in type.GetConstructors()) Console.WriteLine("  " + c);
            foreach (var p in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) Console.WriteLine($"  {p.PropertyType} {p.Name}");
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Where(m => !m.IsSpecialName && (m.Name.Contains("Trade") || m.Name.Contains("Time") || m.Name.Contains("Candle") || m.Name.Contains("Bar")))) Console.WriteLine("  " + m);
        }
        return 0;
    }
}


