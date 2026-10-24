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
        foreach (var type in new[] { typeof(Indicator), typeof(IndicatorCandle), typeof(IIntCandle), typeof(IInstrumentInfo), typeof(ValueDataSeries) })
        {
            Console.WriteLine(type.FullName);
            foreach (var p in type.GetProperties()) Console.WriteLine($"  {p.PropertyType} {p.Name} set={p.CanWrite}");
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Where(m => m.Name.Contains("Session") || m.Name.Contains("Clear") || m.Name.Contains("LineEnd"))) Console.WriteLine("  " + m);
        }
        return 0;
    }
}
