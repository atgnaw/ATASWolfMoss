// 文件职责：维护有界连续价区，检查极值附近三档单侧成交量，并保留减速标记的创建、移动、翻向与隐藏语义。
// 可见性从吸收模块的只读合同输入。
#include "patterns.h"

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <optional>
#include <stdexcept>
#include <vector>

namespace vtracker {
namespace {

constexpr std::size_t MAXIMUM_WINDOW = 35;
constexpr std::int64_t MINIMUM_SPAN = 3;
constexpr std::int64_t PROXIMITY_TICKS = 3;
constexpr std::size_t PATTERN_LEVELS = 3;

double deviationsFor(Strength strength) {
    switch (strength) {
    case Strength::Weak:
        return 0.45;
    case Strength::Strong:
        return 1.05;
    default:
        return 0.8;
    }
}

std::int64_t slowdownThreshold(const Statistics& statistics, Strength strength) {
    const auto value = mean(statistics) - standardDeviation(statistics) * deviationsFor(strength);
    if (!std::isfinite(value) || value < std::numeric_limits<std::int32_t>::min() || value > std::numeric_limits<std::int32_t>::max()) {
        throw std::overflow_error("Slowdown threshold exceeds the source Int32 domain");
    }

    // C#转换向0截断；负门槛不能钳制成0，否则会改变三档零量的判定。
    return static_cast<std::int64_t>(value);
}

std::vector<PriceLevel> expandedLevels(const SlowdownState& previous, std::int64_t low, std::int64_t high) {
    const auto count = checkedAdd(checkedDifference(high, low), 1);
    std::vector<PriceLevel> levels;
    levels.reserve(static_cast<std::size_t>(count));
    for (std::int64_t offset = 0; offset < count; ++offset) {
        const auto price = checkedAdd(low, offset);
        if (price >= previous.lowTicks && price <= previous.highTicks) {
            levels.push_back(previous.levels.at(static_cast<std::size_t>(checkedDifference(price, previous.lowTicks))));
        } else {
            levels.push_back(PriceLevel{.priceTicks = price});
        }
    }

    return levels;
}

SlowdownState extendRange(const SlowdownState& previous, const Tick& tick) {
    auto next = previous;
    next.lowTicks = std::min(previous.lowTicks, tick.priceTicks);
    next.highTicks = std::max(previous.highTicks, tick.priceTicks);
    const auto span = checkedDifference(next.highTicks, next.lowTicks);
    if (next.lowTicks != previous.lowTicks || next.highTicks != previous.highTicks) {
        next.levels = expandedLevels(previous, next.lowTicks, next.highTicks);
    }

    if (span > MINIMUM_SPAN) {
        if (next.lowTicks != previous.lowTicks) {
            next.lowProximityCeiling = checkedAdd(next.lowTicks, PROXIMITY_TICKS);
        }

        if (next.highTicks != previous.highTicks) {
            next.highProximityFloor = checkedDifference(next.highTicks, PROXIMITY_TICKS);
        }
    }

    return next;
}

SlowdownState accumulate(const SlowdownState& previous, const Tick& tick) {
    if (tick.side == Side::Unknown) {
        return previous;
    }

    const auto index = static_cast<std::size_t>(checkedDifference(tick.priceTicks, previous.lowTicks));
    const auto level = previous.levels.at(index);
    const auto ask = tick.side == Side::Buy ? checkedAdd(level.askVolume, tick.volume) : level.askVolume;
    const auto bid = tick.side == Side::Sell ? checkedAdd(level.bidVolume, tick.volume) : level.bidVolume;
    auto next = previous;
    next.levels[index] = PriceLevel{tick.priceTicks, ask, bid, checkedAdd(ask, bid)};
    next.maximumVolume = std::max({previous.maximumVolume, ask, bid});
    const auto isNearExtreme =
        tick.side == Side::Buy ? tick.priceTicks >= previous.highProximityFloor : tick.priceTicks <= previous.lowProximityCeiling;
    next.hasPendingEvaluation = previous.hasPendingEvaluation || isNearExtreme;
    return next;
}

bool matchesSide(const SlowdownState& state, bool isUp) {
    std::int64_t priorVolume = 0;
    for (std::size_t index = 0; index < PATTERN_LEVELS; ++index) {
        const auto& level = isUp ? state.levels.at(index) : state.levels.at(state.levels.size() - 1 - index);
        const auto volume = isUp ? level.bidVolume : level.askVolume;
        if (volume < priorVolume || volume > state.volumeThreshold) {
            return false;
        }

        priorVolume = volume;
    }

    return true;
}

bool isVisibleAt(std::int64_t extreme, const Bar& bar, const LevelVisibility& visibility) {
    return visibility.isActive
           || (checkedDifference(bar.index, visibility.endIndex) <= visibility.lookbackBars
               && extreme >= visibility.bodyLow
               && extreme <= visibility.bodyHigh);
}

Signal locatedMarker(const Signal& previous, const Bar& bar, std::int64_t extreme, bool isUp, const LevelVisibility& visibility) {
    auto next = previous;
    next.startIndex = bar.index;
    next.endIndex = bar.index;
    next.startTimeNs = bar.startTimeNs;
    next.endTimeNs = bar.endTimeNs - 1;
    next.lowerTicks = extreme;
    next.upperTicks = extreme;
    next.isUp = isUp;
    next.isVisible = isVisibleAt(extreme, bar, visibility);
    return next;
}

SlowdownResult placeMarker(const SlowdownState& previous, const Bar& bar, const LevelVisibility& visibility, bool isUp) {
    const auto extreme = isUp ? previous.lowTicks : previous.highTicks;
    const auto shouldCreate =
        !previous.hasCurrentPattern && (!previous.marker.has_value() || checkedAdd(previous.marker->startIndex, 1) != bar.index);
    if (!shouldCreate && previous.previousExtreme == extreme) {
        // 源实现极值未变时连X和Visible都不更新；不能用每次重投影替代该早退。
        return {previous, std::nullopt};
    }

    auto next = previous;
    Signal marker = previous.marker.value_or(Signal{});
    if (shouldCreate) {
        next.sequence = previous.sequence + 1;
        next.hasCurrentPattern = true;
        marker = Signal{};
        marker.id = signalId(SignalKind::Slowdown, next.sequence);
        marker.kind = SignalKind::Slowdown;
        marker.createdIndex = bar.index;
    }

    next.previousExtreme = extreme;
    next.marker = locatedMarker(marker, bar, extreme, isUp, visibility);
    return {next, next.marker};
}

SlowdownResult hideMarker(const SlowdownState& previous) {
    if (!previous.hasCurrentPattern || !previous.marker.has_value() || !previous.marker->isVisible) {
        return {previous, std::nullopt};
    }

    auto next = previous;
    auto marker = *previous.marker;
    marker.isVisible = false;
    next.marker = marker;
    return {next, marker};
}

} // namespace

SlowdownState resetSlowdownRange(const SlowdownState& previous, std::int64_t priceTicks, Strength strength) {
    auto next = previous;
    if (previous.isInitialized) {
        next.maxima = appendSample(previous.maxima, static_cast<double>(previous.maximumVolume), MAXIMUM_WINDOW);
        next.volumeThreshold = slowdownThreshold(next.maxima, strength);
    }

    next.isInitialized = true;
    next.levels = {PriceLevel{.priceTicks = priceTicks}};
    next.lowTicks = priceTicks;
    next.highTicks = priceTicks;
    next.maximumVolume = 0;
    next.lowProximityCeiling = priceTicks;
    next.highProximityFloor = priceTicks;
    next.previousExtreme = 0;
    next.hasCurrentPattern = false;
    return next;
}

SlowdownState advanceSlowdown(const SlowdownState& previous, const Tick& tick, std::int64_t rangeLimitTicks, Strength strength) {
    if (rangeLimitTicks < 1) {
        throw std::invalid_argument("Slowdown range must contain at least one tick");
    }

    const auto low = std::min(previous.lowTicks, tick.priceTicks);
    const auto high = std::max(previous.highTicks, tick.priceTicks);
    const auto shouldReset = !previous.isInitialized || checkedDifference(high, low) > rangeLimitTicks;
    // 所有输入采用源实时路径，宿主历史重放和实时推进复用同一因果计算，不依赖呈现阶段标志。
    const auto ranged = shouldReset ? resetSlowdownRange(previous, tick.priceTicks, strength) : extendRange(previous, tick);
    return accumulate(ranged, tick);
}

SlowdownState resetSlowdownData(const SlowdownState& previous) {
    SlowdownState next;
    next.sequence = previous.sequence;
    return next;
}

SlowdownResult evaluateSlowdown(const SlowdownState& previous, const Bar& bar, const LevelVisibility& visibility) {
    if (!previous.isInitialized || checkedDifference(previous.highTicks, previous.lowTicks) < MINIMUM_SPAN || previous.volumeThreshold == 0) {
        return {previous, std::nullopt};
    }

    // 低端Bid形态优先于高端Ask；允许相等和零量，所以不能跳过未成交档位。
    if (matchesSide(previous, true)) {
        return placeMarker(previous, bar, visibility, true);
    }
    if (matchesSide(previous, false)) {
        return placeMarker(previous, bar, visibility, false);
    }

    return hideMarker(previous);
}

SlowdownResult endSlowdown(const SlowdownState& previous, const Bar& bar, const LevelVisibility& visibility, bool forceInstance) {
    auto prepared = previous;
    if (!forceInstance && previous.isInitialized) {
        prepared.lowProximityCeiling = checkedAdd(previous.lowTicks, PROXIMITY_TICKS);
        prepared.highProximityFloor = checkedDifference(previous.highTicks, PROXIMITY_TICKS);
    }

    if (!previous.hasPendingEvaluation) {
        return {prepared, std::nullopt};
    }

    const auto evaluated = evaluateSlowdown(prepared, bar, visibility);
    auto next = evaluated.state;
    next.hasPendingEvaluation = false;
    return {next, evaluated.signal};
}

SlowdownResult revealSlowdown(const SlowdownState& previous) {
    if (!previous.hasCurrentPattern || !previous.marker.has_value() || previous.marker->isVisible) {
        return {previous, std::nullopt};
    }

    auto next = previous;
    auto marker = *previous.marker;
    marker.isVisible = true;
    next.marker = marker;
    return {next, marker};
}

} // namespace vtracker
