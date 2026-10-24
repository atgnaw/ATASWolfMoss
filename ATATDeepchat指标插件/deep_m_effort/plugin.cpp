// 文件职责：以 SDK 标准组装器导出 Effort 指标函数表，统一异常边界、取消、实例复制及身份握手。
#include "calculator.h"
#include "renderer.h"

#include <indicator/plugin.hpp>

SDK_INDICATOR(Calculator, Renderer, readSettings)
