// 文件职责：按只读记录绘制 Tracker 各类形态与资格提示；原图形的 index+1 仅在 X 变换时使用，不发布未来柱。
#include "renderer.h"

#include "calculator.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <string>
#include <string_view>

namespace {

constexpr double TEXT_INSET = 4;
constexpr double STATUS_INSET = 6;
constexpr double STATUS_HEIGHT = 18;
constexpr std::uint32_t STATUS_COLOR = 0xFFD39128;

bool isVisible(const sdk::Canvas& canvas, const SdkRect& bounds) {
    return std::isfinite(bounds.left)
           && std::isfinite(bounds.right)
           && std::isfinite(bounds.top)
           && std::isfinite(bounds.bottom)
           && sdk::intersects(bounds, canvas.plot());
}

std::uint32_t directionColor(const Mark& mark, const Settings& settings) {
    return mark.isBullish ? settings.askColor : settings.bidColor;
}

void hover(const sdk::Canvas& canvas, const sdk::Record<Mark>& record, SdkRect bounds, std::uint32_t color, std::string_view title) {
    const auto& plot = canvas.plot();
    const SdkRect clipped{
        std::max(bounds.left, plot.left), std::max(bounds.top, plot.top), std::min(bounds.right, plot.right), std::min(bounds.bottom, plot.bottom)};
    if (clipped.right <= clipped.left || clipped.bottom <= clipped.top || !sdk::intersects(clipped, plot)) {
        return;
    }

    canvas.hit({.kind = SDK_HIT_RECT,
                .flags = SDK_HIT_HOVER,
                .objectId = record.id,
                .rect = clipped,
                .colorArgb = color,
                .title = {title.data(), static_cast<std::uint32_t>(title.size())}});
}

double endBar(const sdk::Record<Mark>& record, std::int64_t latestBar) {
    const auto index = record.extendsRight ? std::max(record.to.bar, latestBar) : record.to.bar;
    return static_cast<double>(index) + record.data.endOffset;
}

void drawAcceleration(const sdk::Canvas& canvas, const sdk::Record<Mark>& record, std::int64_t latestBar, const Settings& settings) {
    const auto left = canvas.x(static_cast<double>(record.from.bar) + record.data.startOffset);
    const auto right = canvas.x(endBar(record, latestBar));
    const auto first = canvas.y(record.series, record.from.value);
    const auto last = canvas.y(record.series, record.to.value);
    const SdkRect bounds{left, std::min(first, last), right, std::max(first, last)};
    if (!isVisible(canvas, bounds)) {
        return;
    }

    const auto color = settings.accelerationColor;
    const auto fill = (color & 0x00FFFFFFU) | (settings.accelerationAlpha << 24);
    canvas.rectangle(bounds, fill, color, 1, record.id);
    hover(canvas, record, bounds, color, "Acceleration · 成交量加速");
}

void drawPattern(const sdk::Canvas& canvas, const sdk::Record<Mark>& record, const Settings& settings) {
    const auto x = canvas.x(static_cast<double>(record.from.bar) + record.data.startOffset);
    const auto y = canvas.y(record.series, record.from.value);
    const auto radius = static_cast<double>(settings.iconSize) / 2;
    const SdkRect bounds{x - radius, y - radius, x + radius, y + radius};
    if (!isVisible(canvas, bounds)) {
        return;
    }

    const bool isSlowdown = record.data.kind == MarkKind::Slowdown;
    const auto color = isSlowdown ? settings.slowdownColor : settings.exhaustionColor;
    const auto direction = record.data.isBullish ? 1.0 : -1.0;
    const std::array triangle{
        SdkPoint{x, y - direction * radius}, SdkPoint{x - radius, y + direction * radius}, SdkPoint{x + radius, y + direction * radius}};
    if (isSlowdown || record.data.kind == MarkKind::ExhaustionTrades) {
        canvas.polygonOutline(triangle, color, isSlowdown ? 2.0 : 1.0, record.id);
    } else {
        canvas.polygon(triangle, color, record.id);
    }

    const std::string_view title = isSlowdown                                       ? "Slowdown · 减速（可移动或消失）"
                                   : record.data.kind == MarkKind::ExhaustionTrades ? "Exhaustion · 成交记录计数衰竭"
                                                                                    : "Exhaustion · 成交量衰竭";
    hover(canvas, record, bounds, color, title);
}

void drawLevel(const sdk::Canvas& canvas, const sdk::Record<Mark>& record, std::int64_t latestBar, const Settings& settings) {
    const bool isControl = record.data.kind == MarkKind::ControlLevel;
    const auto width = isControl ? settings.controlWidth : settings.extremeWidth;
    if (width <= 0) {
        return;
    }

    const auto left = canvas.x(static_cast<double>(record.from.bar) + record.data.startOffset);
    const auto right = canvas.x(endBar(record, latestBar));
    const auto y = canvas.y(record.series, record.from.value);
    const SdkRect bounds{left, y - TEXT_INSET, right, y + TEXT_INSET};
    if (!isVisible(canvas, bounds)) {
        return;
    }

    const auto color = directionColor(record.data, settings);
    canvas.line({left, y}, {right, y}, color, width, record.id, record.data.isAbsorption ? SDK_STROKE_DASH : SDK_STROKE_SOLID);
    const std::string label = std::string(record.data.isAbsorption ? "A" : "P") + (isControl ? "C" : "E");
    if (settings.showLabels) {
        const auto& plot = canvas.plot();
        const auto labelLeft = std::max(left, plot.left) + TEXT_INSET;
        canvas.text({labelLeft, y - settings.fontSize - TEXT_INSET, plot.right, y - TEXT_INSET},
                    label,
                    sdk::font(canvas.fontFamily(), settings.fontSize),
                    color,
                    SDK_TEXT_LEFT | SDK_TEXT_BOTTOM,
                    record.id);
    }

    hover(canvas, record, bounds, color, label + (record.data.isAbsorption ? " · 吸收" : " · 压力"));
}

void drawStatus(const sdk::Visible<Mark>& records, const sdk::Canvas& canvas) {
    std::uint32_t issues{};
    for (const auto& record : records) {
        const auto bar = static_cast<double>(record.from.bar);
        if (record.data.kind == MarkKind::InputStatus && bar >= canvas.context().firstVisibleBar && bar <= canvas.context().lastVisibleBar) {
            issues = issues | record.data.issues;
        }
    }

    if (issues == 0) {
        return;
    }

    const auto has = [issues](InputIssue value) { return (issues & static_cast<std::uint32_t>(value)) != 0; };
    const auto text = std::string("Deep-V Tracker：")
                      + (has(InputIssue::UnknownAggressor) ? "主动方向缺失；" : "")
                      + (has(InputIssue::IncompleteVolume) ? "逐笔量不完整；" : "")
                      + (has(InputIssue::ContractBoundary) ? "换月重置；" : "")
                      + "相关统计已重新预热";
    const auto& plot = canvas.plot();
    canvas.text({plot.left + STATUS_INSET, plot.top + STATUS_INSET, plot.right, plot.top + STATUS_INSET + STATUS_HEIGHT},
                text,
                sdk::font(canvas.fontFamily(), 10),
                STATUS_COLOR);
}

} // namespace

