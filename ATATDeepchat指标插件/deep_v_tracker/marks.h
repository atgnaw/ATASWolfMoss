// 文件职责：定义 Tracker 的只读图形记录与输入资格原因；只保存可平凡复制的值，不借用行情或宿主内存。
#pragma once

#include <cstdint>

inline constexpr std::uint32_t ACCELERATION_SERIES = 1;
inline constexpr std::uint32_t EXHAUSTION_SERIES = 2;
inline constexpr std::uint32_t SLOWDOWN_SERIES = 3;
inline constexpr std::uint32_t LEVEL_SERIES = 4;
inline constexpr std::uint32_t STATUS_SERIES = 5;

enum class MarkKind : std::uint32_t { Acceleration, ExhaustionVolume, ExhaustionTrades, Slowdown, ControlLevel, ExtremeLevel, InputStatus };

enum class InputIssue : std::uint32_t { UnknownAggressor = 1, IncompleteVolume = 2, ContractBoundary = 4 };

struct Mark {
    MarkKind kind{};
    bool isBullish{};
    bool isAbsorption{};
    std::uint32_t issues{};
    double startOffset{};
    double endOffset{};
};
