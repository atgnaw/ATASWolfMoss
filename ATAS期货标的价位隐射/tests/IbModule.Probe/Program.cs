using System.Reflection;
using System.Runtime.Loader;
using WolfMoss.ATAS.PriceMapping;

// Offline standalone consumer: no eConnect and no account/order/historical request.
var api = ModuleProbe.Api();
var context = AssemblyLoadContext.GetLoadContext(api)!;
Console.WriteLine($"OFFLINE PASS: {typeof(ModuleProbe).Assembly.GetName().Name}; API {api.GetName().Version}; private={context != AssemblyLoadContext.Default}");

public static class ModuleProbe
{
    public static Assembly Api() => EmbeddedDependencyResolver.TryLoadIbApiAssembly()
        ?? throw new InvalidOperationException("Missing embedded IB API");
    public static IDisposable Reserve(string host, int port, int id) => IbSessionReservation.Reserve(host, port, id);
    public static int FirstRequestId() => new IbRequestIdSequence().Next();
    public static object CreateUnconnectedSocket()
        => IbSocketRuntime.Create(Api(), static (_, _) => { }).Client;
    public static object CreateCallbackProxy(Action callback)
    {
        return IbCallbackDispatchProxy.Create(Api(), (_, _) => callback());
    }
#if IB_OPTIONS_PROBE
    public static object AcquireOptions(string host, int port, int id)
        => IbOptionGatewayPool.Acquire(new(host, port, id));
#endif
}
