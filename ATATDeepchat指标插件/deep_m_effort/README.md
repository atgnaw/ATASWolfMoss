# Deep-M Effort (NQ)

TradeVant SDK 6 原生 C++ 指标，包 ID 为 `deep_m_effort`，依据本机 Deepchart 16.5.4 已恢复的计算规则编写。

## 图表

原版专为 NQ 的 40 Range 图设计：厂商说明要求先把图表设成 40 Range，核心参数不要改。在 TradeVant 里把图表周期选成
Range 40（周期面板的 Range 行，40 tick 即 NQ 的 10 点）即可。指标按图表的柱计算：每根柱的主动买卖量、Delta、Effort、
耗时与连续段都取自宿主交付的柱及其逐笔，同原版一样不另造柱，也不区分图表类型。

默认参数沿用原版，是按 Range 40 定的。放到别的周期上也能算，但含义会变：例如 1 分钟图上相邻两根柱的首笔时间差通常接近
一分钟，默认的 `maxTimeSeconds=45` 会滤掉大部分柱，很少或没有区域。参数 `fullRangeMaxBars` 只控制区域是否采用完整高低范围，
不是 Range 图表设置。

TradeVant 的 Range 柱按 ATAS 的规则构造（柱内高低差最多 N 个 tick，新交易日总开新柱），与 Deepchart 的 Range 40 是否逐根
相同没有核对；用同一份逐笔与 Deepchart 的 Range 40 输出逐点对照也还没有做，不能宣称两个平台逐点相同。

## 职责与依赖

| 文件 | 职责与状态 |
| --- | --- |
| `engine.h`、`engine.cpp` | 纯计算契约、整数 Effort、连续段、EMA；只读输入，返回新状态与区域事件 |
| `engine_validation.cpp`、`engine_internal.h` | 核心输入验证与内部约定，不依赖 SDK 或平台 |
| `zone_lifecycle.cpp` | 区域严格越界、最大跨度、交易日及合约边界的停止规则 |
| `calculator.h/.cpp` | SDK 逐笔/柱事件适配；独占累计成交量、未配对逐笔、已收柱 EMA 及区域时间锚点 |
| `marks.h` | 计算与呈现之间的值载荷，不持有外部指针 |
| `renderer.h/.cpp` | 区域绘图、EMA 系列样式、输入不足提示；不参与信号判断 |
| `settings.h/.cpp`、`parameters.schema.json` | 参数范围与 JSON 边界验证；未知字段或非法组合明确报错 |
| `plugin.cpp`、`CMakeLists.txt`、`manifest.json.in` | SDK 导出入口、构建与包注册 |

依赖方向为入口到适配器、适配器到纯核心；渲染器通过 SDK 只读模型和记录。只链接公开 SDK 和本目录源码，
不访问宿主或其他指标的私有实现。实例回调由宿主串行调度，支持独立克隆，不创建线程、不读写文件或网络、不执行下单。

## 计算规则

主动买量为 Ask，主动卖量为 Bid。价格实体先按合约 tick 大小转换为整数 tick，不能按价格点数代替。

```text
delta        = askVolume - bidVolume
deltaPercent = abs(delta) / double(totalVolume) * 100
effort       = delta / (abs(closeTicks - openTicks) + 1)  // 有符号整数除法，向零截断
elapsed      = floor(nonnegativeElapsedMicroseconds / 1,000,000)
```

默认条件同时满足：Delta 占比在 `[10, 40]`，`abs(effort) <= 14`，整数秒 `<= 45`。
Delta 非负时要求 `close >= open`，负数时要求 `close < open`。相关过滤阈值为 0 时关闭该项；EMA 不参与资格判断。

同方向至少 2 根合格柱后只记住待确认区间，等后续柱打断连续段才在打断柱上创建区域。
初始范围扫描最近 `minimumBars` 根物理柱，之后随合格柱扩展。零成交量柱保留连续计数和待确认段，但仍参与物理柱范围及已有区域维护。
`minimumBars=1` 时，反向合格柱仍达到门槛，会保留并扩展待确认区间，同时更新方向；这是原逻辑的兼容行为。

默认上行段画绿色下半区间，下行段画紫色上半区间。段长不超过 `fullRangeMaxBars` 时保留全部高低范围；
否则按 `entryZonePercent` 从对应方向裁剪。最高价初始值沿用原实现的 0，不针对负价格品种改写算法。

区域只延伸右端，不再改变上下边界。停止后保留历史图形：

