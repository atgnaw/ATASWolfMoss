// 文件职责：串行消费真实逐笔并发布 Effort 记录；历史耗时等待下一真实柱首笔撮合时间，EMA 则在已知收柱时独立递推。
#include "calculator.h"

#include <algorithm>
#include <limits>
#include <stdexcept>
#include <utility>

namespace {

constexpr std::uint64_t RECORD_KINDS = 3;
constexpr std::int64_t NANOSECONDS_PER_MICROSECOND = 1000;

std::uint64_t recordId(std::int64_t bar, std::uint32_t kind) {
    const auto maximum = (std::numeric_limits<std::uint64_t>::max() - RECORD_KINDS) / RECORD_KINDS;
    if (bar < 0 || static_cast<std::uint64_t>(bar) > maximum) {
        throw std::overflow_error("Deep-M Effort 记录身份超出范围");
    }

    return static_cast<std::uint64_t>(bar) * RECORD_KINDS + kind;
}

std::uint64_t sumVolume(std::uint64_t first, std::uint64_t second) {
    constexpr auto MAXIMUM = static_cast<std::uint64_t>(std::numeric_limits<std::int64_t>::max());
    if (first > MAXIMUM || second > MAXIMUM - first) {
        throw std::overflow_error("Deep-M Effort 成交量超出有符号 64 位范围");
    }

    return first + second;
}

std::uint32_t issue(bool isPresent, InputIssue value) {
    return isPresent ? static_cast<std::uint32_t>(value) : 0U;
}

std::int64_t elapsedMicroseconds(std::int64_t first, std::int64_t last) {
    if (last < first) {
        return 0;
    }

    const auto elapsed = static_cast<std::uint64_t>(last) - static_cast<std::uint64_t>(first);
    return static_cast<std::int64_t>(elapsed / NANOSECONDS_PER_MICROSECOND);
}

} // namespace

Calculator::Calculator(const Settings& settings) : settings_(settings), state_(effort::initialState(settings.calculation)) {}

std::int64_t Calculator::lastObservedBar() const noexcept {
    return currentBar_ ? currentBar_->index : -1;
}

void Calculator::on(const SdkTrade& trade) {
    if (pendingTrade_
        || trade.size == 0
        || trade.side > SDK_SIDE_SELL
        || trade.context.tickSizeUnits <= 0
        || trade.price % trade.context.tickSizeUnits != 0
        || (lastCursor_ && trade.context.eventCursor < *lastCursor_)) {
        throw std::invalid_argument("Deep-M Effort 逐笔价格、数量、方向或回调顺序无效");
    }

    (void)sumVolume(0, trade.size);
    // symbol 仅在回调内借用；后续配对只需数值身份，检查点中不能保留该地址。
    auto owned = trade;
    owned.context.symbol = {};
    pendingTrade_ = owned;
}

void Calculator::validatePair(const SdkTrade& trade, const SdkBarUpdate& update) const {
    const auto& bar = update.currentBar;
    const bool isSame = currentBar_ && currentBar_->index == bar.index;
    const bool isContractChanged = contractKey_ && *contractKey_ != trade.context.contractKey;
    const bool isTimeBars = sdk::isTimePeriod(update.context.period);
    if (trade.barIndex != bar.index
        || trade.barStartTimeNs != bar.startTimeNs
        || trade.barEndTimeNs != bar.endTimeNs
        || trade.context.eventCursor != update.context.eventCursor
        || trade.context.visibleTimeNs != update.context.visibleTimeNs
        || trade.context.tickSizeUnits != update.context.tickSizeUnits
        || trade.context.contractKey != update.context.contractKey
        || bar.endTimeNs <= bar.startTimeNs
        || bar.lowPrice > std::min(bar.openPrice, bar.closePrice)
        || bar.highPrice < std::max(bar.openPrice, bar.closePrice)
        || trade.price != bar.closePrice
        || (!currentBar_ && bar.index != 0)
        || bar.openPrice % trade.context.tickSizeUnits != 0
        || bar.closePrice % trade.context.tickSizeUnits != 0
        || (isSame
            && (bar.startTimeNs != currentBar_->startTimeNs
                || (isTimeBars ? bar.endTimeNs != currentBar_->endTimeNs : bar.endTimeNs < currentBar_->endTimeNs)
                || bar.revision <= currentBar_->revision))
        || (currentBar_
            && (trade.context.visibleTimeNs < lastVisibleNs_
                || (!isContractChanged && trade.context.tickSizeUnits != tickSizeUnits_)
                || (!isSame && (bar.index != currentBar_->index + 1 || bar.startTimeNs < currentBar_->endTimeNs - (isTimeBars ? 0 : 1)))))) {
        throw std::invalid_argument("Deep-M Effort 逐笔与柱回调的身份、价格或时间不一致");
    }

    (void)sumVolume(0, bar.volume);
}

