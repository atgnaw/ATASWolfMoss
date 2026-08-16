package com.atasbridge.bookmap;

import com.atasbridge.bookmap.core.BookmapSymbolConfig;
import com.atasbridge.bookmap.core.BookmapOrderExecutor;
import com.atasbridge.bookmap.core.NetPositionPlan;
import com.atasbridge.bookmap.core.NetPositionPlanner;
import com.atasbridge.bookmap.core.SyncPositionRequest;

import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;

/**
 * Dependency-light bridge core used by the Bookmap add-on wrapper.
 *
 * <p>The actual Bookmap API wrapper should call {@link #handleSyncPosition} after
 * receiving a Gateway sync_position request, then translate the returned plan
 * into Bookmap Api.sendOrder(...) when dryRun is false.</p>
 */
public final class ATASBookmapBridge {
    private final Map<String, BookmapSymbolConfig> symbols = new ConcurrentHashMap<>();
    private volatile boolean dryRun = true;

    public void setDryRun(boolean dryRun) {
        this.dryRun = dryRun;
    }

    public boolean isDryRun() {
        return dryRun;
    }

    public void putSymbolConfig(BookmapSymbolConfig config) {
        symbols.put(config.atasSymbol(), config);
    }

    public String bookmapAliasFor(String atasSymbol) {
        BookmapSymbolConfig config = symbols.get(atasSymbol);
        if (config == null) {
            throw new IllegalArgumentException("symbol is not configured: " + atasSymbol);
        }
        return config.bookmapAlias();
    }

    public NetPositionPlan handleSyncPosition(SyncPositionRequest request, int currentBookmapPosition) {
        BookmapSymbolConfig config = symbols.get(request.symbol());
        if (config == null) {
            throw new IllegalArgumentException("symbol is not configured: " + request.symbol());
        }
        if (!config.enabled()) {
            throw new IllegalArgumentException("symbol is disabled: " + request.symbol());
        }

        return NetPositionPlanner.plan(
            currentBookmapPosition,
            request.netVolume(),
            config.unitMultiplier(),
            config.maxDeltaPerSync()
        );
    }

    public NetPositionPlan executeSyncPosition(
        SyncPositionRequest request,
        int currentBookmapPosition,
        BookmapOrderExecutor executor
    ) {
        BookmapSymbolConfig config = symbols.get(request.symbol());
        NetPositionPlan plan = handleSyncPosition(request, currentBookmapPosition);

        if (!dryRun && !plan.isNoop()) {
            executor.executeMarketOrder(config.bookmapAlias(), plan.side(), plan.quantity());
        }

        return plan;
    }
}
