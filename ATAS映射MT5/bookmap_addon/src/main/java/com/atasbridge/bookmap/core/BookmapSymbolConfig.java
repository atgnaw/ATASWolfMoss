package com.atasbridge.bookmap.core;

public record BookmapSymbolConfig(
    String atasSymbol,
    String bookmapAlias,
    int unitMultiplier,
    boolean enabled,
    int maxDeltaPerSync
) {
    public BookmapSymbolConfig {
        if (atasSymbol == null || atasSymbol.isBlank()) {
            throw new IllegalArgumentException("atasSymbol is required");
        }
        if (bookmapAlias == null || bookmapAlias.isBlank()) {
            throw new IllegalArgumentException("bookmapAlias is required");
        }
        if (unitMultiplier <= 0) {
            throw new IllegalArgumentException("unitMultiplier must be greater than zero");
        }
        if (maxDeltaPerSync <= 0) {
            throw new IllegalArgumentException("maxDeltaPerSync must be greater than zero");
        }
    }
}
