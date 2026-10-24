// 文件职责：定义 Effort 区域、均线和输入缺失提示的值记录；不持有 SDK 回调内存或渲染资源。
#pragma once

#include <cstdint>
#include <variant>

inline constexpr std::uint32_t ZONE_SERIES = 1;
inline constexpr std::uint32_t EMA_SERIES = 2;
inline constexpr std::uint32_t STATUS_SERIES = 3;

struct ZoneMark {
    bool isBullish{};
};

struct EmaMark {};

enum class InputIssue : std::uint32_t { None = 0, UnknownAggressor = 1, ExchangeTime = 2, IncompleteVolume = 4, ContractBoundary = 8 };

struct InputStatus {
    std::uint32_t issues{};
};

using Mark = std::variant<ZoneMark, EmaMark, InputStatus>;
