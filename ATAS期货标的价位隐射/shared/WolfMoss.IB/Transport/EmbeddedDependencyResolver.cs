using System.Reflection;
using System.IO;
using System.Runtime.Loader;

namespace WolfMoss.ATAS.PriceMapping;

internal static class EmbeddedDependencyResolver
{
    private static readonly object Sync = new();
    private static Assembly? _ibApiAssembly;

    internal static Assembly? TryLoadIbApiAssembly()
    {
        var loaded = Volatile.Read(ref _ibApiAssembly);
        if (loaded != null) return loaded;
        lock (Sync)
        {
            if (_ibApiAssembly != null) return _ibApiAssembly;
            var owner = typeof(EmbeddedDependencyResolver).Assembly;
            if (!owner.GetManifestResourceNames().Contains("EmbeddedDependencies.CSharpAPI.dll")) return null;
            return _ibApiAssembly = new PrivateIbContext(owner).LoadFromAssemblyName(new AssemblyName("CSharpAPI"));
        }
    }

    // One private context per consuming assembly. Never reuse a namesake from another
    // plugin, and never register a process-wide AssemblyResolve handler.
    private sealed class PrivateIbContext(Assembly owner)
        : AssemblyLoadContext($"WolfMoss.IB:{owner.GetName().Name}:{Guid.NewGuid():N}", isCollectible: false)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            var resource = name.Name switch
            {
                "CSharpAPI" => "EmbeddedDependencies.CSharpAPI.dll",
                "Google.Protobuf" => "EmbeddedDependencies.Google.Protobuf.dll",
                _ => null
            };
            if (resource == null) return null; // Framework types share the runtime.
            using var stream = owner.GetManifestResourceStream(resource)
                ?? throw new FileNotFoundException("Missing private IB dependency", resource);
            return LoadFromStream(stream);
        }
    }
}
