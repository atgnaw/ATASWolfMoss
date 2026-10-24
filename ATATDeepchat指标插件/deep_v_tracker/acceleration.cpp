// 文件职责：以首笔对齐的一秒桶检测成交量加速，保留先采样再比较和首桶PinT初始化的移植边界。
// 只创建区域，后续延伸和停止由引擎统一处理。
#include "patterns.h"

#include <algorithm>
#include <cstddef>

namespace vtracker {
namespace {

constexpr std::size_t ACCELERATION_WINDOW = 50;

double deviationsFor(Strength strength) {
    switch (strength) {
    case Strength::Weak:
        return 3.25;
    case Strength::Strong:
        return 6.5;
    default:
        return 4.75;
    }
}

AccelerationState startBucket(const AccelerationState& previous, const Tick& tick) {
    auto next = previous;
    next.isInitialized = true;
    next.deadlineNs = checkedAdd(tick.timeNs, SECOND_NS);

    // 源代码首桶把PinT同时写入量和Delta；后续桶才从0开始，不能当作笔误修正。
    next.bucketVolume = previous.isInitialized ? 0 : tick.priceTicks;
    next.bucketDelta = next.bucketVolume;
    next.lowTicks = tick.priceTicks;
    next.highTicks = tick.priceTicks;
    next.firstBar = tick.barIndex;
    next.lastBar = tick.barIndex;
    next.firstTimeNs = tick.timeNs;
    next.lastTimeNs = tick.timeNs;
    return next;
}

Signal accelerationSignal(const AccelerationState& state) {
    Signal signal;
    signal.id = signalId(SignalKind::Acceleration, state.sequence);
    signal.kind = SignalKind::Acceleration;
    signal.startIndex = state.firstBar;
    signal.endIndex = state.lastBar;
    signal.createdIndex = state.lastBar;
    signal.startTimeNs = state.firstTimeNs;
    signal.endTimeNs = state.lastTimeNs;
    signal.lowerTicks = state.lowTicks;
    signal.upperTicks = state.highTicks;
    signal.startOffset = 0.5;
    signal.endOffset = 1.0;
    signal.isUp = state.peakDelta >= 0;
    signal.isActive = true;
    signal.lifetimeBars = ACCELERATION_EXTENSION;
    return signal;
}

AccelerationResult finishBucket(const AccelerationState& previous, Strength strength) {
    if (!previous.canSample || previous.peakVolume == 0) {
        return {previous, std::nullopt};
    }

    auto next = previous;
    next.samples = appendSample(previous.samples, static_cast<double>(previous.peakVolume), ACCELERATION_WINDOW);
    std::optional<Signal> signal;

    // 候选本身进入50项统计后才比较；峰值可来自更早桶，但区域仍使用当前桶的价区。
    if (static_cast<double>(previous.peakVolume) >= threshold(next.samples, deviationsFor(strength))) {
        next.sequence = previous.sequence + 1;
        signal = accelerationSignal(next);
    }

    next.peakVolume = 0;
    next.peakDelta = 0;
    next.canSample = false;
    return {next, signal};
}

AccelerationState addTick(const AccelerationState& previous, const Tick& tick) {
    auto next = previous;
    next.bucketVolume = checkedAdd(previous.bucketVolume, tick.volume);
    const auto delta = tick.side == Side::Buy ? tick.volume : tick.side == Side::Sell ? -tick.volume : 0;
    next.bucketDelta = checkedAdd(previous.bucketDelta, delta);
    next.lowTicks = std::min(previous.lowTicks, tick.priceTicks);
    next.highTicks = std::max(previous.highTicks, tick.priceTicks);
    next.lastBar = tick.barIndex;
    next.lastTimeNs = tick.timeNs;
    if (next.bucketVolume > previous.peakVolume) {
        next.peakVolume = next.bucketVolume;
        next.peakDelta = next.bucketDelta;
    }

    return next;
}

} // namespace

AccelerationState openAcceleration(const AccelerationState& previous) {
    auto next = previous;
    next.canSample = true;
    return next;
}

AccelerationResult advanceAcceleration(const AccelerationState& previous, const Tick& tick, Strength strength) {
    if (!previous.isInitialized) {
        return {addTick(startBucket(previous, tick), tick), std::nullopt};
    }
    if (tick.timeNs < previous.deadlineNs) {
        return {addTick(previous, tick), std::nullopt};
    }

    const auto completed = finishBucket(previous, strength);
    return {addTick(startBucket(completed.state, tick), tick), completed.signal};
}

} // namespace vtracker
