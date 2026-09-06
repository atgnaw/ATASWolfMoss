# ATAS 期货—现货同分钟价格轴映射

这是一个适用于 ATAS 8.x 的自定义指标，在期货主图左侧增加参考标的价格轴：

- NQ / MNQ → QQQ
- ES / MES → SPX（Yahoo 代码 `^GSPC`）

自动模式不会把延迟的参考价格与当前期货价格直接配比。指标会取得参考数据源
分钟 K 的时间戳，再从 ATAS 成交缓存或历史累计成交中寻找**同一分钟**的期货
收盘价：

```text
比例 = 同分钟期货收盘价 ÷ 同分钟 QQQ/SPX 收盘价
参考价格轴 = 期货价格轴 ÷ 比例
```

项目可同时生成两个版本：

- 普通版 `FuturesReferencePriceAxis` v1.1.0：保持原有映射功能和指标类型不变。
- Pro 版 `FuturesReferencePriceAxis.DealerHeatmap` v2.2.0：在映射轴右侧依次增加同宽的 Nightwatch Dealer Heatmap、Dealer GEX、IBKR Option OI 和 Option Premium/Volume。

## 编译

本项目默认使用：

- .NET 10 (`net10.0-windows`)
- `C:\Program Files (x86)\ATAS Platform` 中的 ATAS 8.0.14.395 程序集

```powershell
dotnet build .\FuturesReferencePriceAxis.sln -c Release
```

如果 ATAS 安装在其他位置：

```powershell
dotnet build .\FuturesReferencePriceAxis.sln -c Release `
  -p:ATASInstallDir="D:\Apps\ATAS Platform"
