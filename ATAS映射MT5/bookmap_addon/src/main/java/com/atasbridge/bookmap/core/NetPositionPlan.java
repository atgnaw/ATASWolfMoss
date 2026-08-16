package com.atasbridge.bookmap.core;

public record NetPositionPlan(String side, int quantity, int currentPosition, int targetPosition, int delta) {
    public boolean isNoop() {
        return quantity == 0 || "NONE".equals(side);
    }
}
