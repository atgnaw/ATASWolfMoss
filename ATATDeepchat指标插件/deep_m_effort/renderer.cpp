// 文件职责：绘制已确认 Effort 区域并显示输入资格提示；配色与透明度只影响外观，不参与算法或交易执行。
#include "renderer.h"

#include <algorithm>
#include <cmath>
#include <string>
#include <variant>

namespace {

void drawZone(const sdk::Canvas& canvas, const sdk::Record<Mark>& record, std::int64_t latestBar, const Settings& settings) {
    const auto& zone = std::get<ZoneMark>(record.data);
    const auto endBar = record.extendsRight ? std::max(record.to.bar, latestBar) : record.to.bar;
    const auto left = canvas.x(static_cast<double>(record.from.bar));
    const auto right = canvas.x(static_cast<double>(endBar));
    const auto upper = canvas.y(ZONE_SERIES, record.from.value);
    const auto lower = canvas.y(ZONE_SERIES, record.to.value);
    if (!std::isfinite(left) || !std::isfinite(right) || !std::isfinite(upper) || !std::isfinite(lower)) {
        return;
    }

    const auto stroke = zone.isBullish ? settings.bullishColor : settings.bearishColor;
    const auto fill = (stroke & 0x00FFFFFFU) | (settings.fillAlpha << 24);
    canvas.rectangle({left, std::min(upper, lower), right, std::max(upper, lower)}, fill, stroke, settings.zoneWidth, record.id);
}

void drawStatus(const sdk::Visible<Mark>& records, const sdk::Canvas& canvas) {
    std::uint32_t issues = 0;
    std::size_t count = 0;
    for (const auto& record : records) {
        const auto* status = std::get_if<InputStatus>(&record.data);
        const auto bar = static_cast<double>(record.from.bar);
        if (status && bar >= canvas.context().firstVisibleBar && bar <= canvas.context().lastVisibleBar) {
            issues |= status->issues;
            ++count;
        }
    }

    if (count == 0) {
        return;
    }

    std::string reasons;
    const auto append = [&](InputIssue value, const char* text) {
        if ((issues & static_cast<std::uint32_t>(value)) != 0) {
            reasons += reasons.empty() ? text : std::string("、") + text;
        }
    };
    append(InputIssue::UnknownAggressor, "主动方向缺失");
    append(InputIssue::ExchangeTime, "撮合时间回退");
    append(InputIssue::IncompleteVolume, "逐笔量不完整");
    append(InputIssue::ContractBoundary, "换月尾柱待定");
    const auto text = "Deep-M Effort：" + std::to_string(count) + " 根柱未判定区域（" + reasons + "）";
    constexpr double INSET = 6;
    constexpr double HEIGHT = 18;
    constexpr std::uint32_t COLOR = 0xFFD39128;
    const auto& plot = canvas.plot();
    canvas.text(
        {plot.left + INSET, plot.top + INSET, plot.right, plot.top + INSET + HEIGHT}, text, sdk::font(canvas.fontFamily(), 10), COLOR, SDK_TEXT_LEFT);
}

} // namespace

std::vector<sdk::Series> Renderer::series() const {
    constexpr auto READOUTS = SDK_SERIES_AUTOSCALE | SDK_READOUT_LATEST_AXIS | SDK_READOUT_CROSSHAIR_POINT | SDK_READOUT_HOVER_VALUE;
    return {sdk::Series::objects(ZONE_SERIES, "Effort 区域").layer(SDK_LAYER_BELOW_PRICE).extension(SDK_EXTEND_REPLAY).decimals(2),
            sdk::Series::line(EMA_SERIES, "Effort EMA(" + std::to_string(settings_.calculation.emaLength) + ")")
                .unit("价格")
                .layer(SDK_LAYER_ABOVE_PRICE)
                .readouts(settings_.showEma ? READOUTS : 0U)
                .decimals(2),
            sdk::Series::objects(STATUS_SERIES, "输入资格").axis(SDK_AXIS_SCREEN).layer(SDK_LAYER_FOREGROUND)};
}

sdk::Style Renderer::style(const Mark&) const {
    return {settings_.showEma ? settings_.emaColor : 0U, settings_.emaWidth};
}

void Renderer::render(const Calculator& model, const sdk::Visible<Mark>& records, const sdk::Canvas& canvas) const {
    const auto below = canvas.on(SDK_LAYER_BELOW_PRICE);
    below.clipped(canvas.plot(), [&] {
        for (const auto& record : records) {
            if (std::holds_alternative<ZoneMark>(record.data)) {
                drawZone(below, record, model.lastObservedBar(), settings_);
            }
        }
    });

    const auto foreground = canvas.on(SDK_LAYER_FOREGROUND);
    foreground.clipped(canvas.plot(), [&] { drawStatus(records, foreground); });
}
