// 文件职责：严格读取 Effort 的 JSON 参数，拒绝未知字段、非法组合和可能超过宿主记录预算的无限延伸配置。
#include "settings.h"

#include <sdk/settings.hpp>

Settings readSettings(std::string_view json) {
    sdk::SettingsReader reader(json, "Deep-M Effort");
    const Settings defaults;
    const auto& config = defaults.calculation;
    const effort::Config calculation{reader.number("minDeltaPercent", config.minDeltaPercent, {0, 100}),
                                     reader.number("maxDeltaPercent", config.maxDeltaPercent, {0, 100}),
                                     reader.number("maxDeltaEffort", config.maxDeltaEffort, {0, 1'000'000}),
                                     reader.integer("maxTimeSeconds", config.maxTimeSeconds, std::int64_t{0}, std::int64_t{86'400'000}),
                                     reader.integer("minimumBars", config.minimumBars, std::int64_t{1}, MAX_CALCULATION_LENGTH),
                                     reader.integer("emaLength", config.emaLength, std::int64_t{1}, MAX_CALCULATION_LENGTH),
                                     reader.integer("maxZoneExtensions", config.maxZoneExtensions, std::int64_t{1}, MAX_ZONE_EXTENSIONS),
                                     reader.integer("fullRangeMaxBars", config.fullRangeMaxBars, std::int64_t{0}, MAX_CALCULATION_LENGTH),
                                     reader.number("entryZonePercent", config.entryZonePercent, {0, 100})};
    const Settings settings{calculation,
                            reader.color("bullishColor", defaults.bullishColor),
                            reader.color("bearishColor", defaults.bearishColor),
                            reader.integer("fillAlpha", defaults.fillAlpha, std::uint32_t{0}, std::uint32_t{255}),
                            static_cast<float>(reader.number("zoneWidth", defaults.zoneWidth, {1, 20})),
                            reader.boolean("showEma", defaults.showEma),
                            reader.color("emaColor", defaults.emaColor),
                            static_cast<float>(reader.number("emaWidth", defaults.emaWidth, {1, 20}))};
    reader.finish();
    (void)effort::initialState(calculation);
    return settings;
}
