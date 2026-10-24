// 文件职责：把 Tracker 信号映射为稳定 SDK 记录，处理可变标记删除、有限延展缓存、换月序号及输入资格提示。
#include "calculator.h"

#include <algorithm>
#include <stdexcept>
#include <utility>

namespace {

constexpr std::uint64_t SEQUENCE_MASK = (std::uint64_t{1} << 56) - 1;
constexpr std::uint64_t STATUS_PREFIX = std::uint64_t{255} << 56;

MarkKind markKind(vtracker::SignalKind kind) {
    switch (kind) {
    case vtracker::SignalKind::Acceleration:
        return MarkKind::Acceleration;
    case vtracker::SignalKind::ExhaustionVolume:
        return MarkKind::ExhaustionVolume;
    case vtracker::SignalKind::ExhaustionCount:
        return MarkKind::ExhaustionTrades;
    case vtracker::SignalKind::Slowdown:
        return MarkKind::Slowdown;
    case vtracker::SignalKind::Control:
        return MarkKind::ControlLevel;
    case vtracker::SignalKind::Extreme:
        return MarkKind::ExtremeLevel;
    }

    throw std::logic_error("Deep-V Tracker 收到未知信号类型");
}

std::uint32_t seriesId(vtracker::SignalKind kind) {
    switch (kind) {
    case vtracker::SignalKind::Acceleration:
        return ACCELERATION_SERIES;
    case vtracker::SignalKind::ExhaustionVolume:
    case vtracker::SignalKind::ExhaustionCount:
        return EXHAUSTION_SERIES;
    case vtracker::SignalKind::Slowdown:
        return SLOWDOWN_SERIES;
    case vtracker::SignalKind::Control:
    case vtracker::SignalKind::Extreme:
        return LEVEL_SERIES;
    }

    throw std::logic_error("Deep-V Tracker 收到未知序列类型");
}

} // namespace

void Calculator::publish(const std::vector<vtracker::Signal>& signals, sdk::Records<Mark>& records) {
    if (signals.empty()) {
        return;
    }

    auto nextActive = activeSignals_;
    auto nextMaximum = maximumSequences_;
    for (const auto& signal : signals) {
        const auto category = static_cast<std::size_t>(signal.kind);
        const auto sequence = signal.id & SEQUENCE_MASK;
        if (category == 0
            || category >= sequenceOffsets_.size()
            || signal.id >> 56 != category
            || sequence == 0
            || sequence > SEQUENCE_MASK - sequenceOffsets_[category]) {
            throw std::overflow_error("Deep-V Tracker 信号序号或类型超出 SDK 记录身份范围");
        }

        const auto mappedSequence = sequence + sequenceOffsets_[category];
        const auto id = vtracker::signalId(signal.kind, mappedSequence);
        publishSignal(signal, id, records);
        nextMaximum[category] = std::max(nextMaximum[category], mappedSequence);
        if (signal.isActive && signal.isVisible) {
            nextActive.insert_or_assign(id, signal);
        } else {
            nextActive.erase(id);
        }
    }

    activeSignals_ = std::move(nextActive);
    maximumSequences_ = nextMaximum;
}

void Calculator::publishSignal(const vtracker::Signal& signal, std::uint64_t id, sdk::Records<Mark>& records) const {
    if (!signal.isVisible) {
        records.remove(id);
        return;
    }

    const auto series = seriesId(signal.kind);
    const bool isRange = signal.kind == vtracker::SignalKind::Acceleration;
    const bool isLevel = signal.kind == vtracker::SignalKind::Control || signal.kind == vtracker::SignalKind::Extreme;
    const auto tickPrice = sdk::price(tickSizeUnits_);
    const sdk::Anchor from{signal.startIndex, signal.startTimeNs, static_cast<double>(signal.lowerTicks) * tickPrice};
    const sdk::Anchor to{signal.endIndex, signal.endTimeNs, static_cast<double>(signal.upperTicks) * tickPrice};
    const Mark mark{
        markKind(signal.kind), isRange || isLevel ? signal.isAsk : signal.isUp, signal.isAbsorption, 0, signal.startOffset, signal.endOffset};
    const sdk::Options options{.extendsRight = signal.isActive, .isScaled = false};
    if (isRange) {
        records.band(id, series, from, to, mark, options);
    } else if (isLevel) {
        records.line(id, series, from, to, mark, options);
    } else {
        records.point(id, series, from, mark, options);
    }
}

void Calculator::publishStatus(std::uint32_t issues, sdk::Records<Mark>& records) const {
    if (issues == 0 || !currentBar_) {
        return;
    }
    if (currentBar_->index < 0 || static_cast<std::uint64_t>(currentBar_->index) >= SEQUENCE_MASK) {
        throw std::overflow_error("Deep-V Tracker 柱序号超出输入资格记录范围");
    }

    const auto id = STATUS_PREFIX | (static_cast<std::uint64_t>(currentBar_->index) + 1);
    const Mark mark{.kind = MarkKind::InputStatus, .issues = issues};
    records.point(id, STATUS_SERIES, {currentBar_->index, currentBar_->startTimeNs, 0}, mark, {.isScaled = false});
}
