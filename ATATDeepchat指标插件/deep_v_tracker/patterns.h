// 文件职责：定义加速、双路衰竭和可变减速形态的纯值状态与迁移合同，不依赖行情宿主或绘图 SDK。
// 状态仅含有界统计、局部价区和一个当前减速标记；输入按事件顺序串行交付，所有迁移保留旧值。
#pragma once

#include "model.h"
#include "statistics.h"

#include <cstdint>
#include <optional>
#include <vector>

namespace vtracker {

struct AccelerationState {
    Statistics samples;
    bool isInitialized{};
    bool canSample{};
    std::int64_t deadlineNs{};
    std::int64_t bucketVolume{};
    std::int64_t bucketDelta{};
    std::int64_t peakVolume{};
    std::int64_t peakDelta{};
    std::int64_t lowTicks{};
    std::int64_t highTicks{};
    std::int64_t firstBar{};
    std::int64_t lastBar{};
    std::int64_t firstTimeNs{};
    std::int64_t lastTimeNs{};
    std::uint64_t sequence{};
};

struct AccelerationResult {
    AccelerationState state;
    std::optional<Signal> signal;
};

// 每根新柱放行一次统计；桶边界为首笔时间+1秒，跨界笔属于新桶。
[[nodiscard]] AccelerationState openAcceleration(const AccelerationState& previous);
[[nodiscard]] AccelerationResult advanceAcceleration(const AccelerationState& previous, const Tick& tick, Strength strength);

struct ExhaustionDetector {
    std::vector<std::int64_t> volumes;
    std::vector<std::int64_t> deltas;
    Statistics volumePeaks;
    Statistics deltaPeaks;
    std::int64_t rollingVolume{};
    std::int64_t rollingDelta{};
    std::int64_t peakVolume{};
    std::int64_t peakDelta{};

    // CLR 默认段起点是0、符号是负侧，不能改成常见的-1预热初始化。
    std::int64_t segmentStart{};
    bool isPositive{};
    std::uint64_t sequence{};
};

struct ExhaustionState {
    ExhaustionDetector volume;
    ExhaustionDetector eventCount;
};

struct ExhaustionResult {
    ExhaustionState state;
    std::vector<Signal> signals;
};

// 成交量、输入事件笔数是两路独立检测器；仅已收柱输入，无满窗口预热门槛。
[[nodiscard]] ExhaustionResult closeExhaustion(const ExhaustionState& previous, const Bar& bar, Strength strength);

// 输入方向缺失时断开统计连续性；保留已分配序号，不模拟源Reset残留的方向段状态。
[[nodiscard]] ExhaustionState resetExhaustion(const ExhaustionState& previous);

struct SlowdownState {
    Statistics maxima;
    // 从lowTicks开始逐tick连续排列，未成交档位的买卖量均为0。
    std::vector<PriceLevel> levels;
    std::int64_t lowTicks{};
    std::int64_t highTicks{};
    std::int64_t maximumVolume{};
    std::int64_t volumeThreshold{};
    std::int64_t lowProximityCeiling{};
    std::int64_t highProximityFloor{};
    std::int64_t previousExtreme{};
    bool isInitialized{};
    bool hasCurrentPattern{};
    bool hasPendingEvaluation{true};
    std::optional<Signal> marker;
    std::uint64_t sequence{};
};

struct SlowdownResult {
    SlowdownState state;
    std::optional<Signal> signal;
};

// 超过rangeLimitTicks或新会话时采样旧区间，首次初始化不加入空样本；保留旧图标引用。
[[nodiscard]] SlowdownState resetSlowdownRange(const SlowdownState& previous, std::int64_t priceTicks, Strength strength);
[[nodiscard]] SlowdownState advanceSlowdown(const SlowdownState& previous, const Tick& tick, std::int64_t rangeLimitTicks, Strength strength);

// 输入中断后开始全新区间；旧输出由引擎冻结或隐藏，本函数仅保留序号以避免身份复用。
[[nodiscard]] SlowdownState resetSlowdownData(const SlowdownState& previous);

// 收柱总会判断；盘中endSlowdown仅处理脏标记。forceInstance=false时刷新近端3tick的触发边界。
[[nodiscard]] SlowdownResult evaluateSlowdown(const SlowdownState& previous, const Bar& bar, const LevelVisibility& visibility);
[[nodiscard]] SlowdownResult endSlowdown(const SlowdownState& previous, const Bar& bar, const LevelVisibility& visibility, bool forceInstance);

// 吸收区间确认会直接重新显示当前隐藏标记，按源代码不再检查三档形态。
[[nodiscard]] SlowdownResult revealSlowdown(const SlowdownState& previous);

} // namespace vtracker
