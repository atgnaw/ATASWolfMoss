// 文件职责：校验 Effort 的配置与行情边界，阻止非法价格、成交量和索引进入纯计算。
#include "engine_internal.h"

#include <algorithm>
#include <cmath>
#include <limits>
#include <stdexcept>

namespace effort::detail {
namespace {

bool isPercent(double value) {
    return std::isfinite(value) && value >= 0.0 && value <= 100.0;
}

void validatePrices(const Bar& bar) {
    if (!std::isfinite(bar.open) || !std::isfinite(bar.high) || !std::isfinite(bar.low) || !std::isfinite(bar.close)) {
        throw std::invalid_argument("Effort prices must be finite");
    }
    if (bar.high < std::max(bar.open, bar.close) || bar.low > std::min(bar.open, bar.close) || bar.low > bar.high) {
        throw std::invalid_argument("Effort OHLC range is inconsistent");
    }
}

void validateVolumes(const Bar& bar) {
    if (bar.askVolume < 0 || bar.bidVolume < 0 || bar.totalVolume < 0) {
        throw std::invalid_argument("Effort volumes must be non-negative");
    }
    if (bar.bidVolume > bar.totalVolume || bar.askVolume > bar.totalVolume - bar.bidVolume) {
        throw std::invalid_argument("Effort total volume must include ask and bid volume");
    }
}

} // namespace

void validateConfig(const Config& config) {
    if (!isPercent(config.minDeltaPercent) || !isPercent(config.maxDeltaPercent) || !isPercent(config.entryZonePercent)) {
        throw std::invalid_argument("Effort percentages must be between 0 and 100");
    }
    if (config.maxDeltaPercent != 0.0 && config.minDeltaPercent > config.maxDeltaPercent) {
        throw std::invalid_argument("Effort minimum delta percent exceeds maximum");
    }
    if (!std::isfinite(config.maxDeltaEffort) || config.maxDeltaEffort < 0.0) {
        throw std::invalid_argument("Effort maximum effort must be finite and non-negative");
    }
    if (config.maxTimeSeconds < 0 || config.maxZoneExtensions < 0 || config.fullRangeMaxBars < 0) {
        throw std::invalid_argument("Effort time and extension limits must be non-negative");
    }
    if (config.minimumBars < 1 || config.emaLength < 1 || config.emaLength >= std::numeric_limits<std::int64_t>::max() - 1) {
        throw std::invalid_argument("Effort lengths must be positive and representable");
    }
}

std::int64_t tickDivisor(const Bar& bar) {
    // 无符号减法避免跨过零点时有符号溢出，再在 +1 前检查可表达范围。
    const auto open = static_cast<std::uint64_t>(bar.openTicks);
    const auto close = static_cast<std::uint64_t>(bar.closeTicks);
    const auto distance = bar.closeTicks >= bar.openTicks ? close - open : open - close;
    if (distance >= static_cast<std::uint64_t>(std::numeric_limits<std::int64_t>::max())) {
        throw std::overflow_error("Effort body tick divisor exceeds int64 range");
    }

    return static_cast<std::int64_t>(distance) + 1;
}

void validateBar(const State& previous, const Bar& bar) {
    if (bar.index < 0 || bar.index != previous.nextBarIndex) {
        throw std::invalid_argument("Effort bars must have consecutive, non-repeated indexes");
    }
    if (bar.index == std::numeric_limits<std::int64_t>::max()) {
        throw std::overflow_error("Effort bar index has no representable right endpoint");
    }
    if (bar.elapsedMicroseconds < 0) {
        throw std::invalid_argument("Effort elapsed time must be non-negative");
    }

    validatePrices(bar);
    validateVolumes(bar);
    static_cast<void>(tickDivisor(bar));
}

} // namespace effort::detail
