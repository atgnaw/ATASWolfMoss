// 文件职责：将 SDK 逐笔与柱事件转换为 Effort 的纯值输入，管理收柱去重、检查点、合约边界与记录发布。
// 私有状态由串行回调替换；完整复制即检查点，不保存借用字符串，不依赖阶段、墙钟或中间时钟次数。
#pragma once

#include "engine.h"
#include "marks.h"
#include "settings.h"

#include <sdk/calculation.hpp>

#include <cstdint>
#include <map>
#include <optional>

class Calculator final {
  public:
    using Data = Mark;
    static constexpr bool isCloneable = true;

    explicit Calculator(const Settings& settings);
    void on(const SdkTrade& trade);
    void on(const SdkBarUpdate& update, sdk::Records<Mark>& records);
    [[nodiscard]] std::int64_t lastObservedBar() const noexcept;

  private:
    struct VolumeState {
        std::uint64_t ask{};
        std::uint64_t bid{};
        std::uint64_t total{};
        std::int64_t firstExchangeNs{};
        std::int64_t lastExchangeNs{};
        bool hasUnknownAggressor{};
        bool hasValidExchangeTime{true};
        bool isContractPartial{};
    };

    void validatePair(const SdkTrade& trade, const SdkBarUpdate& update) const;
    void consume(const SdkTrade& trade, const SdkBarUpdate& update, sdk::Records<Mark>& records);
    void beginBar(const SdkTrade& trade, const SdkBarUpdate& update, sdk::Records<Mark>& records);
    void closeForSignals(const SdkBar& bar, const SdkTrade& nextTrade, const SdkBar& nextBar, sdk::Records<Mark>& records);
    void closeForEma(const SdkBar& bar, sdk::Records<Mark>& records);
    void changeContract(const SdkTrade& trade, sdk::Records<Mark>& records);
    void beginSession(const SdkTrade& trade, sdk::Records<Mark>& records);
    void apply(const effort::Update& update, const SdkBar& endBar, sdk::Records<Mark>& records);
    void publishStatus(const SdkBar& bar, std::uint32_t issues, sdk::Records<Mark>& records) const;

    const Settings& settings_;
    effort::State state_;
    effort::EmaState emaState_;
    std::optional<SdkTrade> pendingTrade_;
    std::optional<SdkBar> currentBar_;
    std::optional<sdk::Anchor> previousEma_;
    std::optional<std::int64_t> lastEmaBar_;
    std::optional<std::uint64_t> lastCursor_;
    std::optional<std::uint64_t> contractKey_;
    std::optional<std::int64_t> sessionStartNs_;
    std::int64_t tickSizeUnits_{};
    std::int64_t lastVisibleNs_{};
    VolumeState volume_;
    // 只保存仍延伸区域的起点时间；已停止区域留在宿主记录中，不扩大计算器历史状态。
    std::map<std::int64_t, std::int64_t> zoneStartTimes_;
};
