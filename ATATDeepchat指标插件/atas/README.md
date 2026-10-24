# Deep-M Effort — ATAS 版 1.0.0

将仓库 `deep_m_effort` 的 C++ 规则移植为 ATAS 原生 C# 指标。原有两个 C++ 目录保持不变。另一个指标见 [Deep-V Tracker 移植说明](DeepVTracker/README.md)；使用 `build.ps1 -Indicator DeepVTracker` 单独构建，或 `-Indicator All` 构建两者。

## 安装

此版本针对 Windows ATAS / .NET 10 构建，使用本机 `ATAS.Indicators.dll` **8.0.15.304** 编译与集成测试。不适用于仍运行 .NET 8 的旧版 ATAS。

1. 解压 `DeepMEffort-ATAS-1.0.0.zip`。
2. ATAS 图表 → 指标设置 → **Add custom indicator / 添加自定义指标**，选择 `WolfMoss.DeepMEffort.dll`。
3. 在指标列表搜索 **Deep-M Effort / 推进效率** 并添加到主图。不同版本可能将自定义 DLL 放在 Custom 分类。
4. 默认计算参数对应 **NQ、Range 40（40 tick）**。指标不会自动更改图表周期。

也可以将 DLL 复制到 `%APPDATA%\ATAS\Indicators`。平台运行期间更新 DLL 后，按主窗口状态栏出现的重新加载按钮；若当前版本没有该入口，重启平台。PDB 仅用于排错，不是安装必需文件；不要复制平台自带的 ATAS/OFT 程序集。

