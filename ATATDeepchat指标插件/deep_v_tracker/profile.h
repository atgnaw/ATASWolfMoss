// 文件职责：定义吸收/压力区间的 POC 与源算法成交量扩展边界；与通用 Value Area 计算保持独立。
#pragma once

#include "model.h"

#include <cstdint>
#include <span>

namespace vtracker {

struct ProfileArea {
    std::int64_t pointOfControl{};
    std::int64_t lowTicks{};
    std::int64_t highTicks{};
};

// bars 按时间升序，指针只在调用内借用；每柱 profile 升序且完整。空分布沿用源算法的零价回退。
// 分价首个插入价用 openTicks 还原；该约定与输入完整逐笔柱一致。
[[nodiscard]] ProfileArea calculateProfileArea(std::span<const Bar* const> bars);

} // namespace vtracker
