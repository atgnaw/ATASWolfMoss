// 文件职责：分别对成交量和输入事件笔数运行滚动Delta转向与方向段峰值过滤，输出收柱衰竭标记。
// N根滚动窗口与最近10个合格方向段统计是两个独立时间尺度。
#include "patterns.h"

#include <algorithm>
#include <cstddef>
#include <optional>
#include <vector>

namespace vtracker {
namespace {

constexpr std::size_t SEGMENT_WINDOW = 10;
constexpr std::int64_t MARKER_OFFSET_TICKS = 3;

std::size_t lengthFor(Strength strength) {
    switch (strength) {
    case Strength::Weak:
        return 5;
    case Strength::Strong:
        return 10;
    default:
        return 8;
    }
}

std::vector<std::int64_t> appendValue(const std::vector<std::int64_t>& previous, std::int64_t value, std::size_t capacity) {
    const auto first = previous.size() >= capacity ? previous.begin() + 1 : previous.begin();
    auto next = std::vector<std::int64_t>(first, previous.end());
    next.push_back(value);
    return next;
}

ExhaustionDetector updateWindow(const ExhaustionDetector& previous, std::int64_t ask, std::int64_t bid, std::size_t length) {
    const auto volume = checkedAdd(ask, bid);
    const auto delta = checkedDifference(ask, bid);
    const auto removedVolume = previous.volumes.size() >= length ? previous.volumes.front() : 0;
    const auto removedDelta = previous.deltas.size() >= length ? previous.deltas.front() : 0;
    auto next = previous;
    next.rollingVolume = checkedDifference(checkedAdd(previous.rollingVolume, volume), removedVolume);
    next.rollingDelta = checkedDifference(checkedAdd(previous.rollingDelta, delta), removedDelta);
    next.volumes = appendValue(previous.volumes, volume, length);
    next.deltas = appendValue(previous.deltas, delta, length);
    return next;
}

struct DetectorResult {
    ExhaustionDetector state;
    std::optional<bool> isUp;
};

DetectorResult finishSegment(const ExhaustionDetector& previous, std::int64_t barIndex, Strength strength) {
    auto next = previous;
    std::optional<bool> signal;
    if (checkedDifference(barIndex, previous.segmentStart) >= static_cast<std::int64_t>(lengthFor(strength) / 2)) {
        next.volumePeaks = appendSample(previous.volumePeaks, static_cast<double>(previous.peakVolume), SEGMENT_WINDOW);
        next.deltaPeaks = appendSample(previous.deltaPeaks, static_cast<double>(previous.peakDelta), SEGMENT_WINDOW);
        const auto deltaDeviations = strength == Strength::Weak ? 1.25 : 1.0;

        // 只采样达到最短长度的旧段；未通过过滤的段也进入统计，首段可直接命中等号。
        if (static_cast<double>(previous.peakVolume) >= threshold(next.volumePeaks, 0.5)
            && static_cast<double>(previous.peakDelta) <= threshold(next.deltaPeaks, deltaDeviations)) {
            next.sequence = previous.sequence + 1;
            signal = !previous.isPositive;
        }
    }

    next.segmentStart = barIndex;
    next.isPositive = previous.rollingDelta >= 0;
    next.peakDelta = 0;
    next.peakVolume = 0;
    return {next, signal};
}

DetectorResult closeDetector(const ExhaustionDetector& previous, std::int64_t ask, std::int64_t bid, std::int64_t barIndex, Strength strength) {
    const auto updated = updateWindow(previous, ask, bid, lengthFor(strength));
    const auto transition =
        (updated.rollingDelta >= 0) != previous.isPositive ? finishSegment(updated, barIndex, strength) : DetectorResult{updated, std::nullopt};
    auto next = transition.state;

    // 翻转柱的滚动量仅用于新段峰值，不能混入刚刚评估的旧段。
    next.peakDelta = std::max(next.peakDelta, magnitude(next.rollingDelta));
    next.peakVolume = std::max(next.peakVolume, next.rollingVolume);
    return {next, transition.isUp};
}

Signal exhaustionSignal(const DetectorResult& result, const Bar& bar, SignalKind kind) {
    Signal signal;
    signal.id = signalId(kind, result.state.sequence);
    signal.kind = kind;
    signal.startIndex = bar.index;
    signal.endIndex = bar.index;
    signal.createdIndex = bar.index;
    signal.startTimeNs = bar.startTimeNs;
    signal.endTimeNs = bar.endTimeNs - 1;
    signal.isUp = *result.isUp;
    const auto price = signal.isUp ? checkedDifference(bar.lowTicks, MARKER_OFFSET_TICKS) : checkedAdd(bar.highTicks, MARKER_OFFSET_TICKS);
    signal.lowerTicks = price;
    signal.upperTicks = price;
    return signal;
}

} // namespace

ExhaustionResult closeExhaustion(const ExhaustionState& previous, const Bar& bar, Strength strength) {
    const auto volume = closeDetector(previous.volume, bar.askVolume, bar.bidVolume, bar.index, strength);
    const auto count = closeDetector(previous.eventCount, bar.askCount, bar.bidCount, bar.index, strength);
    std::vector<Signal> signals;
    if (volume.isUp.has_value()) {
        signals.push_back(exhaustionSignal(volume, bar, SignalKind::ExhaustionVolume));
    }

    if (count.isUp.has_value()) {
        signals.push_back(exhaustionSignal(count, bar, SignalKind::ExhaustionCount));
    }

    return {{volume.state, count.state}, signals};
}

ExhaustionState resetExhaustion(const ExhaustionState& previous) {
    ExhaustionState next;
    next.volume.sequence = previous.volume.sequence;
    next.eventCount.sequence = previous.eventCount.sequence;
    return next;
}

} // namespace vtracker
