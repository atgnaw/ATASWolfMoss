using System.Reflection;

namespace WolfMoss.ATAS.PriceMapping;

// Reusable SDK boundary. The feature adapter owns request routing, permissions,
// pacing, cancellation and disposal. This helper never requests accounts or orders.
internal static class IbSocketRuntime
{
    public static (object Client, object Signal) Create(Assembly api, Action<MethodInfo, object?[]> callback)
    {
        var proxy = IbCallbackDispatchProxy.Create(api, callback);
        var signal = Activator.CreateInstance(api.GetType("IBApi.EReaderMonitorSignal", true)!)!;
        var client = Activator.CreateInstance(api.GetType("IBApi.EClientSocket", true)!, proxy, signal)!;
        return (client, signal);
    }

    public static void Connect(object client, string host, int port, int clientId)
    {
        var method = client.GetType().GetMethods().Where(static m => m.Name == "eConnect")
            .OrderByDescending(static m => m.GetParameters().Length)
            .FirstOrDefault(static m => m.GetParameters().Length is 3 or 4)
            ?? throw new MissingMethodException("IB eConnect");
        method.Invoke(client, method.GetParameters().Length == 4
            ? new object?[] { host, port, clientId, false } : new object?[] { host, port, clientId });
    }

    public static object StartReader(Assembly api, object client, object signal)
    {
        var reader = Activator.CreateInstance(api.GetType("IBApi.EReader", true)!, client, signal)!;
        Invoke(reader, "Start");
        return reader;
    }

    public static void Invoke(object target, string method, params object?[] args)
    {
        var candidate = target.GetType().GetMethods().FirstOrDefault(value => value.Name == method
            && value.GetParameters().Length == args.Length) ?? throw new MissingMethodException(target.GetType().FullName, method);
        candidate.Invoke(target, args);
    }
}
