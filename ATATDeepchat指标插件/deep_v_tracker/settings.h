// 文件职责：保存 Tracker 的计算参数与绘图外观；计算配置独立于 SDK，外观变化可直接替换渲染层。
#pragma once

#include "model.h"

#include <cstdint>
#include <string_view>

struct Settings {
    vtracker::Config calculation;
    std::uint32_t askColor{0xFF32CD32};
    std::uint32_t bidColor{0xFFFF5252};
    std::uint32_t exhaustionColor{0xFF00FFFF};
    std::uint32_t slowdownColor{0xFFFFD700};
    std::uint32_t accelerationColor{0xFF696969};
    std::uint32_t accelerationAlpha{45};
    float controlWidth{2};
    float extremeWidth{1};
    float iconSize{10};
    float fontSize{12};
    bool showLabels{true};
};

[[nodiscard]] Settings readSettings(std::string_view json);
