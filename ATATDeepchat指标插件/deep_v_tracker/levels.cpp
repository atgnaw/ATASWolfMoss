// 文件职责：识别窄幅实体区间，并在离开区间的收柱上输出吸收/压力控制线与极值线。
// 价格为整数 tick，柱索引从宿主传入；不修改旧状态、不负责信号延展。
#include "levels.h"

#include "profile.h"

#include <algorithm>
#include <cstddef>
#include <limits>
#include <stdexcept>
#include <utility>

namespace vtracker {
namespace {

constexpr std::size_t TRUE_RANGE_SAMPLES = 21;
constexpr std::size_t SEGMENT_SAMPLES = 25;
constexpr std::size_t BAR_VOLUME_SAMPLES = 50;
constexpr std::int64_t RANGE_MINIMUM_BARS = 4;
constexpr std::int64_t DEFAULT_MINIMUM_BARS = 5;
constexpr double RANGE_FRACTION = 0.85;
constexpr double EXIT_FRACTION = 0.2;

struct RangeRules {
    std::int64_t minimumBars{};
    std::int64_t maximumTicks{};
    std::int64_t exitTicks{};
    bool isReady{};
};

struct Candidate {
    std::int64_t startIndex{};
    std::int64_t bodyLow{};
    std::int64_t bodyHigh{};
    std::int64_t volume{};
    std::int64_t delta{};
};

std::int64_t truncatedTicks(double value) {
    if (!(value >= 0.0) || value >= static_cast<double>(std::numeric_limits<std::int64_t>::max())) {
        throw std::overflow_error("V Tracker 区间阈值超出 tick 整数范围");
    }

    return static_cast<std::int64_t>(value);
}

RangeRules rangeRules(const LevelState& state, std::int64_t rangeTicks) {
    if (rangeTicks > 0) {
        return {RANGE_MINIMUM_BARS, checkedAdd(rangeTicks, rangeTicks / 2), rangeTicks / 4, true};
    }

    const auto maximumTicks = truncatedTicks(std::max(1.0, mean(state.trueRanges) * RANGE_FRACTION));
    const auto exitTicks = truncatedTicks(static_cast<double>(maximumTicks) / RANGE_FRACTION * EXIT_FRACTION);

    // 原 Buffer 只有发生第 22 次写入才设置 IsFull；21 项样本满了仍不能启动。
    return {DEFAULT_MINIMUM_BARS, maximumTicks, exitTicks, state.trueRanges.totalAdded > TRUE_RANGE_SAMPLES};
}

Statistics appendTrueRange(const LevelState& previous, const Bar& bar) {
    const auto previousClose = previous.history ? previous.history->bar.closeTicks : bar.closeTicks;
    const auto trueRange = std::max({magnitude(checkedDifference(bar.highTicks, bar.lowTicks)),
                                     magnitude(checkedDifference(bar.lowTicks, previousClose)),
                                     magnitude(checkedDifference(bar.highTicks, previousClose))});
    return appendSample(previous.trueRanges, static_cast<double>(trueRange), TRUE_RANGE_SAMPLES);
}

bool leavesRange(const LevelState& state, const Bar& bar, const RangeRules& rules, const Config& config) {
    // Strong 改用影线，而 Weak/Medium 用收盘；严格大于退出距离，触碰不成立。
    const auto upper = config.absorption == Strength::Strong ? bar.highTicks : bar.closeTicks;
    const auto lower = config.absorption == Strength::Strong ? bar.lowTicks : bar.closeTicks;
    return (upper > state.bodyHigh && checkedDifference(upper, state.bodyHigh) > rules.exitTicks)
           || (lower < state.bodyLow && checkedDifference(state.bodyLow, lower) > rules.exitTicks);
}

LevelState extendRange(const LevelState& previous, const Bar& bar) {
    auto next = previous;
    next.maintainedLow = std::min(previous.maintainedLow, bar.lowTicks);
    next.maintainedHigh = std::max(previous.maintainedHigh, bar.highTicks);
    next.volume = checkedAdd(previous.volume, bar.totalVolume);
    next.delta = checkedAdd(previous.delta, checkedDifference(bar.askVolume, bar.bidVolume));
    next.endIndex = bar.index;
    return next;
}

LevelState scanRange(const LevelState& previous, const Bar& bar, const RangeRules& rules) {
    auto next = previous;
    Candidate candidate{bar.index, std::min(bar.openTicks, bar.closeTicks), std::max(bar.openTicks, bar.closeTicks)};
    for (auto node = previous.history; node; node = node->previous) {
        const auto low = std::min(candidate.bodyLow, std::min(node->bar.openTicks, node->bar.closeTicks));
        const auto high = std::max(candidate.bodyHigh, std::max(node->bar.openTicks, node->bar.closeTicks));
        if (checkedDifference(high, low) > rules.maximumTicks) {
            next.nextCandidateIndex = checkedAdd(node->bar.index, rules.minimumBars);
            break;
        }

        candidate.bodyLow = low;
        candidate.bodyHigh = high;
        candidate.startIndex = node->bar.index;

        // 初始扫描只加 Ask+Bid；后续延展使用 TotVol，不能把未知方向量无声归给任一侧。
        candidate.volume = checkedAdd(candidate.volume, checkedAdd(node->bar.askVolume, node->bar.bidVolume));
        candidate.delta = checkedAdd(candidate.delta, checkedDifference(node->bar.askVolume, node->bar.bidVolume));
    }

    next.startIndex = candidate.startIndex;
    if (checkedAdd(checkedDifference(bar.index, candidate.startIndex), 1) < rules.minimumBars) {
        return next;
    }

    next.isActive = true;
    next.bodyLow = candidate.bodyLow;
    next.bodyHigh = candidate.bodyHigh;
    // 初始只取实体，不能在 Conservative 模式下补入这些初始柱的影线。
    next.maintainedLow = candidate.bodyLow;
    next.maintainedHigh = candidate.bodyHigh;
    next.volume = candidate.volume;
    next.delta = candidate.delta;
    next.endIndex = bar.index;
    return next;
}

std::vector<const Bar*> segmentBars(const LevelState& state) {
    std::vector<const Bar*> bars;
    for (auto node = state.history; node && node->bar.index >= state.startIndex; node = node->previous) {
        if (node->bar.index <= state.endIndex) {
            bars.push_back(&node->bar);
        }
    }

    std::reverse(bars.begin(), bars.end());
    return bars;
}

LevelState sampledSegment(const LevelState& previous, const std::vector<const Bar*>& bars) {
    auto next = previous;
    next.segmentVolumes = appendSample(previous.segmentVolumes, static_cast<double>(previous.volume), SEGMENT_SAMPLES);
    next.segmentDeltas = appendSample(previous.segmentDeltas, static_cast<double>(previous.delta), SEGMENT_SAMPLES);
    for (const auto* bar : bars) {
        next.barVolumes = appendSample(next.barVolumes, static_cast<double>(bar->totalVolume), BAR_VOLUME_SAMPLES);
    }

    return next;
}

bool passesVolumeFilter(const LevelState& state, const Config& config, std::int64_t rangeTicks) {
    const bool isRange = rangeTicks > 0;
    const auto strength = config.absorption;
    const auto barDeviation = strength == Strength::Weak     ? (isRange ? 0.7 : -0.5)
                              : strength == Strength::Medium ? (isRange ? 1.0 : -0.3)
                                                             : (isRange ? 1.3 : 0.0);
    const auto segmentDeviation = strength == Strength::Weak ? 0.7 : strength == Strength::Medium ? 1.0 : 1.3;
    const auto deltaDeviation = strength == Strength::Weak ? 0.3 : strength == Strength::Medium ? 0.5 : 0.8;
    const auto count = checkedAdd(checkedDifference(state.endIndex, state.startIndex), 1);
    return static_cast<double>(magnitude(state.volume)) >= threshold(state.barVolumes, barDeviation) * static_cast<double>(count)
           || static_cast<double>(magnitude(state.volume)) >= threshold(state.segmentVolumes, segmentDeviation)
           || static_cast<double>(magnitude(state.delta)) >= threshold(state.segmentDeltas, deltaDeviation);
}

Signal makeLevel(const LevelState& state, const Bar& bar, const Config& config, SignalKind kind, std::int64_t price) {
    const bool highAbove = bar.highTicks > state.bodyHigh;
    Signal signal;
    signal.id = signalId(kind, state.signalSequence);
    signal.kind = kind;
    signal.startIndex = bar.index;
    signal.endIndex = bar.index;
    signal.createdIndex = bar.index;
    signal.startTimeNs = bar.startTimeNs;
    signal.endTimeNs = bar.endTimeNs - 1;
    signal.lowerTicks = price;
    signal.upperTicks = price;
    signal.stopTicks = price;
    signal.lifetimeBars = config.projectionBars;
    signal.isUp = highAbove;
    signal.isAsk = state.delta >= 0;
    signal.isAbsorption = highAbove != signal.isAsk;
    signal.stopsOnAbove = bar.closeTicks < price;
    signal.isActive = true;
    return signal;
}

std::vector<Signal> createLevels(const LevelState& state, const Bar& bar, const Config& config, const std::vector<const Bar*>& bars) {
    const auto area = calculateProfileArea(bars);
    const bool highAbove = bar.highTicks > state.bodyHigh;
    const auto control = config.levelMode == LevelMode::Aggressive ? (highAbove ? state.bodyHigh : state.bodyLow) : area.pointOfControl;
    const auto extreme = config.levelMode == LevelMode::Conservative ? (highAbove ? state.maintainedLow : state.maintainedHigh)
                                                                     : (highAbove ? area.lowTicks : area.highTicks);
    return {makeLevel(state, bar, config, SignalKind::Control, control), makeLevel(state, bar, config, SignalKind::Extreme, extreme)};
}

LevelUpdate finishRange(const LevelState& previous, const Bar& bar, const Config& config, std::int64_t rangeTicks) {
    // history 含本次突破柱，但分价与统计只取旧 endIndex 以前；候选先入统计再判断。
    const auto bars = segmentBars(previous);
    auto next = sampledSegment(previous, bars);
    std::vector<Signal> signals;
    if (passesVolumeFilter(next, config, rangeTicks) && config.enableLevels) {
        if (next.signalSequence == std::numeric_limits<std::uint64_t>::max()) {
            throw std::overflow_error("V Tracker 水平线信号序号已耗尽");
        }

        ++next.signalSequence;
        signals = createLevels(next, bar, config, bars);
    }

    next.isActive = false;
    return {std::move(next), std::move(signals), false};
}

} // namespace

BarNode::BarNode(const Bar& value, std::shared_ptr<const BarNode> predecessor) : bar(value), previous(std::move(predecessor)) {}

BarNode::~BarNode() noexcept {
    auto node = std::move(previous);
    while (node && node.use_count() == 1) {
        // 先保留下一节点，使被释放节点的析构看到共享尾链而停止递归；不修改任何仍存活节点。
        auto next = node->previous;
        node.reset();
        node = std::move(next);
    }
}

LevelUpdate closeLevels(const LevelState& previous, const Bar& bar, const Config& config, std::int64_t rangeTicks) {
    validateBar(bar);
    validateConfig(config);
    if (rangeTicks < 0) {
        throw std::invalid_argument("V Tracker 的 Range 数值不能为负");
    }
    if (previous.history && bar.index != checkedAdd(previous.history->bar.index, 1)) {
        throw std::invalid_argument("V Tracker 水平线需要连续递增的已收柱索引");
    }

    auto next = previous;
    next.history = std::make_shared<const BarNode>(bar, previous.history);
    if (rangeTicks == 0) {
        next.trueRanges = appendTrueRange(previous, bar);
    }

    const auto rules = rangeRules(next, rangeTicks);
    if (!rules.isReady || bar.index <= rules.minimumBars) {
        return {std::move(next), {}, false};
    }
    if (next.isActive) {
        if (leavesRange(next, bar, rules, config)) {
            return finishRange(next, bar, config, rangeTicks);
        }

        return {extendRange(next, bar), {}, false};
    }
    if (bar.index < next.nextCandidateIndex) {
        return {std::move(next), {}, false};
    }

    next = scanRange(next, bar, rules);
    const bool shouldReveal = next.isActive;
    return {std::move(next), {}, shouldReveal};
}

LevelVisibility levelVisibility(const LevelState& state, std::int64_t rangeTicks) {
    return {state.isActive, state.endIndex, rangeTicks > 0 ? RANGE_MINIMUM_BARS : DEFAULT_MINIMUM_BARS, state.bodyLow, state.bodyHigh};
}

LevelState resetLevels(const LevelState& previous) {
    LevelState result;
    result.signalSequence = previous.signalSequence;
    return result;
}

} // namespace vtracker
