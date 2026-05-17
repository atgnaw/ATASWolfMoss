package com.atasbridge.bookmap;

import com.atasbridge.bookmap.core.BookmapCurrentPositionProvider;
import com.atasbridge.bookmap.core.BookmapOrderExecutor;
import com.atasbridge.bookmap.gateway.GatewayClient;
import com.atasbridge.bookmap.gateway.GatewaySyncHandler;

import java.net.URI;

/**
 * Runtime wiring for the Bookmap add-on wrapper.
 *
 * <p>The Bookmap API lifecycle class should create this runtime after it has
 * access to Bookmap position/order APIs. The two adapters are deliberately tiny:
 * one reads current net position, the other sends a market order.</p>
 */
public final class BookmapAddonRuntime {
    private final GatewayClient gatewayClient;

    public BookmapAddonRuntime(
        URI gatewayUri,
        ATASBookmapBridge bridge,
        BookmapCurrentPositionProvider positionProvider,
        BookmapOrderExecutor orderExecutor
    ) {
        GatewaySyncHandler syncHandler = new GatewaySyncHandler(bridge, positionProvider, orderExecutor);
        final GatewayClient[] clientRef = new GatewayClient[1];
        GatewayClient client = new GatewayClient(gatewayUri, message -> {
            String response = syncHandler.handle(message);
            clientRef[0].send(response);
        });
        clientRef[0] = client;
        this.gatewayClient = client;
    }

    public void start() {
        gatewayClient.connectAndRegister();
    }

}