std::vector<sdk::Series> Renderer::series() const {
    return {sdk::Series::objects(ACCELERATION_SERIES, "Acceleration · 加速").layer(SDK_LAYER_BELOW_PRICE).extension(SDK_EXTEND_REPLAY),
            sdk::Series::objects(EXHAUSTION_SERIES, "Exhaustion · 衰竭").layer(SDK_LAYER_ABOVE_PRICE),
            sdk::Series::objects(SLOWDOWN_SERIES, "Slowdown · 减速").layer(SDK_LAYER_ABOVE_PRICE),
            sdk::Series::objects(LEVEL_SERIES, "Absorption / Pressure · 吸收 / 压力").layer(SDK_LAYER_ABOVE_PRICE).extension(SDK_EXTEND_REPLAY),
            sdk::Series::objects(STATUS_SERIES, "输入资格").axis(SDK_AXIS_SCREEN).layer(SDK_LAYER_FOREGROUND)};
}

void Renderer::render(const Calculator& model, const sdk::Visible<Mark>& records, const sdk::Canvas& canvas) const {
    const auto below = canvas.on(SDK_LAYER_BELOW_PRICE);
    below.clipped(canvas.plot(), [&] {
        for (const auto& record : records) {
            if (record.data.kind == MarkKind::Acceleration) {
                drawAcceleration(below, record, model.lastObservedBar(), settings_);
            }
        }
    });

    const auto above = canvas.on(SDK_LAYER_ABOVE_PRICE);
    above.clipped(canvas.plot(), [&] {
        for (const auto& record : records) {
            if (record.data.kind == MarkKind::ControlLevel || record.data.kind == MarkKind::ExtremeLevel) {
                drawLevel(above, record, model.lastObservedBar(), settings_);
            } else if (record.data.kind != MarkKind::Acceleration && record.data.kind != MarkKind::InputStatus) {
                drawPattern(above, record, settings_);
            }
        }
    });

    const auto foreground = canvas.on(SDK_LAYER_FOREGROUND);
    foreground.clipped(canvas.plot(), [&] { drawStatus(records, foreground); });
}
