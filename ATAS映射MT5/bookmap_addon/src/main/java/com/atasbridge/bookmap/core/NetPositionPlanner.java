package com.atasbridge.bookmap.core;

public final class NetPositionPlanner {
    private NetPositionPlanner() {
    }

    public static NetPositionPlan plan(
        int currentPosition,
        double targetPositionValue,
        int unitMultiplier,
        int maxDeltaPerSync
    ) {
        if (!isInteger(targetPositionValue)) {
            throw new IllegalArgumentException("target position must be an integer");
        }
        if (unitMultiplier <= 0) {
            throw new IllegalArgumentException("unit multiplier must be greater than zero");
        }
        if (maxDeltaPerSync <= 0) {
            throw new IllegalArgumentException("max delta per sync must be greater than zero");
        }

        int targetPosition = (int) targetPositionValue;
        int delta = targetPosition - currentPosition;
        int absoluteDelta = Math.abs(delta);
        if (absoluteDelta > maxDeltaPerSync) {
            throw new IllegalArgumentException(
                "delta exceeds maxDeltaPerSync: " + absoluteDelta + " > " + maxDeltaPerSync
            );
        }

        if (delta == 0) {
            return new NetPositionPlan("NONE", 0, currentPosition, targetPosition, delta);
        }

        String side = delta > 0 ? "BUY" : "SELL";
        int quantity = absoluteDelta * unitMultiplier;
        return new NetPositionPlan(side, quantity, currentPosition, targetPosition, delta);
    }

    private static boolean isInteger(double value) {
        return !Double.isNaN(value) && !Double.isInfinite(value) && Math.rint(value) == value;
    }
}
