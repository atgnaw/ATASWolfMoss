// 文件职责：声明吸收/压力窄幅区间的不可变计算状态与收柱接口；只持有行情值，不依赖 SDK 或绘图。
// 旧状态及共享柱节点始终只读。
#pragma once

#include "model.h"
#include "statistics.h"

#include <cstdint>
#include <memory>
#include <vector>

namespace vtracker {

struct BarNode {
    Bar bar;
    std::shared_ptr<const BarNode> previous;

    BarNode(const Bar& value, std::shared_ptr<const BarNode> predecessor);
    // 最后一个所有者释放时迭代回收独占尾链；共享检查点仍持有的节点不受影响。
    ~BarNode() noexcept;
};

struct LevelState {
    // 历史按最新柱在前共享；收柱只增加一个节点，不复制此前所有分价数据。
    std::shared_ptr<const BarNode> history;
    Statistics trueRanges;
    Statistics segmentVolumes;
    Statistics segmentDeltas;
    Statistics barVolumes;
    bool isActive{};
    std::int64_t startIndex{};
    std::int64_t endIndex{};
    std::int64_t nextCandidateIndex{};
    std::int64_t bodyLow{};
    std::int64_t bodyHigh{};
    std::int64_t maintainedLow{};
    std::int64_t maintainedHigh{};
    std::int64_t volume{};
    std::int64_t delta{};
    std::uint64_t signalSequence{};
};

struct LevelUpdate {
    LevelState state;
    std::vector<Signal> signals;
    // 原逻辑发现新区间时会重新显示尚未显现的当前减速图标。
    bool shouldRevealSlowdown{};
};

// 输入为完整已收柱，index 必须连续递增；首根允许非零 index。失败抛异常，旧状态不变。
// 突破柱不计入被评估区间；新信号从本柱收盘后生效，延展由统一图形管理器负责。
// rangeTicks 为 Range 图的 N（tick）：区间至少 4 根、实体跨度不超过 N + N/2、离开超过 N/4 才算突破；其余图表（0）至少 5 根，
// 跨度与离开距离由最近 21 根柱的真实波幅定出，第 22 根柱收盘前不识别区间。两者的量能门槛也不同。
[[nodiscard]] LevelUpdate closeLevels(const LevelState& previous, const Bar& bar, const Config& config, std::int64_t rangeTicks);
[[nodiscard]] LevelVisibility levelVisibility(const LevelState& state, std::int64_t rangeTicks);
// 输入资格中断时丢弃全部行情状态，但保留信号序号，避免覆盖已经发布的历史图形。
[[nodiscard]] LevelState resetLevels(const LevelState& previous);

} // namespace vtracker
