# MNQ/NQ、MES/ES 绘图同步可行性调查

调查日期：2026-10-15。核对本机 `C:\Program Files (x86)\ATAS Platform` 中版本为 **8.0.15.304** 的程序集及 ATAS 官方文档。

结论：水平价位的跨图同步显示有明确的公开 API 实现路径。原生绘图对象的完整双向编辑同步存在内部接口路径，值得开发验证原型，但尚未完成运行验证，不能承诺所有对象均可稳定同步。

## 已确认的事实

1. 官方 `IChart.DrawingObjectsListInfo` 返回 `IDrawingObjectsListInfo`，后者只有 `HorizontalLines`、`HorizontalChannels` 两个枚举属性，没有通用原生对象增删、变化订阅接口。本机版本与文档一致。
2. 本机 `ChartData.DrawingObjectsListCollection.HorizontalLines` 每次创建新的 `HorizontalLine`，复制价格、颜色、线宽、线型。它不是原生对象引用；对返回对象赋值不能回写原生线。没有对象 ID、文字、时间锚点。水平射线也转成这种摘要，无法保持射线起点。
3. `HorizontalChannels` 在本机实现中包含 HighlightY 和向右延伸的 RectangleObj 的价格区间摘要，不能读取任意矩形的完整时间范围。
4. 本机内部 `DrawingObjectsCollection.GetAllObjects` 通过 instrument.UniqueName、instrument.UniqueClass、ChartId 查询对象。现有机制按品种身份区分，不能指望设置 Global 就自动建立 MNQ/NQ 的品种别名配对。
5. `OFT.Platform.Core.dll` 内存在完整原生对象基础设施：
   - `ChartData.DrawingObjects`、`DrawingObjectsStateChanged`。
   - `DrawingObjectsCollection.GetAllObjects`、`LoadDrawingObjects`、`AddGlobalDrawingObject`、`Delete`、`SaveDrawingObjects`、`Undo`。
   - `DrawingObject.Id`、`InstrumentUniqName`、`ChartId`、`ChartCore`、`Clone()`、`ResetId()`、`Init()`，以及完成和设置窗口事件。
   - `IGlobalTradingObjectsStore` 的 Get、Add、Load、Save、Delete、Undo。

第 5 项是安装包内部类型，不属于已确认受官方支持的指标 SDK。即使成员为 public，也不能视为有兼容性保证的插件协议。实际对象获取入口中存在私有字段和混淆名称，适配应按版本检查并在不兼容时停止同步。

本次只读取程序集并保存分析资料，没有在运行中的 ATAS 中添加、修改或删除绘图。没有发布或安装同步插件。

## 可选实现路径

| 路径 | 能做到什么 | 主要限制 |
|---|---|---|
| 公开 API 镜像 | 两张图各挂一个指标；读取本图原生水平价位，向同组图发布快照并自定义绘制；新增、移动、删除反映在对侧显示 | 镜像不是可原生编辑对象；不支持完整趋势线、射线起点、任意矩形或所有文字设置 |
| 插件自己的绘图工具 | 插件维护对象 ID、时间和价格锚点，并在两侧提供鼠标编辑与持久化 | 需要使用插件工具；不能自动接管全部已有原生对象；交互与撤销需要自行实现 |
| 原生对象桥接 | 通过内部对象集合复制并配对原生对象，目标是在任一侧编辑都同步到另一侧 | 最接近需求，但图表实例访问、事件覆盖、线程、保存、撤销及跨版本兼容尚待验证 |

指标 `TrendLines`、`Rectangles`、`Labels` 是指标的绘制集合，不应误当成用户工具栏所画全部原生对象的管理接口。

## 建议原型范围

以独立指标 DLL 实现，先要求同一 ATAS 进程内的两张图各加载一个实例，保存到图表模板后复用。不要在没有验证全局加载入口前承诺“安装一次便自动接管所有打开图表”。

1. 只配对同到期月份的 NQ ↔ MNQ、ES ↔ MES；连续合约、回放/实时、不同数据源另设显式组，避免错误合并。
2. 价格默认原值复制，例如 NQ 的 22000 水平线映射到 MNQ 的 22000。微型合约是合约乘数缩小，不是指数价格除以 10；两个独立市场的实时成交价格仍可有差异。
3. 先验证原生水平线，再逐项加入水平射线、趋势线、矩形。包含时间的对象按时间与价格映射，不复制 K 线编号或屏幕像素；不同周期、成交量图和 Range 图需处理目标锚点落在哪根 K 线上。
4. 使用同步组、独立原生对象 ID 和持久化配对表。防止 A→B→A 循环、重复对象，以及 Global 在同品种多个图上的多重上报。
5. 首次连接合并现有对象时不以“对侧不存在”为删除依据；删除只作用于已确认配对的对象。关闭图表或临时断开不等于删除全部对象。
6. 两边同时编辑需要修订号与冲突规则。所有原生修改遵守图表线程约束；先核实事件覆盖，必要时增加低频状态比对。
7. 重启恢复时核对存储、对象 ID 和配对表，保证不重复创建、不恢复已删除对象。仅事件存在不能证明拖动、颜色修改、撤销等全部变化都能收到通知。
8. 成交量分布、VWAP、TPO 等含品种数据计算的对象后置：相同锚点可能在两张图上算出不同结果，需要定义同步的是锚点、参数还是计算输出。

## 原型必须通过的验收

- 两个方向的新建、拖动、样式修改、删除和撤销。
- 已有对象导入、同价不同对象区分、Global 对象去重。
- 多张同品种图、不同周期图和非时间图的锚点正确性。
- 关闭一张图、换品种/合约、重新打开、保存工作区并重启。
- 回放隔离、缺少历史数据、版本不匹配时停止同步。
- 大量对象和连续拖动时的延迟、CPU 与内存表现。

目前这些验收均未执行。本调查证明的是接口现状和开发路径，不是已验证成品。

## 来源与复核位置

- [ATAS 绘图对象管理](https://help.atas.net/en/support/solutions/articles/72000602378)：Global、复制粘贴及对象管理功能。
- [IDrawingObjectsListInfo](https://docs.atas.net/en/interfaceATAS_1_1Indicators_1_1IDrawingObjectsListInfo.html)：公开读取范围。
- [HorizontalLine](https://docs.atas.net/en/classATAS_1_1Indicators_1_1Drawing_1_1HorizontalLine.html)：公开价格及样式字段。
- [IChart 完整成员列表](https://docs.atas.net/en/interfaceATAS_1_1Indicators_1_1IChart-members.html)。
- [指标内置图形集合](https://docs.atas.net/en/md_DataFeedsCore_2Docs_2en_20060__EmbeddedGraphicShapes.html)。
- [CME Micro E-mini FAQ](https://www.cmegroup.com/articles/faqs/micro-e-mini-equity-index-futures-frequently-asked-questions.html)：微型合约与对应 E-mini 的合约乘数关系。

本机只读分析输出位于 `.artifacts/drawing-sync-research/`，包括 ChartData.cs、DrawingObjectsCollection.cs、DrawingObject.cs、IGlobalTradingObjectsStore.cs。该目录已被仓库忽略，不应随插件发行。旧 `.artifacts/ChartData.cs` 不作为本次版本结论依据。
