// 文件职责：声明 Effort 区域、EMA 与数据状态序列；按只读记录绘制区域，标准 EMA 折线交给宿主。
#pragma once

#include "calculator.h"
#include "marks.h"
#include "settings.h"

#include <sdk/canvas.hpp>

#include <vector>

class Renderer final {
  public:
    explicit Renderer(const Settings& settings) : settings_(settings) {}

    [[nodiscard]] std::vector<sdk::Series> series() const;
    [[nodiscard]] sdk::Style style(const Mark& mark) const;
    void render(const Calculator& model, const sdk::Visible<Mark>& records, const sdk::Canvas& canvas) const;

  private:
    const Settings& settings_;
};