```

Release 构建 Pro 2.2.0 前，需要安装官方 TWS API 10.45 C# 源码。默认目录：

```text
C:\TWS API\source\CSharpClient\client
```

可以先验证本机源码：

```powershell
.\tools\Test-IbApiSource.ps1
```

如果源码安装在其他目录，可传入 `-p:IBApiSourceDir="..."`。官方 C# Client
源码只编译进 Pro DLL，不提交到仓库；换电脑部署时只需复制最终 Pro DLL，
不需要安装或另外复制 `CSharpAPI.dll` 或 `Google.Protobuf.dll`；构建时验证 IB 官方源码，
并将官方 10.45 C# Client 运行库及 protobuf 作为资源封装进同一个 Pro DLL。仅做不连接 IB
的开发/单元测试时，可显式使用：

```powershell
dotnet build .\FuturesReferencePriceAxis.sln -c Release -p:RequireIbApiSource=false
```

指标 DLL：

```text
src\FuturesReferencePriceAxis\bin\Release\FuturesReferencePriceAxis.dll
src\FuturesReferencePriceAxis.DealerHeatmap\bin\Release\FuturesReferencePriceAxis.DealerHeatmap.dll
```

## 安装

可在 ATAS 指标窗口中使用 **Add custom indicator** 选择 DLL，或将 DLL 复制到：

```text
%APPDATA%\ATAS\Indicators
```

加载指标后搜索：

```text
Futures Reference Price Axis / 期货现货映射轴
Futures Reference Price Axis Pro / 期货现货映射轴 Pro（Dealer Heatmap）
```

两个 DLL 是自包含指标，可以同时安装，不需要额外的共享业务 DLL。

## 设置

- `Pair / 映射组合`：自动识别，或强制选择 NQ/QQQ、ES/SPX。
- `Mode / 模式`：自动或手动。
- `Manual ratio / 手动比例`：期货价除以参考价；`0` 表示未配置。自动冷启动失败时也会用它兜底。
- `Refresh interval`：默认每 30 分钟更新。
- `Max quote age`：默认拒绝交易时段内超过 20 分钟的参考数据。QQQ 或
  SPX 休市时例外使用最近一个有效收盘分钟，并与 NQ/MNQ 或 ES/MES
  的同一分钟成交匹配。
- `Show update status`：关闭后不显示状态面板，但后台仍继续更新。
- `Status panel X/Y offset`：以左轴右上方的默认位置为 `0,0` 锚点，
  X 正数向右、负数向左，Y 正数向下、负数向上，单位为像素。面板会自动
  限制在图表可见范围内。
- `UI UTC offset`：状态面板的时区偏移，默认 `0` 即 UTC+0；例如输入
  `8` 显示 UTC+8，输入 `-5` 显示 UTC-5。“尝试时间”和“生效K时间”
  都从内部 UTC 时间戳转换，不影响同分钟成交匹配。
- `Show crosshair mapped price`：默认开启。鼠标在主图上移动时，在左侧
  映射标尺的同一水平位置显示 QQQ/SPX 价格标签；关闭后不影响价格轴。

状态面板显示最近一次尝试状态、尝试时间、当前生效的分钟 K 时间、当时双方
价格和比例。自动更新失败时，最后一次成功比例继续生效，并标记为“已冻结”。

ATAS 冷启动时，指标会等图表历史数据计算完成后再首次请求成交历史。如果数据
连接仍未就绪或历史请求超时，会按 5、10、20、30、60 秒退避并持续自动重试；
成功后恢复正常的半小时刷新周期，无需移除并重新加载指标。

v1.1.0 会让同一进程中的多个图表共享同一标的和同一分钟选择策略的
进行中请求：成功结果缓存 15 秒，失败抑制 5 秒。报价过期和重复校验仍由
每个指标实例按自己的设置独立执行。主源和备用源均失败时，状态行会同时
保留两边的简短诊断。

## Pro Dealer Heatmap 与 Dealer GEX

Pro 版使用 Nightwatch REST 接口读取指定到期日的最新 Dealer Heatmap 5 分钟桶：

- NQ / MNQ 使用 QQQ。
- ES / MES 使用 SPX。
- 美股交易日盘前和 RTH 使用当天 expiration；收盘后、周末和完整休市日使用
  之后最近的 NYSE 开盘日。
- RTH 默认每 5 分钟、数据边界后 20 秒更新；其他时段默认每 60 分钟更新，
  并在下一 RTH 开始时主动唤醒。
- 只绘制 API 返回的稀疏节点。QQQ 每格固定为 1 美元执行价，SPX 每格固定为
  5 美元；缺失节点不会补零、插值或用旧节点填充。
- API 时间字段表示五分钟桶起点；界面中的“数据时间”、数据列顶部和悬停提示
  显示该桶实际读取的最后一分钟，即桶起点加 4 分钟。例如 `09:30` 桶显示为
  `09:34`，原始桶时间仍用于缓存、去重和调度。
- 相同 API key、ticker、expiration 和 5 分钟桶的 Pro 实例共享同一请求。
  `429` 严格服从 `Retry-After`；网络、服务或解析失败时只冻结同 ticker、
  同 expiration 的最后有效帧。

Pro 版还会从 `/v1/derived/dealer-gex/{ticker}/snapshot` 读取最新完成的 0DTE
Dealer GEX 五个关键价位。最终列顺序为“映射轴 → Dealer Heatmap → Dealer GEX”；
关闭 Heatmap 后 Dealer GEX 会直接紧贴映射轴。两项功能均可独立开关，并复用相同
API key、5/60 分钟刷新设置、映射比例和列宽。

Dealer GEX 的五个价位按绝对 GEX 最大值归一化为从左向右的进度条：负值红色、
正值绿色、King 节点白色，条内只显示执行价。Gamma Flip 使用黄色虚线，Call Wall
使用绿色实线，Put Wall 使用红色实线。闭市后保留最后有效桶并显示 `CLOSED`；请求
失败而沿用旧数据时显示 `FROZEN`，Dealer GEX 不使用 `NEXT SESSION` 状态。

Pro 设置组中需要填写 `Nightwatch API key`。该属性在 ATAS 设置面板中以密码
样式遮盖，但 ATAS 仍可能把它明文保存在本地模板或配置中。密钥不会写入代码、
请求 URL、状态文字或异常诊断。未配置密钥时 Dealer Heatmap 和 Dealer GEX 均不发出网络请求，
原有映射功能仍可正常使用。

两个数据列的头部只显示数据时间；状态面板会标明目标、数据 as-of 时间及
`LIVE`、`NEXT SESSION`、`CLOSED`、`FROZEN`、`AUTH FAILED` 或 `RATE LIMITED` 状态，
避免把休盘期间针对下一到期日的旧观测误标为实时 0DTE。

## Pro IBKR 0DTE Option OI 与 Premium/Volume

Pro 2.2.0 的最终列顺序为“映射轴 → Dealer Heatmap → Dealer GEX → Option OI →
Option Premium/Volume”。两个 IBKR 列默认关闭，可独立启用；Nightwatch 和 IB Gateway
任一数据源故障都不会阻止其他列或原映射轴工作。

使用前请确认：

- IB Gateway 已登录并启用 Socket API，建议开启 Read-Only API。
- 账号具有 `OPRA (US Option Exchanges) (L1)` 实时权限。
- Live Gateway 默认端口为 `4001`，请按实际 Gateway 配置修改。
- Client ID 默认 `2210`，不应与其他 API 客户端冲突。插件不请求账户、持仓或订单数据。

新增设置包括：

- `Show Option OI`、`Show Option Premium/Volume`：分别控制两个列，默认关闭。
- `Strike levels`：5–21 奇数档，默认 21（ATM 上下各 10 档）。
- `Flow interval`：1、3、5 或 10 分钟，默认 5。
- `Flow bucket mode`：默认上一固定完成桶，也可选择 Rolling。
- `Trade scope`：默认 `RegularTrades`（generic tick 375），也可选择
  `AllTimeAndSales`（generic tick 233）。
- Gateway host/port/client ID 与插件行情线预算；默认预算 84 条。

QQQ/NQ 只统计 RTH；SPX/ES 按 IB 合约的 `trading_hours` 与 `time_zone_id`
统计 GTH 和 RTH。Flow 仅在交易区段前 2 分钟至结束后 1 分钟保留长期订阅；
OI-only 最多以 8 个合约分批临时订阅。OI 日缓存位于
`%LOCALAPPDATA%\WolfMoss\FuturesReferencePriceAxis\option-oi`。

Call 在行上半部显示为绿色，Put 在下半部显示为红色。柱长以当前列全部可见
Call/Put 的最大值共享归一化；无事件显示 `·`，缺失数据或缺少可用基线显示 `?`，
真实零值显示空柱。标签使用 K/M/B
且不带美元符号；悬停可查看完整 OI、Premium、Volume、桶区间、统计口径、ATM
相对档位和 RTH/GTH 状态。

Rolling 始终计算最近 N 分钟；ATM 与当前接收的执行价范围锁定 N 分钟，
到期后按最新映射价格重新选择（即使 ATM 未变也重新锁定 N 分钟）。
换档后重叠合约保留累计样本；移出范围的合约不再接收新数据，但已有成交
会继续显示，直到自然滚出窗口。过渡期间 Flow 可显示超过设置档数的价位，
这些保留行不增加行情线，柱色变淡，悬停标记 `RETAINED`。
新进入合约的第一笔推送较晚时，统计可确认的成交并以数值后的 `*` 和
悬停 `PARTIAL` 标记部分覆盖。合约重新进入范围或断线重连后建立新的基线，
不跨未观察区间相减。窗口限制在同一交易区段内；新交易区段清除旧区段数据，
初期不足 N 分钟的有效统计也标记 `PARTIAL`，收盘后保留截至收盘的窗口。

IB Gateway live smoke test 默认不会运行。完成正式 Release 构建并登录 Gateway 后，
可显式执行（`spot` 为当前 QQQ/SPX 映射现价）：

```powershell
dotnet run --project .\tests\FuturesReferencePriceAxis.Tests\FuturesReferencePriceAxis.Tests.csproj `
  -c Release --no-build -- --live-ib-options --ticker=SPX --spot=6500 `
  --host=127.0.0.1 --port=4001 --client-id=2210 --levels=5 --seconds=20