以上安装方法依据 [ATAS 官方开发与安装文档](https://docs.atas.net/en/md_DataFeedsCore_2Docs_2en_20010__BasicIndicator.html)。本项目的构建脚本只在仓库生成产物，不自动安装或操作正在运行的 ATAS。

## 计算规则

```text
Delta        = Ask - Bid
DeltaPercent = abs(Delta) / Volume * 100
Effort       = Delta / (abs(CloseTicks - OpenTicks) + 1)
Elapsed      = floor((下一柱 Time - 本柱 Time).TotalSeconds)
```

Effort 为有符号整数除法，向零截断。开收盘价格按合约真实 TickSize 转成整数 tick，不使用图表显示聚合档位作为 TickSize。

默认同时满足 Delta 占比 10～40%、|Effort|≤14、整数秒≤45，并且 Delta 非负对应 Close≥Open、Delta 负对应 Close<Open，才算合格柱。

至少 2 根同方向合格柱建立待确认段，连续段被后续柱打断后才创建区域。**打断柱也要等下一根柱出现才结算**，图形锚定在打断柱；历史图上看见的位置不是盘中首次可用时间。

默认上行段画绿色下半区间，下行段画紫色上半区间。区域创建后上下边界固定，只延伸右端；停止后保留历史图形。保留原版的完整范围条件、minimumBars=1 反向连续段兼容行为、零量柱处理及最高价初值为 0 的行为。

- 绿色区域：实体下沿严格跌破区域下沿时停止。
- 紫色区域：创建柱 Close<Upper 时，以实体上沿严格超过 Upper 停止；否则以实体下沿严格低于 Upper 停止。
- 影线和刚好等于边界不触发停止；创建柱可以当场停止新区域。
- 默认创建后索引跨度达到 20 停止，右端落在触发柱后一根柱坐标。
- 会话切换使用 ATAS `IsNewSession`，已有区域冻结到新会话首柱，但连续段和 EMA 不重置。
- EMA 默认长度 20，第 22 根已确认柱 Close 为首值，此后 alpha=2/21。EMA 不参与区域资格判断。

## ATAS 数据映射及明确差异

本实现使用 [ATAS IndicatorCandle](https://docs.atas.net/en/classATAS_1_1Indicators_1_1IndicatorCandle.html) 提供的 OHLC、Ask、Bid、Volume、Betweens、Time 和 LastTime。历史加载与实时更新走同一条计算路径，不使用实时 OnNewTrade 另建一套与历史不同的成交统计。

1. **耗时来源**：当前版使用相邻柱 `Time`，不是原 C++ 适配器捕获的逐笔首笔 `eventTimeNs`。Range 图通常以成交开柱，但 ATAS API 只承诺 Time 为柱开盘时间，不能承诺跨平台逐笔时间完全一致。在分钟等时间周期上，它可能是周期边界时间；默认 45 秒过滤会使信号很少。LastTime 只用于校验时间顺序，不能替代下一柱 Time。
2. **收柱确认**：只在下一真实柱出现时结算上一柱。最后一根柱不强制确认，当前柱 EMA 保持上一已确认值。原 TradeVant 版可在单独的收柱通知中先更新 EMA；本版没有使用墙钟猜测时间柱收盘，因此无下一笔时尾部 EMA 更新可能更晚。
3. **成交方向质量**：要求 Ask、Bid、Volume 为非负整数，Ask+Bid=Volume，Betweens=0。未知方向量、量不匹配或小数手数会丢弃未确认段，图上显示提示；已有区域及 EMA 继续维护。不会把小数成交量截断后当成真实整数。零成交量柱遵循原核心的保留段规则。
4. **时间／价格质量**：时间倒序、缺失 Time 或不在合约 tick 网格的开收盘价不参与新区域判断。TickSize 暂未提供时显示提示，元数据变为有效后重建历史。
5. **会话／合约边界**：会话取决于 ATAS 图表的交易时段设置。切换数据提供者、品种或 TickSize 会重建该图表状态。此接口没有每根历史柱的合约身份，不能识别连续合约历史中隐藏的换月；不会凭日期猜测换月，也不调整历史价格。
6. **重算**：计算参数变更重算全部历史；颜色、宽度和显示开关只重绘。最小 Delta 占比大于非零最大值时，清空旧输出并显示配置提示。

两个平台的构柱、数据分类或历史起点不同都可能产生不同区域。本版保留核心规则，但不宣称与 Deepchart 或 TradeVant 的真实行情逐信号一致。指标仅计算和绘图，没有交易执行、网络、声音或弹窗。

## 参数

| 参数 | 默认 | 说明 |
| --- | --- | --- |
| 最小 / 最大 Delta 占比 | 10 / 40 | 0 分别关闭对应界限 |
| 最大 Effort 绝对值 | 14 | 0 关闭 |
| 最大柱耗时 | 45 秒 | 0 关闭；先取整数秒 |
| 最少连续合格柱 | 2 | 1～4096 |
| EMA 长度 | 20 | 1～4096，首值在 length+2 根已确认柱 |
| 区域最大延伸柱跨度 | 20 | 1～4096；界面不提供无限延伸 |
| 保留完整范围的最大段长 | 1 | 0～4096 |
| 区域裁剪比例 | 50% | 0 保留全部；100 只留下边界线 |
| 填充不透明度 | 125 | 0～255 |
| 区域 / EMA 线宽 | 1 / 1 | 1～20 |
| 显示 EMA / 输入提示 | 开 / 开 | 可关闭 |

## 构建与验证

需要 .NET 10 SDK 和已安装的 Windows ATAS。所有依赖来自本机 SDK 和 ATAS 安装目录，无需下载 NuGet 包。

在仓库根目录运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\atas\build.ps1
```

若 ATAS 安装在其他位置：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\atas\build.ps1 -ATASInstallDir 'D:\ATAS Platform'
```

输出为 `artifacts\DeepMEffort\WolfMoss.DeepMEffort.dll` 和 `artifacts\DeepMEffort-ATAS-1.0.0.zip`。包内提供 DLL 的 SHA256。`-SkipTests` 可只构建与打包。

默认构建脚本运行无第三方测试框架的控制台验证程序。当前 **64 项检查**包括：整数正负截断、时间阈值、延迟确认、上下区域裁剪、实体／影线／等号、紫色区域两种停止分支、区域寿命、会话冻结、零量与无效输入、特殊 EMA、真实 ATAS 类型加载、模拟历史／实时回调、1000 次重复更新、参数重算、图形坐标／透明度／裁剪恢复和历史清理。

集成测试引用真实 ATAS 程序集，但行情提供者与绘图上下文是模拟对象。**尚未在实际 ATAS 图表上进行真实行情回放、截图验收或与原 C++ 的同源数据对照。**

## 文件结构

- `DeepMEffort/Core/EffortEngine.cs`：平台无关计算、连续段、EMA、区域维护。
- `DeepMEffort/Core/CandleInput.cs`：ATAS 数据快照到整数 tick／成交量输入的转换。
- `DeepMEffort/DeepMEffortIndicator.cs`：ATAS 回调、收柱去重、会话和重算。
- `DeepMEffort/DeepMEffortIndicator.Settings.cs`：中文参数与外观。
- `DeepMEffort/DeepMEffortIndicator.Rendering.cs`：可见区域绘图和输入提示。
- `tests/DeepMEffort.Tests`：核心与真实程序集集成测试。

ZIP 安装包仅包含运行 DLL、符号及本文；源代码和构建脚本位于仓库的 `atas` 目录。
