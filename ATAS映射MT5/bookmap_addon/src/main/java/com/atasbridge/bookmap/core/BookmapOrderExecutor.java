package com.atasbridge.bookmap.core;

public interface BookmapOrderExecutor {
    void executeMarketOrder(String bookmapAlias, String side, int quantity);
}