void Calculator::on(const SdkBarUpdate& update, sdk::Records<Mark>& records) {
    if (pendingTrade_) {
        const auto trade = *pendingTrade_;
        validatePair(trade, update);
        consume(trade, update, records);
        currentBar_ = update.currentBar;
        contractKey_ = trade.context.contractKey;
        tickSizeUnits_ = trade.context.tickSizeUnits;
        lastVisibleNs_ = trade.context.visibleTimeNs;
        lastCursor_ = trade.context.eventCursor;
        pendingTrade_ = std::nullopt;
        return;
    }

    // 收柱没有新成交：只提交一次 EMA，不使用周期结束时间替代撮合时间，也不提前结束连续段。按时间分出的柱在可见时刻到达柱结束时收柱；
    // 由成交开出的柱没有逐笔的柱更新就是收柱，Range XV / Z 的收盘价此时改到边界（修订号随之加一），成交量不变。
    const bool isTimeBars = sdk::isTimePeriod(update.context.period);
    const bool isClose = !isTimeBars || update.context.visibleTimeNs >= update.currentBar.endTimeNs;
    if (currentBar_ && update.currentBar.index == currentBar_->index && isClose) {
        if (update.currentBar.volume != currentBar_->volume
            || (isTimeBars && (update.currentBar.revision != currentBar_->revision || update.currentBar.closePrice != currentBar_->closePrice))) {
            throw std::invalid_argument("Deep-M Effort 未收到对应逐笔的柱修订");
        }

        closeForEma(update.currentBar, records);
        currentBar_ = update.currentBar;
    }
}

void Calculator::consume(const SdkTrade& trade, const SdkBarUpdate& update, sdk::Records<Mark>& records) {
    const bool isNew = !currentBar_ || currentBar_->index != update.currentBar.index;
    const bool isContractChanged = contractKey_ && *contractKey_ != trade.context.contractKey;
    if (isNew || isContractChanged) {
        beginBar(trade, update, records);
    }

    // 原版盘中只显示已完成 EMA 的持值，不再递推；收柱时用同一身份替换本段，保持每柱只计算一次。
    if (isNew && previousEma_) {
        const auto& bar = update.currentBar;
        const sdk::Anchor held{bar.index, bar.startTimeNs, previousEma_->value};
        records.line(recordId(bar.index, EMA_SERIES), EMA_SERIES, *previousEma_, held, EmaMark{});
    }

    const VolumeState next{sumVolume(volume_.ask, trade.side == SDK_SIDE_BUY ? trade.size : 0),
                           sumVolume(volume_.bid, trade.side == SDK_SIDE_SELL ? trade.size : 0),
                           sumVolume(volume_.total, trade.size),
                           volume_.firstExchangeNs,
                           trade.eventTimeNs,
                           volume_.hasUnknownAggressor || trade.side == SDK_SIDE_UNKNOWN,
                           volume_.hasValidExchangeTime && trade.eventTimeNs >= volume_.lastExchangeNs,
                           volume_.isContractPartial};
    if (next.total > update.currentBar.volume) {
        throw std::invalid_argument("Deep-M Effort 逐笔累计量超过 SDK 柱成交量");
    }

    volume_ = next;
}

void Calculator::beginBar(const SdkTrade& trade, const SdkBarUpdate& update, sdk::Records<Mark>& records) {
    const bool isContractChanged = contractKey_ && *contractKey_ != trade.context.contractKey;
    const bool isTimeValid = !currentBar_ || isContractChanged || trade.eventTimeNs >= volume_.lastExchangeNs;
    if (isContractChanged) {
        changeContract(trade, records);
    } else if (currentBar_) {
        closeForEma(*currentBar_, records);
        closeForSignals(*currentBar_, trade, update.currentBar, records);
    }

    const bool isPartial = isContractChanged && currentBar_ && currentBar_->index == trade.barIndex;
    volume_ = VolumeState{0, 0, 0, trade.eventTimeNs, trade.eventTimeNs, false, isTimeValid, isPartial};
    beginSession(trade, records);
}

