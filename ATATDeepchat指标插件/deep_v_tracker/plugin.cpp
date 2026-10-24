// 文件职责：通过公开 SDK 组装 Tracker 的计算、绘制和参数入口，统一 ABI、取消、错误边界与检查点复制。
#include "calculator.h"
#include "renderer.h"

#include <indicator/plugin.hpp>

SDK_INDICATOR(Calculator, Renderer, readSettings)
