// 文件职责：严格读取 Tracker 参数并验证数值范围和枚举；未知字段失败。Range 图的阈值取自图表的柱周期，不是参数。
#include "settings.h"

#include <sdk/settings.hpp>

namespace {

vtracker::Strength strength(sdk::SettingsReader& reader, std::string_view key, vtracker::Strength fallback) {
    return reader.choice(
        key, fallback, {{"weak", vtracker::Strength::Weak}, {"medium", vtracker::Strength::Medium}, {"strong", vtracker::Strength::Strong}});
}

vtracker::Config readCalculation(sdk::SettingsReader& reader) {
    const vtracker::Config defaults;
    return {strength(reader, "acceleration", defaults.acceleration),
            strength(reader, "exhaustion", defaults.exhaustion),
            strength(reader, "slowdown", defaults.slowdown),
            strength(reader, "absorption", defaults.absorption),
            reader.choice("levelMode",
                          defaults.levelMode,
                          {{"conservative", vtracker::LevelMode::Conservative},
                           {"medium", vtracker::LevelMode::Medium},
                           {"aggressive", vtracker::LevelMode::Aggressive}}),
            reader.integer("projectionBars", defaults.projectionBars, std::int64_t{1}, std::int64_t{1024}),
            reader.boolean("enableAcceleration", defaults.enableAcceleration),
            reader.boolean("enableExhaustion", defaults.enableExhaustion),
            reader.boolean("enableSlowdown", defaults.enableSlowdown),
            reader.boolean("enableLevels", defaults.enableLevels)};
}

} // namespace

Settings readSettings(std::string_view json) {
    sdk::SettingsReader reader(json, "Deep-V Tracker");
    const Settings defaults;
    const Settings result{readCalculation(reader),
                          reader.color("askColor", defaults.askColor),
                          reader.color("bidColor", defaults.bidColor),
                          reader.color("exhaustionColor", defaults.exhaustionColor),
                          reader.color("slowdownColor", defaults.slowdownColor),
                          reader.color("accelerationColor", defaults.accelerationColor),
                          reader.integer("accelerationAlpha", defaults.accelerationAlpha, std::uint32_t{0}, std::uint32_t{255}),
                          static_cast<float>(reader.number("controlWidth", defaults.controlWidth, {0, 10})),
                          static_cast<float>(reader.number("extremeWidth", defaults.extremeWidth, {0, 10})),
                          static_cast<float>(reader.number("iconSize", defaults.iconSize, {4, 40})),
                          static_cast<float>(reader.number("fontSize", defaults.fontSize, {6, 32})),
                          reader.boolean("showLabels", defaults.showLabels)};
    reader.finish();
    vtracker::validateConfig(result.calculation);
    return result;
}
