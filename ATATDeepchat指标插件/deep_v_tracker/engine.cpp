// 文件职责：协调四类 Tracker 信号的因果时序、方向缺失降级及图形延展，保存可共享的不可变计算快照。
// 检测器只依赖领域值；SDK 数据转换、持久记录和图形呈现由独立适配层负责。
#include "engine.h"

#include "levels.h"
#include "patterns.h"

#include <algorithm>
#include <optional>
#include <stdexcept>
#include <utility>

namespace vtracker {

struct EngineState {
    Config config;
    std::int64_t rangeTicks{};
    AccelerationState acceleration;
    ExhaustionState exhaustion;
    SlowdownState slowdown;
    LevelState levels;
    std::vector<Signal> activeSignals;
    std::optional<std::int64_t> openIndex;
    std::optional<Bar> lastClosed;
    std::optional<Bar> accelerationLastClosed;
    std::int64_t accelerationStartTimeNs{};
    std::int64_t accelerationEndTimeNs{};
    bool hasFlowGap{};
};

namespace {

struct EngineUpdate {
    EngineState state;
    std::vector<Signal> signals;
};

void requireOpen(const EngineState& state, const Bar& bar) {
    validateBar(bar);
    if (!state.openIndex || *state.openIndex != bar.index) {
        throw std::invalid_argument("Tracker event must refer to the currently open real bar");
    }
}

EngineUpdate breakFlowContinuity(const EngineState& previous) {
    if (previous.hasFlowGap) {
        return {previous, {}};
    }
    auto next = previous;
    std::vector<Signal> signals;
    if (previous.slowdown.marker && previous.slowdown.marker->isVisible) {
        auto hidden = *previous.slowdown.marker;
        hidden.isVisible = false;
        signals.push_back(hidden);
    }
    next.hasFlowGap = true;
    next.exhaustion = resetExhaustion(previous.exhaustion);
    next.slowdown = resetSlowdownData(previous.slowdown);
    next.levels = resetLevels(previous.levels);
    return {std::move(next), std::move(signals)};
}

void appendSignal(EngineUpdate& update, const Signal& signal) {
    update.signals.push_back(signal);
    if (signal.isActive) {
        update.state.activeSignals.push_back(signal);
    }
}

Signal registerAcceleration(const Signal& candidate, const Bar& registrationBar) {
    auto signal = candidate;
    // manager isrule=true：收盘达到上沿或峰值Delta为负时监控下沿，其余监控上沿。
    signal.stopsOnAbove = registrationBar.closeTicks < signal.upperTicks && signal.isUp;
    signal.stopTicks = signal.stopsOnAbove ? signal.upperTicks : signal.lowerTicks;
    signal.isAsk = signal.isUp;
    return signal;
}

const Bar& accelerationRegistrationBar(const EngineState& state, const Signal& signal, const Bar& currentBar) {
    if (signal.createdIndex == currentBar.index) {
        return currentBar;
    }
    if (!state.accelerationLastClosed || state.accelerationLastClosed->index != signal.createdIndex) {
        throw std::logic_error("Tracker acceleration bucket has no matching observed registration bar");
    }
    return *state.accelerationLastClosed;
}

EngineUpdate maintainSignals(const EngineState& previous, const Bar& bar) {
    auto next = previous;
    std::vector<Signal> active;
    std::vector<Signal> signals;
    active.reserve(previous.activeSignals.size());
    signals.reserve(previous.activeSignals.size());
    const auto bodyLow = std::min(bar.openTicks, bar.closeTicks);
    const auto bodyHigh = std::max(bar.openTicks, bar.closeTicks);
    for (const auto& old : previous.activeSignals) {
        auto signal = old;
        signal.endIndex = bar.index;
        signal.endTimeNs = bar.endTimeNs - 1;
        const bool isCrossed = signal.stopsOnAbove ? bodyHigh > signal.stopTicks : bodyLow < signal.stopTicks;
        signal.isActive = !isCrossed && checkedDifference(bar.index, signal.createdIndex) < signal.lifetimeBars;
        signals.push_back(signal);
        if (signal.isActive) {
            active.push_back(signal);
        }
    }
    next.activeSignals = std::move(active);
    return {std::move(next), std::move(signals)};
}

void evaluateClosedFlow(EngineUpdate& update, const Bar& bar) {
    const auto& config = update.state.config;
    const auto rangeTicks = update.state.rangeTicks;
    if (config.enableExhaustion) {
        const auto exhaustion = closeExhaustion(update.state.exhaustion, bar, config.exhaustion);
        update.state.exhaustion = exhaustion.state;
        update.signals.insert(update.signals.end(), exhaustion.signals.begin(), exhaustion.signals.end());
    }
    if (config.enableSlowdown) {
        const auto slowdown = evaluateSlowdown(update.state.slowdown, bar, levelVisibility(update.state.levels, rangeTicks));
        update.state.slowdown = slowdown.state;
        if (slowdown.signal) {
            appendSignal(update, *slowdown.signal);
        }
    }
    // 可见性依赖窄幅区间，即使用户隐藏水平线也必须计算其状态。
    if (config.enableLevels || config.enableSlowdown) {
        const auto levels = closeLevels(update.state.levels, bar, config, rangeTicks);
        update.state.levels = levels.state;
        if (config.enableLevels) {
            for (const auto& signal : levels.signals) {
                appendSignal(update, signal);
            }
        }
        if (config.enableSlowdown && levels.shouldRevealSlowdown) {
            const auto revealed = revealSlowdown(update.state.slowdown);
            update.state.slowdown = revealed.state;
            if (revealed.signal) {
                appendSignal(update, *revealed.signal);
            }
        }
    }
}

Bar withoutProfile(const Bar& bar) {
    // 只保留注册旧桶信号需要的OHLC，避免每笔复制上一柱的完整分价表。
    auto result = bar;
    result.profile.clear();
    return result;
}

void validateClosedProfile(const Bar& bar) {
    std::int64_t total = 0;
    std::int64_t ask = 0;
    std::int64_t bid = 0;
    for (const auto& level : bar.profile) {
        total = checkedAdd(total, level.totalVolume);
        ask = checkedAdd(ask, level.askVolume);
        bid = checkedAdd(bid, level.bidVolume);
    }
    if (total != bar.totalVolume || ask != bar.askVolume || bid != bar.bidVolume) {
        throw std::invalid_argument("Tracker closed-bar profile must reconcile with bar total, ask and bid volumes");
    }
}

} // namespace

Engine::Engine(const Config& config, std::int64_t rangeTicks) {
    validateConfig(config);
    if (rangeTicks < 0) {
        throw std::invalid_argument("Tracker Range size must be nonnegative");
    }

    EngineState state;
    state.config = config;
    state.rangeTicks = rangeTicks;
    state_ = std::make_shared<const EngineState>(std::move(state));
}

std::vector<Signal> Engine::openBar(const Bar& bar) {
    validateBar(bar);
    if (state_->openIndex || (state_->lastClosed && bar.index != checkedAdd(state_->lastClosed->index, 1))) {
        throw std::invalid_argument("Tracker bars must open consecutively after the prior bar has closed");
    }
    auto next = *state_;
    next.openIndex = bar.index;
    next.hasFlowGap = false;
    next.acceleration = openAcceleration(state_->acceleration);
    if (bar.isNewSession && next.config.enableSlowdown) {
        next.slowdown = resetSlowdownRange(next.slowdown, bar.closeTicks, next.config.slowdown);
    }
    const auto update = bar.hasKnownDirection ? EngineUpdate{std::move(next), {}} : breakFlowContinuity(next);
    state_ = std::make_shared<const EngineState>(update.state);
    return update.signals;
}

std::vector<Signal> Engine::onTrade(const Tick& tick, const Bar& currentBar) {
    requireOpen(*state_, currentBar);
    validateTick(tick);
    if (tick.barIndex != currentBar.index) {
        throw std::invalid_argument("Tracker trade bar index must match its paired bar update");
    }
    auto update = tick.side == Side::Unknown || !currentBar.hasKnownDirection ? breakFlowContinuity(*state_) : EngineUpdate{*state_, {}};
    const auto& config = update.state.config;
    if (config.enableAcceleration) {
        const bool isNewBucket = !update.state.acceleration.isInitialized || tick.timeNs >= update.state.acceleration.deadlineNs;
        const auto acceleration = advanceAcceleration(update.state.acceleration, tick, config.acceleration);
        update.state.acceleration = acceleration.state;
        if (acceleration.signal) {
            const auto& registration = accelerationRegistrationBar(update.state, *acceleration.signal, currentBar);
            auto signal = registerAcceleration(*acceleration.signal, registration);
            // 桶仍按撮合时间计算；记录时间用已观察柱的半开区间，避免撮合延迟把锚点投到别的柱。
            signal.startTimeNs = update.state.accelerationStartTimeNs;
            signal.endTimeNs = update.state.accelerationEndTimeNs;
            appendSignal(update, signal);
        }
        if (isNewBucket) {
            update.state.accelerationStartTimeNs = currentBar.startTimeNs;
        }
        update.state.accelerationEndTimeNs = currentBar.endTimeNs - 1;
    }
    if (config.enableSlowdown && !update.state.hasFlowGap) {
        const auto rangeTicks = update.state.rangeTicks;
        const auto rangeLimit = rangeTicks == 0 ? 8 : checkedAdd(rangeTicks, rangeTicks);
        update.state.slowdown = advanceSlowdown(update.state.slowdown, tick, rangeLimit, config.slowdown);
    }
    state_ = std::make_shared<const EngineState>(std::move(update.state));
    return update.signals;
}

std::vector<Signal> Engine::onEnd(const Bar& currentBar) {
    requireOpen(*state_, currentBar);
    if (!state_->config.enableSlowdown || state_->hasFlowGap) {
        return {};
    }
    auto next = *state_;
    const auto result = endSlowdown(next.slowdown, currentBar, levelVisibility(next.levels, next.rangeTicks), false);
    next.slowdown = result.state;
    state_ = std::make_shared<const EngineState>(std::move(next));
    return result.signal ? std::vector<Signal>{*result.signal} : std::vector<Signal>{};
}

std::vector<Signal> Engine::closeBar(const Bar& bar) {
    requireOpen(*state_, bar);
    if (state_->config.enableLevels || state_->config.enableSlowdown) {
        validateClosedProfile(bar);
    }
    const auto prepared = bar.hasKnownDirection ? EngineUpdate{*state_, {}} : breakFlowContinuity(*state_);
    auto update = maintainSignals(prepared.state, bar);
    update.signals.insert(update.signals.begin(), prepared.signals.begin(), prepared.signals.end());
    // 原顺序为旧图形管理、减速判断、新水平线；新水平线不会在创建回调内立即被旧图形管理器终止。
    if (!update.state.hasFlowGap) {
        evaluateClosedFlow(update, bar);
    }
    update.state.lastClosed = withoutProfile(bar);
    if (update.state.acceleration.isInitialized && update.state.acceleration.lastBar == bar.index) {
        update.state.accelerationLastClosed = update.state.lastClosed;
    }
    update.state.openIndex.reset();
    state_ = std::make_shared<const EngineState>(std::move(update.state));
    return update.signals;
}

} // namespace vtracker