- 绿色：`min(open, close) < lower` 时停止，影线或刚好等于边界不停止。
- 紫色：创建柱收盘价低于上边界时，以 `max(open, close) > upper` 停止；否则以 `min(open, close) < upper` 停止。
- 创建区域后立即检查创建柱本身，因此区域可能在同次计算中停止。
- 默认索引跨度达到 20 时停止，右端落在该柱之后的下一根柱；交易日切换时冻结到新交易日第一根柱。

EMA 默认长度 20，第 22 根已收柱收盘价作为首值，此后按 `2 / (length + 1)` 递推。
当前未收柱只显示上一 EMA 的水平持值，收柱以同一记录 ID 替换，不能重复递推。只移植 Exponential 模式，不提供源类的其他均线类型。

## SDK 事件映射与边界

- `onTrade` 与其后的 `onBar` 配对，累计真实 BUY/SELL 数量，不用柱涨跌方向猜测主动方。
  SDK 的 cursor 可相等，柱 revision 用来验证同柱的新修订；价格使用 SDK 的 `1e-9` 定点单位。
- 区域信号采用原历史分支的耗时：下一真实柱首笔 `eventTimeNs` 减本柱首笔 `eventTimeNs`。
  因此须等下一柱首笔到达才结算上一柱；末尾没有下一笔时不强行确认区域。名义周期和墙钟不能替代该耗时。
- EMA 在收柱时完成：按时间分出的柱由时钟确认，由成交开出的柱（Range 40 等）在开出下一根之前收到最终柱值；下一柱先出现时
  也完成。按柱索引去重，重复通知不会再次计算。
- 未知主动方向、撮合时间倒序或逐笔累计量与柱量不符时，显示输入提示并丢弃尚未确认的段，不能借无效柱触发区域。
  EMA 与已有区域维护继续。严格违反配对、价格、索引或容量合同则返回 SDK 错误。
- SDK 允许在源无交易所时间时将 `eventTimeNs` 回退为可见时间；插件无法识别该回退，不能宣称此类来源具备原始撮合时间精度。
- 有交易日元数据时，在新柱首笔观察到交易日变化后冻结已创建区域，但不重置连续段或 EMA；没有元数据时不猜交易日边界。
  跨越多个交易日的长周期柱不会在柱内因日界单独截断区域。
- 合约切换冻结旧区，舍弃无法获得同源下一笔时间的旧尾柱信号，重启连续段与 EMA，并给出提示。
  同一根柱内换合约的混合柱不参与信号；不跨合约调整历史价格。
- 参数及外观修改采用宿主常规重建，不提供 fast restyle。失败、取消和输出拒绝通过 SDK 包装层传播。

逐笔源、构柱方式、历史起点或时间语义不同，都可能改变区域，不能据此宣称两个平台逐点相同。
原平台的声音、弹窗及授权服务不在移植范围；这是绘图指标，不是交易策略。

## 参数

| 参数 | 默认 | 可设置范围或含义 |
| --- | --- | --- |
| `minDeltaPercent` / `maxDeltaPercent` | 10 / 40 | 0~100；0 关闭对应界限 |
| `maxDeltaEffort` | 14 | 0~1000000；0 关闭 |
| `maxTimeSeconds` | 45 | 0~86400000；0 关闭 |
| `minimumBars` / `emaLength` | 2 / 20 | 1~4096 |
| `maxZoneExtensions` | 20 | 1~4096；插件不提供无限延伸，以保证单次回调记录预算 |
| `fullRangeMaxBars` / `entryZonePercent` | 1 / 50 | 0~4096 / 0~100 |
| `bullishColor` / `bearishColor` | `0xFF00FF00` / `0xFF8015E8` | ARGB 轮廓颜色 |
| `fillAlpha` / `zoneWidth` | 125 / 1 | 0~255 / 1~20 DIP |
| `showEma` / `emaColor` / `emaWidth` | true / `0xFFFF5252` / 1 | EMA 外观，线宽 1~20 DIP |

填充 Alpha 默认 125 来自原函数实际绘图常量，不把原界面中未在此路径生效的透明度数值冒充实际结果。
纯核心支持 `maxZoneExtensions=0` 的原始无限延伸规则；SDK 参数有意禁止该值，避免超过宿主单回调记录上限。

## 构建

在 `tradevant` 项目目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1 -Preset windows-gui -Target deep_m_effort
cmake -DACTION=check -DPACKAGE=build/windows-gui-provider/bin/indicators/deep_m_effort -P sdk/tools/plugin-sdk.cmake
```

产物位于 `build/windows-gui-provider/bin/indicators/deep_m_effort/`，包含 `module.dll`、`manifest.json` 和 `parameters.schema.json`。
从该构建目录启动的工作台可按现有指标包发现机制加载；无须修改 Deepchart 的任何文件。

真实行情的验证尚未覆盖。
