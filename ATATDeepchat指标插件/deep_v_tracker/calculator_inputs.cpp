// 文件职责：校验真实逐笔与柱的配对关系，按价格 tick 累积成交量和成交记录计数，生成独立的新柱值。
#include "calculator.h"

#include <algorithm>
#include <limits>
#include <stdexcept>
#include <utility>

namespace {

std::int64_t signedVolume(std::uint64_t value) {
    if (value > static_cast<std::uint64_t>(std::numeric_limits<std::int64_t>::max())) {
        throw std::overflow_error("Deep-V Tracker 成交量超出有符号 64 位范围");
    }

    return static_cast<std::int64_t>(value);
}

vtracker::Side sideOf(std::uint32_t side) {
    if (side == SDK_SIDE_BUY) {
        return vtracker::Side::Buy;
    }
    if (side == SDK_SIDE_SELL) {
        return vtracker::Side::Sell;
    }

    return vtracker::Side::Unknown;
}

vtracker::PriceLevel addLevel(const vtracker::PriceLevel& level, const vtracker::Tick& tick) {
    return {tick.priceTicks,
            vtracker::checkedAdd(level.askVolume, tick.side == vtracker::Side::Buy ? tick.volume : 0),
            vtracker::checkedAdd(level.bidVolume, tick.side == vtracker::Side::Sell ? tick.volume : 0),
            vtracker::checkedAdd(level.totalVolume, tick.volume)};
}

std::vector<vtracker::PriceLevel> addTrade(const std::vector<vtracker::PriceLevel>& profile, const vtracker::Tick& tick) {
    const auto at =
        std::lower_bound(profile.begin(), profile.end(), tick.priceTicks, [](const auto& level, auto price) { return level.priceTicks < price; });
    std::vector<vtracker::PriceLevel> next(profile.begin(), at);
    next.push_back(addLevel(at != profile.end() && at->priceTicks == tick.priceTicks ? *at : vtracker::PriceLevel{}, tick));
    next.insert(next.end(), at != profile.end() && at->priceTicks == tick.priceTicks ? at + 1 : at, profile.end());
    return next;
}

vtracker::Bar updatedBar(const vtracker::Bar& before, const SdkBar& bar, const vtracker::Tick& tick, std::int64_t tickSize, bool isPartial) {
    const auto total = vtracker::checkedAdd(before.totalVolume, tick.volume);
    if (total > signedVolume(bar.volume)) {
        throw std::invalid_argument("Deep-V Tracker 逐笔累计量超过 SDK 柱成交量");
    }

    return {bar.index,
            bar.startTimeNs,
            bar.endTimeNs,
            bar.openPrice / tickSize,
            bar.highPrice / tickSize,
            bar.lowPrice / tickSize,
            bar.closePrice / tickSize,
            vtracker::checkedAdd(before.askVolume, tick.side == vtracker::Side::Buy ? tick.volume : 0),
            vtracker::checkedAdd(before.bidVolume, tick.side == vtracker::Side::Sell ? tick.volume : 0),
            total,
            vtracker::checkedAdd(before.askCount, tick.side == vtracker::Side::Buy ? 1 : 0),
            vtracker::checkedAdd(before.bidCount, tick.side == vtracker::Side::Sell ? 1 : 0),
            addTrade(before.profile, tick),
            before.hasKnownDirection && tick.side != vtracker::Side::Unknown && total == signedVolume(bar.volume) && !isPartial,
            before.isNewSession};
}

} // namespace

void Calculator::on(const SdkTrade& trade) {
    if (pendingTrade_
        || trade.size == 0
        || trade.side > SDK_SIDE_SELL
        || trade.context.tickSizeUnits <= 0
        || trade.price % trade.context.tickSizeUnits != 0
        || (lastCursor_ && trade.context.eventCursor < *lastCursor_)) {
        throw std::invalid_argument("Deep-V Tracker 逐笔数量、方向、价格网格或回调顺序无效");
    }

    (void)signedVolume(trade.size);
    // 清除仅在此次回调有效的 symbol 借用，保留配对与检查点需要的数值字段。
    const SdkCalculationContext context{.phase = trade.context.phase,
                                        .instanceId = trade.context.instanceId,
                                        .generation = trade.context.generation,
                                        .eventCursor = trade.context.eventCursor,
                                        .contractKey = trade.context.contractKey,
                                        .visibleTimeNs = trade.context.visibleTimeNs,
                                        .period = trade.context.period,
                                        .tickSizeUnits = trade.context.tickSizeUnits,
                                        .productType = trade.context.productType,
                                        .flags = trade.context.flags,
                                        .symbol = {},
                                        .tradingDayStartNs = trade.context.tradingDayStartNs,
                                        .tradingDayEndNs = trade.context.tradingDayEndNs,
                                        .tradingDayCloseNs = trade.context.tradingDayCloseNs,
                                        .exchangeUtcOffsetMinutes = trade.context.exchangeUtcOffsetMinutes};
    pendingTrade_ = SdkTrade{trade.side,
                             context,
                             trade.price,
                             trade.size,
                             trade.barIndex,
                             trade.barStartTimeNs,
                             trade.barEndTimeNs,
                             trade.eventTimeNs,
                             trade.aggressorOrderId};
}

