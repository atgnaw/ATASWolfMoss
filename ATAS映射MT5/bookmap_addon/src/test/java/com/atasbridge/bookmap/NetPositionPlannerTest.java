package com.atasbridge.bookmap;

import com.atasbridge.bookmap.core.NetPositionPlan;
import com.atasbridge.bookmap.core.NetPositionPlanner;
import com.atasbridge.bookmap.core.BookmapSymbolConfig;
import com.atasbridge.bookmap.core.SyncPositionRequest;
import com.atasbridge.bookmap.gateway.GatewayClient;
import com.atasbridge.bookmap.gateway.GatewaySyncHandler;
import velox.api.layer1.simplified.Api;
import velox.api.layer1.simplified.Parameter;

import java.io.IOException;
import java.lang.reflect.Field;
import java.lang.reflect.Proxy;
import java.net.URI;
import java.net.http.WebSocket;
import java.util.concurrent.CompletableFuture;

public final class NetPositionPlannerTest {
    public static void main(String[] args) {
        assertPlan("0 -> +2", 0, 2, 1, "BUY", 2);
        assertPlan("+2 -> +1", 2, 1, 1, "SELL", 1);
        assertPlan("+2 -> -1", 2, -1, 1, "SELL", 3);
        assertPlan("-3 -> 0", -3, 0, 1, "BUY", 3);
        assertPlan("unit multiplier", 0, 2, 2, "BUY", 4);

        expectFailure("fractional target", () -> NetPositionPlanner.plan(0, 1.5, 1, 10));
        expectFailure("too large delta", () -> NetPositionPlanner.plan(0, 11, 1, 10));
        assertDryRunDoesNotExecuteOrder();
        assertGatewayHandlerReturnsSuccessJson();
        assertDefaultAddonSettingsAreSafe();
        assertAddonSettingsCreateSymbolConfig();
        assertBookmapParameterFieldsUseWrapperTypes();
        assertModuleDoesNotManuallySubscribeStatusListener();
        assertGatewayClientIgnoresGatewayStatusMessages();
        assertGatewayClientCloseIgnoresAlreadyClosedSocket();
    }

    private static void assertPlan(
        String name,
        int current,
        double target,
        int multiplier,
        String expectedSide,
        int expectedQuantity
    ) {
        NetPositionPlan plan = NetPositionPlanner.plan(current, target, multiplier, 10);
        if (!expectedSide.equals(plan.side())) {
            throw new AssertionError(name + " side expected " + expectedSide + " but got " + plan.side());
        }
        if (expectedQuantity != plan.quantity()) {
            throw new AssertionError(name + " quantity expected " + expectedQuantity + " but got " + plan.quantity());
        }
    }

    private static void expectFailure(String name, Runnable runnable) {
        try {
            runnable.run();
            throw new AssertionError(name + " should have failed");
        } catch (IllegalArgumentException expected) {
            // expected
        }
    }

    private static void assertDryRunDoesNotExecuteOrder() {
        ATASBookmapBridge bridge = new ATASBookmapBridge();
        bridge.putSymbolConfig(new BookmapSymbolConfig("MNQM6@CME", "MNQM6", 1, true, 10));
        bridge.setDryRun(true);

        NetPositionPlan plan = bridge.executeSyncPosition(
            new SyncPositionRequest("MNQM6@CME", 1, 0, "ATAS"),
            0,
            (alias, side, quantity) -> {
                throw new AssertionError("dry run must not execute order");
            }
        );

        if (!"BUY".equals(plan.side()) || plan.quantity() != 1) {
            throw new AssertionError("dry run plan mismatch");
        }
    }

    private static void assertGatewayHandlerReturnsSuccessJson() {
        ATASBookmapBridge bridge = new ATASBookmapBridge();
        bridge.putSymbolConfig(new BookmapSymbolConfig("MNQM6@CME", "MNQM6", 1, true, 10));
        bridge.setDryRun(true);

        GatewaySyncHandler handler = new GatewaySyncHandler(
            bridge,
            alias -> 2,
            (alias, side, quantity) -> {
                throw new AssertionError("dry run must not execute order");
            }
        );

        String response = handler.handle("""
            {
              "id": "abc",
              "action": "sync_position",
              "params": {
                "symbol": "MNQM6@CME",
                "net_volume": -1,
                "average_price": 0,
                "source": "ATAS"
              }
            }
            """);

        if (!response.contains("\"id\":\"abc\"") || !response.contains("\"status\":\"success\"")) {
            throw new AssertionError("gateway handler response mismatch: " + response);
        }
        if (!response.contains("\"side\":\"SELL\"") || !response.contains("\"quantity\":3")) {
            throw new AssertionError("gateway handler plan mismatch: " + response);
        }
    }

