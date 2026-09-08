// Source-shared module: compiled privately into each consuming plugin.
namespace WolfMoss.ATAS.PriceMapping;

using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;

/// <summary>
/// Public, non-sealed proxy required by <see cref="DispatchProxy"/> when the
/// official IB API EWrapper interface is supplied at runtime.
/// </summary>
public class IbCallbackDispatchProxy : DispatchProxy
{
    private static readonly object ProxySync = new();
    private static readonly Dictionary<Assembly, Type> ProxyBases = new();

    internal static object Create(Assembly api, Action<MethodInfo, object?[]> handler)
    {
        Type proxyBase;
        lock (ProxySync)
        {
            if (!ProxyBases.TryGetValue(api, out proxyBase!))
            {
                // DispatchProxy groups generated proxy assemblies by the BASE type's
                // ALC, not the interface's. A private SDK alone cannot isolate EWrapper.
                // Put a trivial base bridge in the SDK context so its generated proxies
                // cannot bind to another plugin's same-name CSharpAPI reference.
                using var scope = AssemblyLoadContext.GetLoadContext(api)!.EnterContextualReflection();
                var dynamicAssembly = AssemblyBuilder.DefineDynamicAssembly(
                    new AssemblyName("WolfMoss.IB.CallbackBridge." + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
                var builder = dynamicAssembly.DefineDynamicModule("Callbacks").DefineType(
                    "PrivateIbCallbackBridge", TypeAttributes.Public, typeof(IbCallbackDispatchProxy));
                builder.DefineDefaultConstructor(MethodAttributes.Public);
                proxyBase = builder.CreateType()!;
                ProxyBases.Add(api, proxyBase);
            }
        }
        using var generationScope = AssemblyLoadContext.GetLoadContext(api)!.EnterContextualReflection();
        var proxy = DispatchProxy.Create(api.GetType("IBApi.EWrapper", true)!, proxyBase);
        ((IbCallbackDispatchProxy)proxy).Handler = handler;
        return proxy;
    }

    public Action<MethodInfo, object?[]>? Handler { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod != null)
            Handler?.Invoke(targetMethod, args ?? Array.Empty<object?>());

        if (targetMethod?.ReturnType == typeof(void) || targetMethod == null)
            return null;

        return targetMethod.ReturnType.IsValueType
            ? Activator.CreateInstance(targetMethod.ReturnType)
            : null;
    }
}
