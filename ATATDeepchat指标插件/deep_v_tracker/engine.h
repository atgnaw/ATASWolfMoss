// 文件职责：按开柱、逐笔、批次结束和收柱的因果顺序协调 Tracker 检测器，并维护已确认图形的有限延展。
// 副本共享只读状态；每次调用先完成新值计算再替换状态，失败时保留原状态，适用于 SDK checkpoint 克隆。
#pragma once

#include "model.h"

#include <memory>
#include <vector>

namespace vtracker {

struct EngineState;

class Engine {
  public:
    // rangeTicks 为 Range 图的 N（tick），其余图表为 0。同原版，Range 图的吸收 / 压力区间与减速价区由 N 定出；其余图表的
    // 吸收 / 压力区间按最近 21 根柱的真实波幅自适应（levels.h），减速价区固定 8 tick。
    Engine(const Config& config, std::int64_t rangeTicks);
    // 每根真实柱只打开、关闭一次；新柱前必须关闭旧柱。Bar.profile 只在 closeBar 时要求完整。
    [[nodiscard]] std::vector<Signal> openBar(const Bar& bar);
    [[nodiscard]] std::vector<Signal> onTrade(const Tick& tick, const Bar& currentBar);
    [[nodiscard]] std::vector<Signal> onEnd(const Bar& currentBar);
    [[nodiscard]] std::vector<Signal> closeBar(const Bar& bar);

  private:
    std::shared_ptr<const EngineState> state_;
};

} // namespace vtracker
