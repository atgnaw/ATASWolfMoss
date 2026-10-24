// 文件职责：将 Tracker 的语义记录绘制为加速区域、衰竭与减速符号及吸收/压力水平线；不推进计算状态。
#pragma once

#include "marks.h"
#include "settings.h"

#include <sdk/canvas.hpp>

#include <vector>

class Calculator;

class Renderer final {
  public:
    explicit Renderer(const Settings& settings) : settings_(settings) {}

    [[nodiscard]] std::vector<sdk::Series> series() const;
    void render(const Calculator& model, const sdk::Visible<Mark>& records, const sdk::Canvas& canvas) const;

  private:
    const Settings& settings_;
};
