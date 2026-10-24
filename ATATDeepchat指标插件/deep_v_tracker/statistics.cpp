// 文件职责：实现与恢复的 StdDevCalculation 一致的增删 Welford 样本统计，调用者旧窗口始终保持不变。
#include "statistics.h"

#include <cmath>
#include <limits>
#include <stdexcept>

namespace vtracker {

Statistics appendSample(const Statistics& previous, double value, std::size_t capacity) {
    if (capacity == 0 || !std::isfinite(value) || previous.values.size() > capacity) {
        throw std::invalid_argument("Tracker statistics require a finite sample and a valid bounded window");
    }
    if (previous.totalAdded == std::numeric_limits<std::size_t>::max()) {
        throw std::overflow_error("Tracker statistics sample count overflow");
    }
    auto result = previous;
    if (result.values.size() == capacity) {
        const auto removed = result.values.front();
        const auto oldMean = mean(result);
        result.values.erase(result.values.begin());
        result.sum -= removed;
        result.squaredDeviation = result.values.empty() ? 0.0 : result.squaredDeviation - (removed - oldMean) * (removed - mean(result));
    }
    const auto sample = std::abs(value);
    const auto oldMean = mean(result);
    result.values.push_back(sample);
    result.sum += sample;
    result.squaredDeviation += (sample - oldMean) * (sample - mean(result));
    result.totalAdded = previous.totalAdded + 1;
    if (!std::isfinite(result.sum) || !std::isfinite(result.squaredDeviation)) {
        throw std::overflow_error("Tracker statistics overflow");
    }
    return result;
}

double mean(const Statistics& state) {
    return state.values.empty() ? 0.0 : state.sum / static_cast<double>(state.values.size());
}

double standardDeviation(const Statistics& state) {
    return state.values.size() < 2 ? 0.0 : std::sqrt(std::abs(state.squaredDeviation) / static_cast<double>(state.values.size() - 1));
}

double threshold(const Statistics& state, double deviations) {
    // 源 GetAvgPlusDev 均值为零时直接返回零，不继续乘标准差。
    const auto average = mean(state);
    return average == 0.0 ? 0.0 : average + deviations * standardDeviation(state);
}

} // namespace vtracker
