// 文件职责：定义 Effort 的纯值计算合同、连续段和区域生命周期，不依赖 SDK、平台或交易执行。
// 这是基于已核对规则的算法重实现，不是作者原始源码。
#pragma once

#include <cstdint>
#include <optional>
#include <vector>

namespace effort {

struct Config {
    double minDeltaPercent{10.0};
    double maxDeltaPercent{40.0};
    double maxDeltaEffort{14.0};
    std::int64_t maxTimeSeconds{45};
    std::int64_t minimumBars{2};
    std::int64_t emaLength{20};
    std::int64_t maxZoneExtensions{20};
    std::int64_t fullRangeMaxBars{1};
    double entryZonePercent{50.0};
};

struct Bar {
    std::int64_t index{};
    double open{};
    double high{};
    double low{};
    double close{};
    // 原生 tick 坐标由适配层提供，不从 double 价格反推。
    std::int64_t openTicks{};
    std::int64_t closeTicks{};
    std::int64_t askVolume{};
    std::int64_t bidVolume{};
    std::int64_t totalVolume{};
    std::int64_t elapsedMicroseconds{};
    bool isNewSession{};
    // 不可靠输入丢弃未确认段，不把数据缺失当成区域信号；已有区域与 EMA 照常推进。
    bool hasKnownAggressor{true};
    bool hasValidElapsed{true};
};

struct Metrics {
    std::int64_t delta{};
    double deltaPercent{};
    std::int64_t effort{};
    std::int64_t elapsedSeconds{};
    bool hasVolume{};
    bool isEligible{};
};

struct EmaState {
    std::int64_t observedBars{};
    std::optional<double> value;
};

struct BarRange {
    double high{};
    double low{};
};

struct PendingSegment {
    double high{};
    double low{};
    std::int64_t barCount{};
    bool isBullish{};
};

struct SegmentState {
    std::int64_t consecutiveBars{};
    bool previousIsBullish{};
    std::optional<PendingSegment> pending;
};

struct Zone {
    std::uint64_t id{};
    std::int64_t createdIndex{};
    // 活动时为 createdIndex，绘制方用 isActive 右延伸；停止后是确定的右端柱坐标。
    std::int64_t endIndex{};
    double lower{};
    double upper{};
    bool isBullish{};
    bool stopsOnAbove{};
    bool isActive{true};
};

enum class ZoneEventKind { Created, Stopped };

enum class ZoneStopReason { None, Boundary, Age, Session, Contract };

struct ZoneEvent {
    ZoneEventKind kind{ZoneEventKind::Created};
    ZoneStopReason reason{ZoneStopReason::None};
    Zone zone;
};

struct State {
    Config config;
    std::int64_t nextBarIndex{};
    SegmentState segment;
    std::vector<BarRange> recentBars;
    EmaState emaState;
    std::vector<Zone> activeZones;
    std::uint64_t nextZoneId{1};
};

struct Update {
    State state;
    Metrics metrics;
    std::optional<double> ema;
    std::vector<ZoneEvent> zoneEvents;
};

// State 由以下纯函数产生后视为不可变；不共享可变资源，调用线程不限。
// 配置百分比为 [0,100]，最小/最大过滤阈值的 0 表示禁用；两个长度必须 >=1。
// 非法配置、OHLC、成交量、时长或不连续柱索引抛 invalid_argument；数值越界抛 overflow_error。
[[nodiscard]] State initialState(const Config& config = {}, std::int64_t firstBarIndex = 0);

// 仅消费已收柱，同一索引不可重入。尾部尚未结束的段不会被强制提交；重算须使用 initialState。
[[nodiscard]] Update onBarClosed(const State& previous, const Bar& bar);

// 独立 EMA 通道供 SDK 在收柱时立即发布；默认长度 20 的首次值出现在第 22 根收柱。
[[nodiscard]] EmaState advanceEma(const EmaState& previous, double close, std::int64_t length);

// 会话开柱或换合约时冻结已有区域，但不改段、EMA 或下一输入索引；原因只允许 Session / Contract。
[[nodiscard]] Update stopExtensions(const State& previous, std::int64_t endIndex, ZoneStopReason reason);

} // namespace effort
