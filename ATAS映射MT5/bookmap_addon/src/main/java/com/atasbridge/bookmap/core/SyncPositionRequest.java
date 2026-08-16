package com.atasbridge.bookmap.core;

public record SyncPositionRequest(String symbol, double netVolume, double averagePrice, String source) {
    public SyncPositionRequest {
        if (symbol == null || symbol.isBlank()) {
            throw new IllegalArgumentException("symbol is required");
        }
    }
}