```

## 数据与限制

- Yahoo Finance chart JSON 不是 Yahoo Developer Network 承诺稳定的正式 Finance
  API，可能发生 429 限流、超时或格式变化。
- QQQ 自动映射以 Yahoo 主备域为首选；当 Yahoo 在盘前返回 HTTP 200 但分钟
  序列为空、过旧或请求失败时，自动回退 Nasdaq QQQ 分钟图。备用数据同样必须
  通过已结束分钟、正数、未重复和 `Max quote age` 校验，且仍与 NQ/MNQ 的同一
  分钟成交匹配。Nasdaq 的美东墙上时间会按纽约夏令时规则转换为 UTC，再转换为
  ATAS 市场时间；状态面板成功行会标记 `Nasdaq备用源`。
- QQQ 在周末、美东时间 04:00 前、20:00 后或美国股市全日休市时，
  会使用最近一根 **QQQ 与 NQ/MNQ 都处于交易时段** 的有效分钟 K。
  例如周末不会选择周五 QQQ 盘后 19:59 美东分钟，因为 NQ 已于
  17:00 结束本周交易；会回退到 16:59 左右的最后共同分钟。正常交易
  时段内仍严格执行 `Max quote age`，数据源中断时不会误用旧价。
  状态面板会显示 `QQQ最近收盘映射成功`。
- SPX 自动映射同样采用双数据源。Yahoo `query1/query2` 均失败、限流、返回空
  序列或损坏 JSON 时，会自动切换到 MarketWatch/WSJ 的独立 SPX 一分钟图表
  接口；状态面板成功行会标记 `MW备用源`。备用源也只读取最后一个已
  结束、非空且为正数的分钟收盘价，并继续匹配 ES/MES 的同一分钟成交。
- Nasdaq 的公开分钟图表接口不提供 SPX（会返回 `Symbol not exists`），因此
  SPX/ES 的可靠双保险是 **Yahoo + MarketWatch/WSJ**，不是伪装成 Nasdaq 的
  同源数据。
- 所有 HTTP 源统一使用 10 秒超时和 1 MB 响应上限。`429`、`5xx`、
  超时和网络错误使用 5–60 秒退避；损坏 JSON 等确定性错误等待正常
  刷新周期。
- QQQ 有盘前、盘后分钟行情；SPX（`^GSPC`）是现金指数，通常只有美股正常
  交易时段才产生新的分钟 K。SPX 未开盘或休市时，ES/MES 自动更新会读取最近
  一个有效 SPX 收盘分钟 `T`，再读取 ES/MES 在同一 `[T,T+1分钟)` 内的最后
  成交价进行映射。它不会拿旧 SPX 与当前 ES 近似配比。
- ATAS 数据连接必须支持近期成交缓存或历史累计成交请求。若不支持，自动模式
  会保留旧比例或回退手动比例，绝不会改用当前期货价近似。
- 最近一次自动比例仅保存在当前指标实例内。建议始终设置一个合理的
  `Manual ratio` 作为重启、休市或网络故障时的兜底。
- UTC 转 ATAS 市场时间时，若当前 ATAS 偏移可识别为纽约或芝加哥，
  会按目标分钟日期重新计算夏令时；其他时区继续使用当前固定偏移。

## 测试

核心测试不需要连接 ATAS：

```powershell
dotnet run --project .\tests\FuturesReferencePriceAxis.Tests\FuturesReferencePriceAxis.Tests.csproj -c Release
```

Nightwatch live smoke test默认不会运行。需要显式提供临时环境变量并指定参数；
输出只包含 ticker、到期日、时间、节点数和 spot，不会输出密钥：

```powershell
$env:YEHANGSHE_API_KEY = "<temporary key>"
dotnet run --project .\tests\FuturesReferencePriceAxis.Tests\FuturesReferencePriceAxis.Tests.csproj `
  -c Release -- --live-dealer-heatmap --ticker=SPX
Remove-Item Env:\YEHANGSHE_API_KEY
```