void Calculator::validatePair(const SdkTrade& trade, const SdkBarUpdate& update) const {
    const auto& bar = update.currentBar;
    const auto tick = trade.context.tickSizeUnits;
    const bool isSame = currentBar_ && currentBar_->index == bar.index;
    const bool isChanged = contractKey_ && *contractKey_ != trade.context.contractKey;
    const bool isTimeBars = sdk::isTimePeriod(update.context.period);
    if (trade.barIndex != bar.index
        || trade.barStartTimeNs != bar.startTimeNs
        || trade.barEndTimeNs != bar.endTimeNs
        || trade.context.eventCursor != update.context.eventCursor
        || trade.context.visibleTimeNs != update.context.visibleTimeNs
        || tick != update.context.tickSizeUnits
        || trade.context.contractKey != update.context.contractKey
        || bar.endTimeNs <= bar.startTimeNs
        || bar.lowPrice > std::min(bar.openPrice, bar.closePrice)
        || bar.highPrice < std::max(bar.openPrice, bar.closePrice)
        || trade.price != bar.closePrice
        || bar.openPrice % tick != 0
        || bar.highPrice % tick != 0
        || bar.lowPrice % tick != 0
        || (isSame && !isChanged && lastClosedBar_ && *lastClosedBar_ == bar.index)
        || (isSame
            && !isChanged
            && (bar.startTimeNs != currentBar_->startTimeNs
                || (isTimeBars ? bar.endTimeNs != currentBar_->endTimeNs : bar.endTimeNs < currentBar_->endTimeNs)
                || bar.revision <= currentBar_->revision))
        || (currentBar_
            && (trade.context.visibleTimeNs < lastVisibleNs_
                || (!isChanged && tick != tickSizeUnits_)
                || (!isSame
                    && (bar.index != currentBar_->index + 1 || (!isChanged && bar.startTimeNs < currentBar_->endTimeNs - (isTimeBars ? 0 : 1))))))) {
        throw std::invalid_argument("Deep-V Tracker 逐笔与柱回调的身份、价格或时间不一致");
    }
    // 阈值按第一根柱的柱周期选定，之后的事件必须是同一周期。
    if (!sdk::samePeriod(trade.context.period, update.context.period) || (period_ && !sdk::samePeriod(trade.context.period, *period_))) {
        throw std::invalid_argument("Deep-V Tracker 的柱周期在实例中途改变");
    }

    (void)signedVolume(bar.volume);
}

void Calculator::consume(const SdkTrade& trade, const SdkBarUpdate& update, sdk::Records<Mark>& records) {
    const bool isChanged = contractKey_ && *contractKey_ != trade.context.contractKey;
    if (!currentBar_ || currentBar_->index != update.currentBar.index || isChanged) {
        beginBar(trade, update.currentBar, records);
    }

    const vtracker::Tick tick{trade.barIndex, trade.eventTimeNs, trade.price / tickSizeUnits_, signedVolume(trade.size), sideOf(trade.side)};
    const auto next = updatedBar(*inputBar_, update.currentBar, tick, tickSizeUnits_, isContractPartial_);
    inputBar_ = next;
    currentBar_ = update.currentBar;
    contractKey_ = trade.context.contractKey;
    lastVisibleNs_ = trade.context.visibleTimeNs;
    lastCursor_ = trade.context.eventCursor;
    publish(engine_->onTrade(tick, next), records);
    // SDK 没有原版批次 OnEnd；以每笔的 Trade→Bar 配对结束为确定边界，播放与重建一致，不由额外时钟驱动。
    publish(engine_->onEnd(next), records);
    const auto unknown = next.askVolume + next.bidVolume != next.totalVolume;
    const auto incomplete = next.totalVolume != signedVolume(update.currentBar.volume) || isContractPartial_;
    const auto issues = (unknown ? static_cast<std::uint32_t>(InputIssue::UnknownAggressor) : 0U)
                        | (incomplete ? static_cast<std::uint32_t>(InputIssue::IncompleteVolume) : 0U)
                        | (isChanged ? static_cast<std::uint32_t>(InputIssue::ContractBoundary) : 0U);
    publishStatus(issues, records);
}
