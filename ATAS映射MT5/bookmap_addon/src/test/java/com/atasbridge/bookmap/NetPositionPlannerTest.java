package com.atasbridge.bookmap;

import com.atasbridge.bookmap.core.NetPositionPlan;
import com.atasbridge.bookmap.core.NetPositionPlanner;
import com.atasbridge.bookmap.core.BookmapSymbolConfig;
import com.atasbridge.bookmap.core.SyncPositionRequest;
import com.atasbridge.bookmap.gateway.GatewaySyncHandler;

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
}
