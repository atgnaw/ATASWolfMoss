// 文件职责：提供有界滚动窗口与可增删样本统计的纯值运算，保留源实现的绝对值输入和 n-1 方差约定。
#pragma once

#include <cstddef>
#include <vector>

namespace vtracker {

struct Statistics {
    std::vector<double> values;
    double sum{};
    double squaredDeviation{};
    std::size_t totalAdded{};
};

// 只返回新值；调用者对象保持不变。capacity>=1，value有限；自动取绝对值，与源统计类默认一致。
[[nodiscard]] Statistics appendSample(const Statistics& previous, double value, std::size_t capacity);
[[nodiscard]] double mean(const Statistics& state);
[[nodiscard]] double standardDeviation(const Statistics& state);
[[nodiscard]] double threshold(const Statistics& state, double deviations);

} // namespace vtracker
