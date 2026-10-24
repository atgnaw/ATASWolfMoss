// 文件职责：协调 Tracker 的因果事件顺序、收柱去重、交易日与合约边界；中间时钟不推进逐笔统计。
#include "calculator.h"

#include <algorithm>
#include <stdexcept>

namespace {

vtracker::Bar emptyBar(const SdkBar& bar, std::int64_t tickSize, bool isNewSession) {
    return {.index = bar.index,
            .startTimeNs = bar.startTimeNs,
            .endTimeNs = bar.endTimeNs,
            .openTicks = bar.openPrice / tickSize,
            .highTicks = bar.highPrice / tickSize,
            .lowTicks = bar.lowPrice / tickSize,
            .closeTicks = bar.closePrice / tickSize,
            .isNewSession = isNewSession};
}

// 原版只有一种 Range 图（柱内高低差最多 N tick），对应柱周期 RANGE，按 N 定阈值；Range X / XV / Z、成交量、Delta 与按时间分柱
// 都按原版的其余图表处理（0）。
std::int64_t rangeTicks(const SdkBarPeriod& period) noexcept {
    return period.kind == SDK_PERIOD_RANGE ? period.value : 0;
}

// 收柱确认里的柱与已收到的一致；由成交开出的柱（movesClose）的 Range XV / Z 收柱时收盘价与高低点可以改到边界，修订号随之加一。
bool unchangedBar(const SdkBar& before, const SdkBar& after, bool movesClose) {
    if (movesClose) {
        return before.index == after.index
               && before.startTimeNs == after.startTimeNs
               && before.endTimeNs == after.endTimeNs
               && before.openPrice == after.openPrice
               && before.volume == after.volume;
    }

    return before.index == after.index
           && before.startTimeNs == after.startTimeNs
           && before.endTimeNs == after.endTimeNs
           && before.openPrice == after.openPrice
           && before.highPrice == after.highPrice
           && before.lowPrice == after.lowPrice
           && before.closePrice == after.closePrice
           && before.volume == after.volume
           && before.revision == after.revision;
}

} // namespace

Calculator::Calculator(const Settings& settings) : settings_(settings) {}

bool Calculator::sameCalculation(const Settings& before, const Settings& after) noexcept {
    return before.calculation == after.calculation;
}

std::int64_t Calculator::lastObservedBar() const noexcept {
    return currentBar_ ? currentBar_->index : -1;
}

void Calculator::on(const SdkBarUpdate& update, sdk::Records<Mark>& records) {
    if (pendingTrade_) {
        const auto trade = *pendingTrade_;
        validatePair(trade, update);
        consume(trade, update, records);
        pendingTrade_ = std::nullopt;
        return;
    }

    // 无逐笔的更新只允许宿主确认已收到的柱收盘；不能拿 OHLC 伪造成交流与分价数据。由成交开出的柱没有逐笔的柱更新就是收柱。
    const bool isTimeBars = sdk::isTimePeriod(update.context.period);
    if (!currentBar_ || !unchangedBar(*currentBar_, update.currentBar, !isTimeBars)) {
        throw std::invalid_argument("Deep-V Tracker 柱更新缺少对应逐笔，不能用 OHLC 代替成交明细");
    }

    if (!isTimeBars || update.context.visibleTimeNs >= currentBar_->endTimeNs) {
        currentBar_ = update.currentBar;
        closeBar(records);
    }
}

void Calculator::on(const SdkClockUpdate& update, sdk::Records<Mark>& records) {
    if (pendingTrade_) {
        throw std::invalid_argument("Deep-V Tracker 在逐笔与柱配对完成前收到时钟");
    }

    // 仅确认真实柱的结束；空闲秒不造桶、重复时钟不重复收柱，clone 后额外时钟不改变后续信号。
    if (currentBar_ && update.isAvailable && sdk::isTimePeriod(update.context.period) && update.virtualTimeNs >= currentBar_->endTimeNs) {
        closeBar(records);
    }
}

void Calculator::beginBar(const SdkTrade& trade, const SdkBar& bar, sdk::Records<Mark>& records) {
    const bool isChanged = contractKey_ && *contractKey_ != trade.context.contractKey;
    const bool isPartial = isChanged && currentBar_ && currentBar_->index == bar.index;
    if (isChanged) {
        changeContract(records);
    } else {
        closeBar(records);
    }

    if (!engine_) {
        engine_.emplace(settings_.calculation, rangeTicks(trade.context.period));
        period_ = trade.context.period;
    }

    const bool hasTradingDay = (trade.context.flags & SDK_CONTEXT_HAS_TRADING_DAY) != 0;
    const bool isNewSession = hasTradingDay && (!sessionStartNs_ || *sessionStartNs_ != trade.context.tradingDayStartNs);
    sessionStartNs_ = hasTradingDay ? std::optional{trade.context.tradingDayStartNs} : std::nullopt;
    tickSizeUnits_ = trade.context.tickSizeUnits;
    isContractPartial_ = isPartial;
    inputBar_ = emptyBar(bar, tickSizeUnits_, isNewSession);
    currentBar_ = bar;
    publish(engine_->openBar(*inputBar_), records);
    if (isChanged) {
        publishStatus(static_cast<std::uint32_t>(InputIssue::ContractBoundary), records);
    }
}

void Calculator::closeBar(sdk::Records<Mark>& records) {
    if (!inputBar_ || (lastClosedBar_ && *lastClosedBar_ == inputBar_->index)) {
        return;
    }

    publish(engine_->closeBar(*inputBar_), records);
    lastClosedBar_ = inputBar_->index;
}

void Calculator::changeContract(sdk::Records<Mark>& records) {
    // 旧合约价格不可与新合约拼成区间；活动图形在最后一根旧柱处冻结，既有历史仍可查阅。
    for (const auto& [id, signal] : activeSignals_) {
        const vtracker::Signal stopped{signal.id,
                                       signal.kind,
                                       signal.startIndex,
                                       currentBar_->index,
                                       signal.createdIndex,
                                       signal.startTimeNs,
                                       currentBar_->startTimeNs,
                                       signal.lowerTicks,
                                       signal.upperTicks,
                                       signal.stopTicks,
                                       signal.lifetimeBars,
                                       signal.startOffset,
                                       signal.endOffset,
                                       signal.isUp,
                                       signal.isAsk,
                                       signal.isAbsorption,
                                       signal.stopsOnAbove,
                                       false,
                                       signal.isVisible};
        publishSignal(stopped, id, records);
    }

    activeSignals_ = {};
    sequenceOffsets_ = maximumSequences_;
    engine_.emplace(settings_.calculation, rangeTicks(*period_));
    inputBar_ = std::nullopt;
    lastClosedBar_ = std::nullopt;
    sessionStartNs_ = std::nullopt;
}
