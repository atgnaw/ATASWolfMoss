// 文件职责：定义 V Tracker 的行情、参数、信号和统计值合同；价格统一为整数 tick，时间为 UTC 纳秒，不依赖 SDK。
// 输入按因果顺序交付，已发布值不得原地修改。
#pragma once

#include <cstdint>
#include <vector>

namespace vtracker {

inline constexpr std::int64_t SECOND_NS = 1'000'000'000;
inline constexpr std::int64_t ACCELERATION_EXTENSION = 12;

enum class Strength { Weak, Medium, Strong };
enum class LevelMode { Conservative, Aggressive, Medium };
enum class Side { Unknown, Buy, Sell };
enum class SignalKind : std::uint32_t { Acceleration = 1, ExhaustionVolume, ExhaustionCount, Slowdown, Control, Extreme };

struct Config {
    Strength acceleration{Strength::Medium};
    Strength exhaustion{Strength::Medium};
    Strength slowdown{Strength::Medium};
    Strength absorption{Strength::Medium};
    LevelMode levelMode{LevelMode::Conservative};
    std::int64_t projectionBars{10};
    bool enableAcceleration{true};
    bool enableExhaustion{true};
    bool enableSlowdown{true};
    bool enableLevels{true};
    bool operator==(const Config&) const = default;
};

struct PriceLevel {
    std::int64_t priceTicks{};
    std::int64_t askVolume{};
    std::int64_t bidVolume{};
    std::int64_t totalVolume{};
};

struct Bar {
    std::int64_t index{};
    std::int64_t startTimeNs{};
    std::int64_t endTimeNs{};
    std::int64_t openTicks{};
    std::int64_t highTicks{};
    std::int64_t lowTicks{};
    std::int64_t closeTicks{};
    std::int64_t askVolume{};
    std::int64_t bidVolume{};
    std::int64_t totalVolume{};
    std::int64_t askCount{};
    std::int64_t bidCount{};
    // 升序、价位不重复的完整分价快照；只在收柱时提供，逐笔阶段可为空。
    std::vector<PriceLevel> profile;
    bool hasKnownDirection{true};
    bool isNewSession{};
};

struct Tick {
    std::int64_t barIndex{};
    std::int64_t timeNs{};
    std::int64_t priceTicks{};
    std::int64_t volume{};
    Side side{Side::Unknown};
};

struct Signal {
    std::uint64_t id{};
    SignalKind kind{SignalKind::Acceleration};
    std::int64_t startIndex{};
    std::int64_t endIndex{};
    std::int64_t createdIndex{};
    std::int64_t startTimeNs{};
    std::int64_t endTimeNs{};
    std::int64_t lowerTicks{};
    std::int64_t upperTicks{};
    std::int64_t stopTicks{};
    std::int64_t lifetimeBars{};
    // 偏移只用于绘图，SDK 记录仍锚定实际已观察柱，不向宿主伪造未来柱。
    double startOffset{1.0};
    double endOffset{1.0};
    bool isUp{};
    bool isAsk{};
    bool isAbsorption{};
    bool stopsOnAbove{};
    bool isActive{};
    // false 表示移除当前可变标记；后续可用同一 id 再次显示。
    bool isVisible{true};
};

struct LevelVisibility {
    bool isActive{};
    std::int64_t endIndex{};
    std::int64_t lookbackBars{5};
    std::int64_t bodyLow{};
    std::int64_t bodyHigh{};
};

// 每种输出独立的单调序号；高 8 位区分种类，序号不得因数据中断重用。
[[nodiscard]] std::uint64_t signalId(SignalKind kind, std::uint64_t sequence);
[[nodiscard]] std::int64_t checkedAdd(std::int64_t first, std::int64_t second);
[[nodiscard]] std::int64_t checkedDifference(std::int64_t first, std::int64_t second);
[[nodiscard]] std::int64_t magnitude(std::int64_t value);
void validateConfig(const Config& config);
void validateBar(const Bar& bar);
void validateTick(const Tick& tick);

} // namespace vtracker
