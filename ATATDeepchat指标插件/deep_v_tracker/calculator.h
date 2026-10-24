// 文件职责：适配 SDK 逐笔、柱与时钟事件，提供完整分价快照并发布 Tracker 语义记录；不依赖宿主私有模块。
// 回调串行、可迁移线程；检查点复制包含全部数值状态，不保存回调借用指针，计算结果与事件阶段无关。
#pragma once

#include "engine.h"
#include "marks.h"
#include "settings.h"

#include <sdk/calculation.hpp>

#include <array>
#include <cstdint>
#include <map>
#include <optional>
#include <vector>

class Calculator final {
  public:
    using Data = Mark;
    static constexpr bool isCloneable = true;

    explicit Calculator(const Settings& settings);
    static bool sameCalculation(const Settings& before, const Settings& after) noexcept;
    void on(const SdkTrade& trade);
    void on(const SdkBarUpdate& update, sdk::Records<Mark>& records);
    void on(const SdkClockUpdate& update, sdk::Records<Mark>& records);
    [[nodiscard]] std::int64_t lastObservedBar() const noexcept;

  private:
    void validatePair(const SdkTrade& trade, const SdkBarUpdate& update) const;
    void consume(const SdkTrade& trade, const SdkBarUpdate& update, sdk::Records<Mark>& records);
    void beginBar(const SdkTrade& trade, const SdkBar& bar, sdk::Records<Mark>& records);
    void closeBar(sdk::Records<Mark>& records);
    void changeContract(sdk::Records<Mark>& records);
    void publish(const std::vector<vtracker::Signal>& signals, sdk::Records<Mark>& records);
    void publishSignal(const vtracker::Signal& signal, std::uint64_t id, sdk::Records<Mark>& records) const;
    void publishStatus(std::uint32_t issues, sdk::Records<Mark>& records) const;

    const Settings& settings_;
    // 原版在加载时读图表；SDK 创建实例时不给柱周期，第一根柱开出时按它建立，换月时按同一周期重建。改周期由宿主新建实例。
    std::optional<vtracker::Engine> engine_;
    std::optional<SdkBarPeriod> period_;
    std::optional<SdkTrade> pendingTrade_;
    std::optional<SdkBar> currentBar_;
    std::optional<vtracker::Bar> inputBar_;
    std::optional<std::int64_t> lastClosedBar_;
    std::optional<std::int64_t> sessionStartNs_;
    std::optional<std::uint64_t> contractKey_;
    std::optional<std::uint64_t> lastCursor_;
    std::int64_t lastVisibleNs_{};
    std::int64_t tickSizeUnits_{};
    bool isContractPartial_{};

    // 只缓存仍延展的记录。换月冻结它们，分种类推进序号偏移，历史图形留给宿主保存。
    std::map<std::uint64_t, vtracker::Signal> activeSignals_;
    std::array<std::uint64_t, 7> sequenceOffsets_{};
    std::array<std::uint64_t, 7> maximumSequences_{};
};
