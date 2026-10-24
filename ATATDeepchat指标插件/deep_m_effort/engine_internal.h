// 文件职责：声明 Effort 插件内部的输入校验与区域维护纯函数，仅供计算实现调用。
#pragma once

#include "engine.h"

namespace effort::detail {

struct ZoneChanges {
    std::vector<Zone> activeZones;
    std::vector<ZoneEvent> events;
};

void validateConfig(const Config& config);
void validateBar(const State& previous, const Bar& bar);
[[nodiscard]] std::int64_t tickDivisor(const Bar& bar);
[[nodiscard]] ZoneChanges maintainZones(const std::vector<Zone>& zones, const Bar& bar, const Config& config);
[[nodiscard]] ZoneChanges freezeZones(const std::vector<Zone>& zones, std::int64_t endIndex, ZoneStopReason reason);

} // namespace effort::detail
