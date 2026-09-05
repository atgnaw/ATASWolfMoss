using System.Reflection;
using System.Runtime.Loader;

namespace WolfMoss.ATAS.PriceMapping;

internal static class EmbeddedDependencyResolver
{
    private const string IbApiAssemblyName = "CSharpAPI";
    private const string IbApiResourceName = "EmbeddedDependencies.CSharpAPI.dll";
    private const string ProtobufAssemblyName = "Google.Protobuf";
    private const string ProtobufResourceName = "EmbeddedDependencies.Google.Protobuf.dll";
    private static readonly object Sync = new();
    private static Assembly? _ibApiAssembly;

    internal static Assembly? TryLoadIbApiAssembly()
    {
        var loaded = Volatile.Read(ref _ibApiAssembly);
        if (loaded is not null)
            return loaded;

        lock (Sync)
        {
            loaded = _ibApiAssembly ?? FindLoadedAssembly(IbApiAssemblyName);
            if (loaded is not null)
                return _ibApiAssembly = loaded;

            _ = FindLoadedAssembly(ProtobufAssemblyName)
                ?? LoadEmbeddedAssembly(ProtobufResourceName);
            loaded = LoadEmbeddedAssembly(IbApiResourceName);
            return _ibApiAssembly = loaded;
        }
    }

    private static Assembly? LoadEmbeddedAssembly(string resourceName)
    {
        using var stream = typeof(EmbeddedDependencyResolver).Assembly
            .GetManifestResourceStream(resourceName);
        return stream is null
            ? null
            : AssemblyLoadContext.Default.LoadFromStream(stream);
    }

    private static Assembly? FindLoadedAssembly(string assemblyName)
    {
        return AppDomain.CurrentDomain
            .GetAssemblies()
            .FirstOrDefault(assembly => string.Equals(
                assembly.GetName().Name,
                assemblyName,
                StringComparison.OrdinalIgnoreCase));
    }
}