    private static void assertDefaultAddonSettingsAreSafe() {
        BookmapAddonSettings settings = new BookmapAddonSettings();
        if (!URI.create("ws://127.0.0.1:8766").equals(settings.gatewayUri())) {
            throw new AssertionError("default gateway URI mismatch");
        }
        if (!settings.dryRun()) {
            throw new AssertionError("default dryRun must be true");
        }
        if (settings.enabled()) {
            throw new AssertionError("default enabled must be false");
        }
    }

    private static void assertAddonSettingsCreateSymbolConfig() {
        BookmapAddonSettings settings = new BookmapAddonSettings();
        settings.atasSymbol = "MNQM6@CME";
        settings.bookmapAlias = "MNQ";
        settings.unitMultiplier = 2;
        settings.maxDeltaPerSync = 5;
        settings.enabled = true;

        BookmapSymbolConfig config = settings.symbolConfig("MNQZ6");

        if (!"MNQM6@CME".equals(config.atasSymbol())) {
            throw new AssertionError("ATAS symbol mismatch");
        }
        if (!"MNQ".equals(config.bookmapAlias())) {
            throw new AssertionError("explicit Bookmap alias mismatch");
        }
        if (config.unitMultiplier() != 2 || config.maxDeltaPerSync() != 5 || !config.enabled()) {
            throw new AssertionError("symbol config values mismatch");
        }
    }

    private static void assertBookmapParameterFieldsUseWrapperTypes() {
        for (Field field : ATASBookmapBridgeModule.class.getDeclaredFields()) {
            if (field.getAnnotation(Parameter.class) == null) {
                continue;
            }
            if (field.getType().isPrimitive()) {
                throw new AssertionError(
                    "@Parameter field must use wrapper type, not primitive: " + field.getName()
                );
            }
        }
    }

    private static void assertModuleDoesNotManuallySubscribeStatusListener() {
        ATASBookmapBridgeModule module = new ATASBookmapBridgeModule();
        Api api = (Api) Proxy.newProxyInstance(
            Api.class.getClassLoader(),
            new Class<?>[] {Api.class},
            (proxy, method, args) -> {
                if ("addStatusListeners".equals(method.getName())) {
                    throw new AssertionError("Bookmap auto-subscribes PositionListener; do not call addStatusListeners");
                }
                if (method.getReturnType().isPrimitive()) {
                    if (boolean.class.equals(method.getReturnType())) {
                        return false;
                    }
                    return 0;
                }
                return null;
            }
        );

        module.initialize("MNQ", null, api, null);
        module.stop();
    }

    private static void assertGatewayClientIgnoresGatewayStatusMessages() {
        if (GatewayClient.shouldDispatchToExecutor("""
            {"status":"success","message":"connected to ATAS execution gateway"}
            """)) {
            throw new AssertionError("Gateway status messages must not be dispatched to sync handler");
        }
        if (!GatewayClient.shouldDispatchToExecutor("""
            {"id":"gw-bookmap-1","action":"sync_position","params":{"symbol":"MNQM6@CME","net_volume":1}}
            """)) {
            throw new AssertionError("sync_position messages must be dispatched to sync handler");
        }
    }

    private static void assertGatewayClientCloseIgnoresAlreadyClosedSocket() {
        GatewayClient client = new GatewayClient(URI.create("ws://127.0.0.1:8766"), message -> {
            throw new AssertionError("no messages expected");
        });
        WebSocket closedSocket = (WebSocket) Proxy.newProxyInstance(
            WebSocket.class.getClassLoader(),
            new Class<?>[] {WebSocket.class},
            (proxy, method, args) -> {
                if ("sendClose".equals(method.getName())) {
                    return CompletableFuture.failedFuture(new IOException("Output closed"));
                }
                if ("isOutputClosed".equals(method.getName())) {
                    return true;
                }
                if ("isInputClosed".equals(method.getName())) {
                    return true;
                }
                if (method.getReturnType().isPrimitive()) {
                    if (boolean.class.equals(method.getReturnType())) {
                        return false;
                    }
                    if (long.class.equals(method.getReturnType())) {
                        return 0L;
                    }
                    return 0;
                }
                return null;
            }
        );

        try {
            Field field = GatewayClient.class.getDeclaredField("webSocket");
            field.setAccessible(true);
            field.set(client, closedSocket);
            client.close();
        } catch (ReflectiveOperationException exc) {
            throw new AssertionError("failed to set websocket for close test", exc);
        }
    }
}
