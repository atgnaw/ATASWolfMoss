// 文件职责：按已确认的 Effort 规则推进整数强度过滤、连续段、独立 EMA 和区域事件。
// 输入状态始终只读，返回的新状态归调用方。
#include "engine_internal.h"

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdlib>
#include <limits>
#include <stdexcept>
#include <utility>

namespace effort {
namespace {

constexpr std::int64_t MICROSECONDS_PER_SECOND = 1'000'000;
constexpr double PERCENT_SCALE = 100.0;

struct SegmentChange {
    SegmentState state;
    std::optional<PendingSegment> completed;
};

Metrics measure(const Bar& bar, const Config& config) {
    Metrics result;
    result.delta = bar.askVolume - bar.bidVolume;
    result.elapsedSeconds = bar.elapsedMicroseconds / MICROSECONDS_PER_SECOND;
    result.hasVolume = bar.totalVolume != 0;
    if (!result.hasVolume) {
        return result;
    }

    result.deltaPercent = static_cast<double>(std::abs(result.delta)) / static_cast<double>(bar.totalVolume) * PERCENT_SCALE;
    // 必须先做有符号整数除法；改为浮点会改变 149 / 10 之类边界柱的资格。
    result.effort = result.delta / detail::tickDivisor(bar);
    const bool isDirectionMatched = result.delta >= 0 ? bar.close >= bar.open : bar.close < bar.open;
    result.isEligible = bar.hasKnownAggressor
                        && bar.hasValidElapsed
                        && isDirectionMatched
                        && (config.minDeltaPercent == 0.0 || result.deltaPercent >= config.minDeltaPercent)
                        && (config.maxDeltaPercent == 0.0 || result.deltaPercent <= config.maxDeltaPercent)
                        && (config.maxDeltaEffort == 0.0 || static_cast<double>(std::abs(result.effort)) <= config.maxDeltaEffort)
                        && (config.maxTimeSeconds == 0 || result.elapsedSeconds <= config.maxTimeSeconds);
    return result;
}

std::vector<BarRange> rememberRange(const State& previous, const Bar& bar) {
    std::vector<BarRange> result;
    const auto limit = static_cast<std::uint64_t>(previous.config.minimumBars);
    const auto skip = previous.recentBars.size() >= limit ? std::size_t{1} : std::size_t{0};
    result.reserve(previous.recentBars.size() + 1 - skip);
    result.insert(result.end(), previous.recentBars.begin() + static_cast<std::ptrdiff_t>(skip), previous.recentBars.end());
    result.push_back({bar.high, bar.low});
    return result;
}

PendingSegment beginPending(const std::vector<BarRange>& ranges, std::int64_t count, bool isBullish) {
    // 保留原实现从 0 初始化最高价的行为；并扫描最近物理柱，而不是最近合格柱。
    PendingSegment result{0.0, std::numeric_limits<double>::max(), count, isBullish};
    for (const auto& range : ranges) {
        result.high = std::max(result.high, range.high);
        result.low = std::min(result.low, range.low);
    }

    return result;
}

std::int64_t nextCount(const SegmentState& previous, const Metrics& metrics) {
    if (!metrics.isEligible) {
        return 0;
    }
    if ((metrics.delta >= 0) != previous.previousIsBullish) {
        return 1;
    }
    if (previous.consecutiveBars == std::numeric_limits<std::int64_t>::max()) {
        throw std::overflow_error("Effort consecutive bar count exceeds int64 range");
    }

    return previous.consecutiveBars + 1;
}

SegmentChange advanceSegment(const State& previous, const Bar& bar, const Metrics& metrics, const std::vector<BarRange>& ranges) {
    if (!bar.hasKnownAggressor || !bar.hasValidElapsed) {
        return {};
    }
    if (!metrics.hasVolume) {
        return {previous.segment, {}};
    }

    auto next = previous.segment;
    next.consecutiveBars = nextCount(previous.segment, metrics);
    if (metrics.isEligible) {
        next.previousIsBullish = metrics.delta >= 0;
    }
    if (next.consecutiveBars < previous.config.minimumBars) {
        next.pending.reset();
        return {next, previous.segment.pending};
    }

    // minimumBars=1 时方向切换仍达到门槛，原逻辑继续已有段，不额外制造终结事件。
    auto pending = previous.segment.pending ? *previous.segment.pending : beginPending(ranges, next.consecutiveBars, metrics.delta >= 0);
    pending.high = std::max(pending.high, bar.high);
    pending.low = std::min(pending.low, bar.low);
    pending.barCount = next.consecutiveBars;
    pending.isBullish = metrics.delta >= 0;
    next.pending = pending;
    return {next, {}};
}

Zone createZone(const PendingSegment& segment, const State& state, const Bar& bar) {
    if (state.nextZoneId == std::numeric_limits<std::uint64_t>::max()) {
        throw std::overflow_error("Effort zone identifier exceeds uint64 range");
    }

    const auto range = segment.high - segment.low;
    if (!std::isfinite(range)) {
        throw std::overflow_error("Effort segment price range is not representable");
    }

    Zone result{state.nextZoneId, bar.index, bar.index, segment.low, segment.high, segment.isBullish, false, true};
    if (segment.barCount > state.config.fullRangeMaxBars) {
        const auto inset = range / PERCENT_SCALE * state.config.entryZonePercent;
        if (segment.isBullish) {
            result.upper = segment.high - inset;
        } else {
            result.lower = segment.low + inset;
        }
    }

    result.stopsOnAbove = !segment.isBullish && bar.close < result.upper;
    return result;
}

Update applyZoneChanges(Update next, const Bar& bar, const std::optional<PendingSegment>& completed) {
    if (completed) {
        const auto zone = createZone(*completed, next.state, bar);
        next.state.activeZones.push_back(zone);
        next.zoneEvents.push_back({ZoneEventKind::Created, ZoneStopReason::None, zone});
        ++next.state.nextZoneId;
    }

    // 原调用顺序为 CalcState 后 ResetProfile，新建区可被同一创建柱立即停止。
    const auto changes = detail::maintainZones(next.state.activeZones, bar, next.state.config);
    next.state.activeZones = changes.activeZones;
    next.zoneEvents.insert(next.zoneEvents.end(), changes.events.begin(), changes.events.end());
    return next;
}

} // namespace

State initialState(const Config& config, std::int64_t firstBarIndex) {
    detail::validateConfig(config);
    if (firstBarIndex < 0 || firstBarIndex == std::numeric_limits<std::int64_t>::max()) {
        throw std::invalid_argument("Effort initial bar index must have a representable right endpoint");
    }

    State result;
    result.config = config;
    result.nextBarIndex = firstBarIndex;
    return result;
}

EmaState advanceEma(const EmaState& previous, double close, std::int64_t length) {
    if (!std::isfinite(close) || length < 1 || length >= std::numeric_limits<std::int64_t>::max() - 1) {
        throw std::invalid_argument("Effort EMA requires a finite close and a positive, representable length");
    }
    if (previous.observedBars < 0 || previous.observedBars == std::numeric_limits<std::int64_t>::max()) {
        throw std::overflow_error("Effort EMA observation count exceeds int64 range");
    }

    const auto count = previous.observedBars + 1;
    if (count <= length + 1) {
        return {count, {}};
    }

    // 原索引从 -1 开始且必须 >length，因此首个值不是常规 SMA 种子，而是第 length+2 根的收盘价。
    const auto seed = previous.value.value_or(close);
    const auto alpha = 2.0 / (static_cast<double>(length) + 1.0);
    const auto value = (close - seed) * alpha + seed;
    if (!std::isfinite(value)) {
        throw std::overflow_error("Effort EMA value is not representable");
    }

    return {count, value};
}

Update stopExtensions(const State& previous, std::int64_t endIndex, ZoneStopReason reason) {
    const auto changes = detail::freezeZones(previous.activeZones, endIndex, reason);
    auto next = previous;
    next.activeZones = changes.activeZones;
    return {std::move(next), {}, previous.emaState.value, changes.events};
}

Update onBarClosed(const State& previous, const Bar& bar) {
    detail::validateBar(previous, bar);
    const auto opened = bar.isNewSession ? stopExtensions(previous, bar.index, ZoneStopReason::Session) : Update{previous, {}, {}, {}};
    const auto metrics = measure(bar, previous.config);
    const auto ranges = rememberRange(previous, bar);
    const auto segment = advanceSegment(previous, bar, metrics, ranges);
    const auto ema = advanceEma(previous.emaState, bar.close, previous.config.emaLength);

    auto next = opened.state;
    next.nextBarIndex = bar.index + 1;
    next.segment = segment.state;
    next.recentBars = ranges;
    next.emaState = ema;
    return applyZoneChanges({std::move(next), metrics, ema.value, opened.zoneEvents}, bar, segment.completed);
}

} // namespace effort
