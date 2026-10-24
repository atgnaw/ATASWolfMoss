// 文件职责：根据实体越界、柱龄和会话边界冻结 Effort 区域，并返回保留历史绘制所需的事件。
#include "engine_internal.h"

#include <algorithm>
#include <stdexcept>

namespace effort::detail {
namespace {

Zone stoppedZone(const Zone& zone, std::int64_t endIndex) {
    auto result = zone;
    result.endIndex = endIndex;
    result.isActive = false;
    return result;
}

ZoneStopReason stopReason(const Zone& zone, const Bar& bar, const Config& config) {
    if (config.maxZoneExtensions != 0 && bar.index - zone.createdIndex >= config.maxZoneExtensions) {
        return ZoneStopReason::Age;
    }

    const auto boundary = zone.isBullish ? zone.lower : zone.upper;
    const bool isCrossed = zone.stopsOnAbove ? std::max(bar.open, bar.close) > boundary : std::min(bar.open, bar.close) < boundary;
    if (isCrossed) {
        return ZoneStopReason::Boundary;
    }
    return ZoneStopReason::None;
}

} // namespace

ZoneChanges maintainZones(const std::vector<Zone>& zones, const Bar& bar, const Config& config) {
    ZoneChanges result;
    result.activeZones.reserve(zones.size());
    for (const auto& zone : zones) {
        const auto reason = stopReason(zone, bar, config);
        if (reason == ZoneStopReason::None) {
            result.activeZones.push_back(zone);
        } else {
            result.events.push_back({ZoneEventKind::Stopped, reason, stoppedZone(zone, bar.index + 1)});
        }
    }

    return result;
}

ZoneChanges freezeZones(const std::vector<Zone>& zones, std::int64_t endIndex, ZoneStopReason reason) {
    if (endIndex < 0 || (reason != ZoneStopReason::Session && reason != ZoneStopReason::Contract)) {
        throw std::invalid_argument("Effort freeze requires a non-negative endpoint and a session or contract reason");
    }

    ZoneChanges result;
    result.events.reserve(zones.size());
    for (const auto& zone : zones) {
        if (endIndex < zone.createdIndex) {
            throw std::invalid_argument("Effort zone cannot end before it starts");
        }

        result.events.push_back({ZoneEventKind::Stopped, reason, stoppedZone(zone, endIndex)});
    }

    return result;
}

} // namespace effort::detail
