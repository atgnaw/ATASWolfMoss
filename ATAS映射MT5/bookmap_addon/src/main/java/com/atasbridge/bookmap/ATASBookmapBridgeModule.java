package com.atasbridge.bookmap;

import velox.api.layer1.annotations.Layer1ApiVersion;
import velox.api.layer1.annotations.Layer1ApiVersionValue;
import velox.api.layer1.annotations.Layer1SimpleAttachable;
import velox.api.layer1.annotations.Layer1StrategyName;
import velox.api.layer1.data.InstrumentInfo;
import velox.api.layer1.data.OrderDuration;
import velox.api.layer1.data.SimpleOrderSendParameters;
import velox.api.layer1.data.StatusInfo;
import velox.api.layer1.simplified.Api;
import velox.api.layer1.simplified.CustomModule;
import velox.api.layer1.simplified.InitialState;
import velox.api.layer1.simplified.Parameter;
import velox.api.layer1.simplified.PositionListener;

import java.util.concurrent.atomic.AtomicInteger;

@Layer1SimpleAttachable
@Layer1StrategyName("ATAS Bookmap Bridge")
@Layer1ApiVersion(Layer1ApiVersionValue.VERSION2)
public final class ATASBookmapBridgeModule implements CustomModule, PositionListener {
    @Parameter(name = "Enabled", step = 1, minimum = 0, maximum = 1, reloadOnChange = true)
    public Boolean enabled = false;

    @Parameter(name = "Dry run", step = 1, minimum = 0, maximum = 1, reloadOnChange = true)
    public Boolean dryRun = true;

    @Parameter(name = "Gateway URL", step = 1, minimum = 0, maximum = 1, reloadOnChange = true)
    public String gatewayUrl = "ws://127.0.0.1:8766";

    @Parameter(name = "ATAS symbol", step = 1, minimum = 0, maximum = 1, reloadOnChange = true)
    public String atasSymbol = "MNQM6@CME";

    @Parameter(name = "Bookmap alias", step = 1, minimum = 0, maximum = 1, reloadOnChange = true)
    public String bookmapAlias = "";

    @Parameter(name = "Unit multiplier", step = 1, minimum = 1, maximum = 100, reloadOnChange = true)
    public Integer unitMultiplier = 1;

    @Parameter(name = "Max delta per sync", step = 1, minimum = 1, maximum = 100, reloadOnChange = true)
    public Integer maxDeltaPerSync = 3;

    private final AtomicInteger currentPosition = new AtomicInteger();
    private volatile BookmapAddonRuntime runtime;
    private volatile Api api;
    private volatile String alias;

    @Override
    public void initialize(String alias, InstrumentInfo info, Api api, InitialState initialState) {
        this.api = api;
        this.alias = alias;

        BookmapAddonSettings settings = toSettings();
        if (!settings.enabled()) {
            log("disabled; enable the module after configuring Gateway and symbols");
            return;
        }

        ATASBookmapBridge bridge = new ATASBookmapBridge();
        bridge.setDryRun(settings.dryRun());
        bridge.putSymbolConfig(settings.symbolConfig(alias));

        runtime = new BookmapAddonRuntime(
            settings.gatewayUri(),
            bridge,
            ignoredAlias -> currentPosition.get(),
            this::executeMarketOrder,
            this::log
        );
        runtime.start();
        log("started for Bookmap alias=" + settings.symbolConfig(alias).bookmapAlias()
            + ", ATAS symbol=" + settings.symbolConfig(alias).atasSymbol()
            + ", Gateway=" + settings.gatewayUri()
            + ", dryRun=" + settings.dryRun());
    }

    @Override
    public void stop() {
        BookmapAddonRuntime activeRuntime = runtime;
        runtime = null;
        if (activeRuntime != null) {
            activeRuntime.stop();
        }
        log("stopped");
    }

    @Override
    public void onPositionUpdate(StatusInfo statusInfo) {
        if (statusInfo == null) {
            return;
        }
        if (statusInfo.instrumentAlias == null || !statusInfo.instrumentAlias.equals(alias)) {
            return;
        }
        currentPosition.set(statusInfo.position);
        log("position update: alias=" + statusInfo.instrumentAlias + ", position=" + statusInfo.position);
    }

    private BookmapAddonSettings toSettings() {
        BookmapAddonSettings settings = new BookmapAddonSettings();
        settings.gatewayUrl = gatewayUrl;
        settings.atasSymbol = atasSymbol;
        settings.bookmapAlias = bookmapAlias;
        settings.unitMultiplier = unitMultiplier == null ? 1 : unitMultiplier;
        settings.maxDeltaPerSync = maxDeltaPerSync == null ? 3 : maxDeltaPerSync;
        settings.enabled = Boolean.TRUE.equals(enabled);
        settings.dryRun = !Boolean.FALSE.equals(dryRun);
        return settings;
    }

    private void executeMarketOrder(String bookmapAlias, String side, int quantity) {
        boolean isBuy = "BUY".equals(side);
        SimpleOrderSendParameters order = new SimpleOrderSendParameters(
            bookmapAlias,
            isBuy,
            quantity,
            OrderDuration.IOC,
            Double.NaN,
            Double.NaN
        );
        api.sendOrder(order);
        log("sent market order: alias=" + bookmapAlias + ", side=" + side + ", quantity=" + quantity);
    }

    private void log(String message) {
        String fullMessage = "[ATAS Bookmap Bridge] " + message;
        System.out.println(fullMessage);
    }
}
