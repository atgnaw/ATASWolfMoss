package com.atasbridge.bookmap;

import com.atasbridge.bookmap.core.BookmapSymbolConfig;

import java.net.URI;

public final class BookmapAddonSettings {
    public String gatewayUrl = "ws://127.0.0.1:8766";
    public String atasSymbol = "MNQM6@CME";
    public String bookmapAlias = "";
    public int unitMultiplier = 1;
    public int maxDeltaPerSync = 3;
    public boolean enabled = false;
    public boolean dryRun = true;

    public URI gatewayUri() {
        return URI.create(gatewayUrl);
    }

    public boolean enabled() {
        return enabled;
    }

    public boolean dryRun() {
        return dryRun;
    }

    public BookmapSymbolConfig symbolConfig(String defaultAlias) {
        String resolvedAlias = bookmapAlias == null || bookmapAlias.isBlank() ? defaultAlias : bookmapAlias;
        return new BookmapSymbolConfig(atasSymbol, resolvedAlias, unitMultiplier, enabled, maxDeltaPerSync);
    }
}