void Calculator::closeForSignals(const SdkBar& bar, const SdkTrade& nextTrade, const SdkBar& nextBar, sdk::Records<Mark>& records) {
    const bool hasValidTime = volume_.hasValidExchangeTime && nextTrade.eventTimeNs >= volume_.lastExchangeNs;
    const bool hasCompleteVolume = volume_.total == bar.volume && !volume_.isContractPartial;
    const auto issues = issue(volume_.hasUnknownAggressor, InputIssue::UnknownAggressor)
                        | issue(!hasValidTime, InputIssue::ExchangeTime)
                        | issue(!hasCompleteVolume, InputIssue::IncompleteVolume);
    publishStatus(bar, issues, records);
    const effort::Bar input{bar.index,
                            sdk::price(bar.openPrice),
                            sdk::price(bar.highPrice),
                            sdk::price(bar.lowPrice),
                            sdk::price(bar.closePrice),
                            bar.openPrice / tickSizeUnits_,
                            bar.closePrice / tickSizeUnits_,
                            static_cast<std::int64_t>(volume_.ask),
                            static_cast<std::int64_t>(volume_.bid),
                            static_cast<std::int64_t>(bar.volume),
                            elapsedMicroseconds(volume_.firstExchangeNs, nextTrade.eventTimeNs),
                            false,
                            !volume_.hasUnknownAggressor && hasCompleteVolume,
                            hasValidTime};
    apply(effort::onBarClosed(state_, input), nextBar, records);
}

void Calculator::closeForEma(const SdkBar& bar, sdk::Records<Mark>& records) {
    if (lastEmaBar_ && bar.index <= *lastEmaBar_) {
        return;
    }

    const auto next = effort::advanceEma(emaState_, sdk::price(bar.closePrice), settings_.calculation.emaLength);
    if (next.value) {
        const sdk::Anchor to{bar.index, bar.startTimeNs, *next.value};
        records.line(recordId(bar.index, EMA_SERIES), EMA_SERIES, previousEma_.value_or(to), to, EmaMark{});
        previousEma_ = to;
    }

    emaState_ = next;
    lastEmaBar_ = bar.index;
}

void Calculator::changeContract(const SdkTrade& trade, sdk::Records<Mark>& records) {
    if (currentBar_) {
        // 新合约首笔无法给旧合约提供同源的撮合耗时；旧尾柱只完成 EMA，不将其补成区域信号。
        closeForEma(*currentBar_, records);
        publishStatus(*currentBar_, static_cast<std::uint32_t>(InputIssue::ContractBoundary), records);
        apply(effort::stopExtensions(state_, currentBar_->index, effort::ZoneStopReason::Contract), *currentBar_, records);
    }

    state_ = effort::initialState(settings_.calculation, trade.barIndex);
    emaState_ = {};
    previousEma_ = std::nullopt;
    lastEmaBar_ = std::nullopt;
    sessionStartNs_ = std::nullopt;
    zoneStartTimes_ = {};
}

void Calculator::beginSession(const SdkTrade& trade, sdk::Records<Mark>& records) {
    if ((trade.context.flags & SDK_CONTEXT_HAS_TRADING_DAY) == 0) {
        return;
    }

    const auto start = trade.context.tradingDayStartNs;
    if (sessionStartNs_ && *sessionStartNs_ != start) {
        const SdkBar nextBar{.index = trade.barIndex, .startTimeNs = trade.barStartTimeNs};
        apply(effort::stopExtensions(state_, trade.barIndex, effort::ZoneStopReason::Session), nextBar, records);
    }

    sessionStartNs_ = start;
}

void Calculator::apply(const effort::Update& update, const SdkBar& endBar, sdk::Records<Mark>& records) {
    auto nextTimes = zoneStartTimes_;
    for (const auto& event : update.zoneEvents) {
        const auto& zone = event.zone;
        if (event.kind == effort::ZoneEventKind::Created) {
            if (!currentBar_ || zone.createdIndex != currentBar_->index) {
                throw std::logic_error("Deep-M Effort 区域起点与已结算柱不一致");
            }

            nextTimes.emplace(zone.createdIndex, currentBar_->startTimeNs);
        }

        const auto start = nextTimes.at(zone.createdIndex);
        const auto end = zone.isActive ? start : endBar.startTimeNs;
        if (!zone.isActive && zone.endIndex != endBar.index) {
            throw std::logic_error("Deep-M Effort 区域停止点没有对应的真实柱");
        }

        records.band(recordId(zone.createdIndex, ZONE_SERIES),
                     ZONE_SERIES,
                     {zone.createdIndex, start, zone.upper},
                     {zone.endIndex, end, zone.lower},
                     ZoneMark{zone.isBullish},
                     {.extendsRight = zone.isActive, .isScaled = false});
        if (!zone.isActive) {
            nextTimes.erase(zone.createdIndex);
        }
    }

    zoneStartTimes_ = std::move(nextTimes);
    state_ = update.state;
}

void Calculator::publishStatus(const SdkBar& bar, std::uint32_t issues, sdk::Records<Mark>& records) const {
    if (issues != 0) {
        records.point(recordId(bar.index, STATUS_SERIES), STATUS_SERIES, {bar.index, bar.startTimeNs, 0}, InputStatus{issues}, {.isScaled = false});
    }
}
