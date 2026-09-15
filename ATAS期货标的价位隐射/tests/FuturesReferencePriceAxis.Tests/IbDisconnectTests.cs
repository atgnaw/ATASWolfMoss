using System.Reflection;
using WolfMoss.ATAS.PriceMapping;

internal static class IbDisconnectTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Set(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);

    public static void OfficialSignature()
    {
        var pro = PerformanceDiagnosticTests.LoadPro();
        var api = (Assembly)pro.GetType("WolfMoss.ATAS.PriceMapping.EmbeddedDependencyResolver")!
            .GetMethod("TryLoadIbApiAssembly", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        var type = api.GetType("IBApi.EClientSocket")!;
        Check(type.GetMethod("eDisconnect", Type.EmptyTypes) == null, "Official SDK has no zero-argument disconnect overload");
        Check(type.GetMethod("eDisconnect", [typeof(bool)]) != null, "Official SDK bool signature");
        var (client, _) = IbSocketRuntime.Create(api, (_, _) => { });
        IbSocketRuntime.Disconnect(client); // Official client, never connected to any endpoint.
        Check(!(bool)type.GetMethod("IsConnected")!.Invoke(client, null)!, "Official SDK offline disconnect succeeds");
    }

    public static void RepeatedInstrumentRetirement() => RepeatedAsync().GetAwaiter().GetResult();
    private static async Task RepeatedAsync()
    {
        // Simulate ATAS recreating the indicator on NQ -> ES -> NQ. Reuse one
        // connection identity, and verify each real production disposal closes it.
        IbCoordinationTests.FakeSocket? previous = null;
        foreach (var ticker in new[] { "QQQ", "SPX", "QQQ", "SPX", "QQQ" })
        {
            Check(previous == null || !previous.IsConnected, "Previous instrument socket is closed before replacement");
            var (client, socket) = IbCoordinationTests.Connected();
            Set(client, "_sessionReservation", IbSessionReservation.Reserve("offline-switch", 4001, 2210));
            await client.DisposeAsync();
            Check(!socket.IsConnected && socket.DisconnectCalls >= 1, ticker + " releases the real socket path");
            await client.DisposeAsync(); // Idempotent.
            previous = socket;
        }
        using var next = IbSessionReservation.Reserve("offline-switch", 4001, 2210);
    }

    public static void FailedDisconnectKeepsReservation() => FailedAsync().GetAwaiter().GetResult();
    private static async Task FailedAsync()
    {
        var (client, socket) = IbCoordinationTests.Connected();
        var reservation = IbSessionReservation.Reserve("offline-failed-disconnect", 4001, 2210);
        Set(client, "_sessionReservation", reservation);
        socket.FailDisconnect = true;
        try
        {
            try { await client.DisposeAsync(); throw new Exception("Retirement must report disconnect failure"); }
            catch (TargetInvocationException e) when (e.InnerException is IOException) { }
            Check(socket.IsConnected, "Failing socket remains connected in simulation");
            try
            {
                using var unexpected = IbSessionReservation.Reserve("offline-failed-disconnect", 4001, 2210);
                throw new Exception("Failed socket must not release identity reservation");
            }
            catch (InvalidOperationException e) when (e.Message.Contains("CLIENT_ID_IN_USE")) { }
        }
        finally
        {
            socket.FailDisconnect = false;
            IbSocketRuntime.Disconnect(socket);
            reservation.Dispose();
        }
    }
}
