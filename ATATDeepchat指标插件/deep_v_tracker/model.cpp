// 文件职责：验证 Tracker 领域输入与参数，集中处理整数溢出和稳定信号标识；不依赖行情宿主。
#include "model.h"

#include <algorithm>
#include <limits>
#include <stdexcept>

namespace vtracker {
namespace {

constexpr auto MAX_SEQUENCE = (std::uint64_t{1} << 56) - 1;
constexpr auto MAX_PRICE = std::numeric_limits<std::int64_t>::max() / 16;

bool validStrength(Strength value) {
    return value == Strength::Weak || value == Strength::Medium || value == Strength::Strong;
}

void validateVolumes(std::int64_t ask, std::int64_t bid, std::int64_t total) {
    if (ask < 0 || bid < 0 || total < 0 || checkedAdd(ask, bid) > total) {
        throw std::invalid_argument("Tracker volumes must be nonnegative and total must cover ask plus bid");
    }
}

void validateIndexPrice(std::int64_t index, std::int64_t price) {
    if (index < 0 || index > std::numeric_limits<std::int64_t>::max() - 2 || price < -MAX_PRICE || price > MAX_PRICE) {
        throw std::invalid_argument("Tracker bar index or tick price is outside the supported integer range");
    }
}

} // namespace

std::uint64_t signalId(SignalKind kind, std::uint64_t sequence) {
    const auto category = static_cast<std::uint32_t>(kind);
    if (category < 1 || category > 6 || sequence == 0 || sequence > MAX_SEQUENCE) {
        throw std::overflow_error("Tracker signal category or sequence is outside its identifier domain");
    }
    return (static_cast<std::uint64_t>(category) << 56) | sequence;
}

std::int64_t checkedAdd(std::int64_t first, std::int64_t second) {
    constexpr auto MIN = std::numeric_limits<std::int64_t>::min();
    constexpr auto MAX = std::numeric_limits<std::int64_t>::max();
    if ((second > 0 && first > MAX - second) || (second < 0 && first < MIN - second)) {
        throw std::overflow_error("Tracker integer addition overflow");
    }
    return first + second;
}

std::int64_t checkedDifference(std::int64_t first, std::int64_t second) {
    constexpr auto MIN = std::numeric_limits<std::int64_t>::min();
    constexpr auto MAX = std::numeric_limits<std::int64_t>::max();
    if ((second < 0 && first > MAX + second) || (second > 0 && first < MIN + second)) {
        throw std::overflow_error("Tracker integer subtraction overflow");
    }
    return first - second;
}

std::int64_t magnitude(std::int64_t value) {
    return value < 0 ? checkedDifference(0, value) : value;
}

void validateConfig(const Config& config) {
    if (!validStrength(config.acceleration)
        || !validStrength(config.exhaustion)
        || !validStrength(config.slowdown)
        || !validStrength(config.absorption)) {
        throw std::invalid_argument("Tracker strength must be Weak, Medium or Strong");
    }
    if (config.levelMode != LevelMode::Conservative && config.levelMode != LevelMode::Aggressive && config.levelMode != LevelMode::Medium) {
        throw std::invalid_argument("Tracker level mode is invalid");
    }
    if (config.projectionBars < 1 || config.projectionBars > 1024) {
        throw std::invalid_argument("Tracker projection bars must be 1..1024");
    }
}

void validateBar(const Bar& bar) {
    validateIndexPrice(bar.index, bar.lowTicks);
    validateIndexPrice(bar.index, bar.highTicks);
    if (bar.startTimeNs < 0
        || bar.endTimeNs <= bar.startTimeNs
        || bar.lowTicks > std::min(bar.openTicks, bar.closeTicks)
        || bar.highTicks < std::max(bar.openTicks, bar.closeTicks)) {
        throw std::invalid_argument("Tracker bar time interval or OHLC envelope is invalid");
    }
    validateVolumes(bar.askVolume, bar.bidVolume, bar.totalVolume);
    if (bar.askCount < 0 || bar.bidCount < 0 || (bar.hasKnownDirection && checkedAdd(bar.askVolume, bar.bidVolume) != bar.totalVolume)) {
        throw std::invalid_argument("Tracker known-direction bar volumes or trade counts are inconsistent");
    }
    for (std::size_t index = 0; index < bar.profile.size(); ++index) {
        const auto& level = bar.profile[index];
        validateVolumes(level.askVolume, level.bidVolume, level.totalVolume);
        if (level.priceTicks < bar.lowTicks
            || level.priceTicks > bar.highTicks
            || (index > 0 && bar.profile[index - 1].priceTicks >= level.priceTicks)) {
            throw std::invalid_argument("Tracker profile must be strictly increasing and within bar OHLC");
        }
    }
}

void validateTick(const Tick& tick) {
    validateIndexPrice(tick.barIndex, tick.priceTicks);
    if (tick.timeNs < 0 || tick.volume < 0 || (tick.side != Side::Buy && tick.side != Side::Sell && tick.side != Side::Unknown)) {
        throw std::invalid_argument("Tracker trade timestamp, volume or direction is invalid");
    }
}

} // namespace vtracker
