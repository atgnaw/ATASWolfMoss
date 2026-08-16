package com.atasbridge.bookmap.gateway;

import com.atasbridge.bookmap.ATASBookmapBridge;
import com.atasbridge.bookmap.core.BookmapCurrentPositionProvider;
import com.atasbridge.bookmap.core.BookmapOrderExecutor;
import com.atasbridge.bookmap.core.NetPositionPlan;
import com.atasbridge.bookmap.core.SyncPositionRequest;

import java.util.regex.Matcher;
import java.util.regex.Pattern;

public final class GatewaySyncHandler {
    private static final Pattern ID_PATTERN = Pattern.compile("\"id\"\\s*:\\s*\"([^\"]+)\"");
    private static final Pattern ACTION_PATTERN = Pattern.compile("\"action\"\\s*:\\s*\"([^\"]+)\"");
    private static final Pattern SYMBOL_PATTERN = Pattern.compile("\"symbol\"\\s*:\\s*\"([^\"]+)\"");
    private static final Pattern NET_VOLUME_PATTERN = Pattern.compile("\"net_volume\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)");
    private static final Pattern AVERAGE_PRICE_PATTERN = Pattern.compile("\"average_price\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)");
    private static final Pattern SOURCE_PATTERN = Pattern.compile("\"source\"\\s*:\\s*\"([^\"]+)\"");

    private final ATASBookmapBridge bridge;
    private final BookmapCurrentPositionProvider positionProvider;
    private final BookmapOrderExecutor orderExecutor;

    public GatewaySyncHandler(
        ATASBookmapBridge bridge,
        BookmapCurrentPositionProvider positionProvider,
        BookmapOrderExecutor orderExecutor
    ) {
        this.bridge = bridge;
        this.positionProvider = positionProvider;
        this.orderExecutor = orderExecutor;
    }

    public String handle(String json) {
        String id = findString(ID_PATTERN, json, "");
        try {
            String action = findString(ACTION_PATTERN, json, "");
            if (!"sync_position".equals(action)) {
                return response(id, "error", "unsupported action: " + action, null);
            }

            String symbol = requireString(SYMBOL_PATTERN, json, "symbol");
            double netVolume = requireDouble(NET_VOLUME_PATTERN, json, "net_volume");
            double averagePrice = findDouble(AVERAGE_PRICE_PATTERN, json, 0);
            String source = findString(SOURCE_PATTERN, json, "ATAS");

            SyncPositionRequest request = new SyncPositionRequest(symbol, netVolume, averagePrice, source);
            String bookmapAlias = bridge.bookmapAliasFor(symbol);
            int currentPosition = positionProvider.currentPosition(bookmapAlias);
            NetPositionPlan plan = bridge.executeSyncPosition(request, currentPosition, orderExecutor);

            String data = "\"side\":\"" + plan.side() + "\","
                + "\"quantity\":" + plan.quantity() + ","
                + "\"current_position\":" + plan.currentPosition() + ","
                + "\"target_position\":" + plan.targetPosition() + ","
                + "\"delta\":" + plan.delta();
            return response(id, "success", "bookmap sync handled", data);
        } catch (Exception exc) {
            return response(id, "error", exc.getMessage(), null);
        }
    }

    private static String response(String id, String status, String message, String dataFields) {
        StringBuilder builder = new StringBuilder();
        builder.append("{");
        if (id != null && !id.isBlank()) {
            builder.append("\"id\":\"").append(escape(id)).append("\",");
        }
        builder.append("\"status\":\"").append(escape(status)).append("\",");
        builder.append("\"message\":\"").append(escape(message)).append("\"");
        if (dataFields != null && !dataFields.isBlank()) {
            builder.append(",\"data\":{").append(dataFields).append("}");
        }
        builder.append("}");
        return builder.toString();
    }

    private static String requireString(Pattern pattern, String json, String fieldName) {
        String value = findString(pattern, json, "");
        if (value.isBlank()) {
            throw new IllegalArgumentException("missing " + fieldName);
        }
        return value;
    }

    private static double requireDouble(Pattern pattern, String json, String fieldName) {
        Matcher matcher = pattern.matcher(json);
        if (!matcher.find()) {
            throw new IllegalArgumentException("missing " + fieldName);
        }
        return Double.parseDouble(matcher.group(1));
    }

    private static String findString(Pattern pattern, String json, String defaultValue) {
        Matcher matcher = pattern.matcher(json);
        return matcher.find() ? matcher.group(1) : defaultValue;
    }

    private static double findDouble(Pattern pattern, String json, double defaultValue) {
        Matcher matcher = pattern.matcher(json);
        return matcher.find() ? Double.parseDouble(matcher.group(1)) : defaultValue;
    }

    private static String escape(String value) {
        return value == null ? "" : value.replace("\\", "\\\\").replace("\"", "\\\"");
    }
}
