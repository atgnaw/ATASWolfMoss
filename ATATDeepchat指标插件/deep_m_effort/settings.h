// 文件职责：定义 Effort 的计算参数与呈现外观；参数只读共享，数值单位与原规则边界由读取器统一校验。
#pragma once

#include "engine.h"

#include <cstdint>
#include <string_view>

inline constexpr std::int64_t MAX_ZONE_EXTENSIONS = 4096;
inline constexpr std::int64_t MAX_CALCULATION_LENGTH = 4096;

struct Settings {
    effort::Config calculation;
    std::uint32_t bullishColor{0xFF00FF00};
    std::uint32_t bearishColor{0xFF8015E8};
    std::uint32_t fillAlpha{125};
    float zoneWidth{1};
    bool showEma{true};
    std::uint32_t emaColor{0xFFFF5252};
    float emaWidth{1};
};

[[nodiscard]] Settings readSettings(std::string_view json);
