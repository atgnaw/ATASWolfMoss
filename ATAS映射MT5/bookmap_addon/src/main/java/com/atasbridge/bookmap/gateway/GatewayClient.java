package com.atasbridge.bookmap.gateway;

import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.WebSocket;
import java.time.Duration;
import java.util.Objects;
import java.util.concurrent.CompletionException;
import java.util.concurrent.CompletionStage;
import java.util.function.Consumer;

public final class GatewayClient implements WebSocket.Listener {
    private final URI gatewayUri;
    private final Consumer<String> messageHandler;
    private WebSocket webSocket;

    public GatewayClient(URI gatewayUri, Consumer<String> messageHandler) {
        this.gatewayUri = Objects.requireNonNull(gatewayUri, "gatewayUri");
        this.messageHandler = Objects.requireNonNull(messageHandler, "messageHandler");
    }

    public void connectAndRegister() {
        webSocket = HttpClient.newBuilder()
            .connectTimeout(Duration.ofSeconds(10))
            .build()
            .newWebSocketBuilder()
            .connectTimeout(Duration.ofSeconds(10))
            .buildAsync(gatewayUri, this)
            .join();

        send(GatewayRegistrationMessage.json());
    }

    public void send(String json) {
        if (webSocket == null) {
            throw new IllegalStateException("Gateway websocket is not connected");
        }
        webSocket.sendText(json, true).join();
    }

    public void close() {
        WebSocket socket = webSocket;
        webSocket = null;
        if (socket != null && !socket.isOutputClosed()) {
            try {
                socket.sendClose(WebSocket.NORMAL_CLOSURE, "Bookmap add-on stopped").join();
            } catch (CompletionException | IllegalStateException ignored) {
                // Bookmap may stop the module after the Gateway has already closed the socket.
                // Stop must be best-effort and never crash the Bookmap module.
            }
        }
    }

    @Override
    public CompletionStage<?> onText(WebSocket webSocket, CharSequence data, boolean last) {
        if (last && shouldDispatchToExecutor(data.toString())) {
            messageHandler.accept(data.toString());
        }
        webSocket.request(1);
        return null;
    }

    @Override
    public void onOpen(WebSocket webSocket) {
        WebSocket.Listener.super.onOpen(webSocket);
        webSocket.request(1);
    }

    public static boolean shouldDispatchToExecutor(String message) {
        return message != null
            && message.contains("\"action\"")
            && message.contains("\"sync_position\"");
    }
}
