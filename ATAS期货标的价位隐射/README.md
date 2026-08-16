# ATAS 期货—现货同分钟价格轴映射 v1.1.0

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

指标 DLL：

```text
src\FuturesReferencePriceAxis\bin\Release\FuturesReferencePriceAxis.dll
```

## 安装

可在 ATAS 指标窗口中使用 **Add custom indicator** 选择 DLL，或将 DLL 复制到：

```text
%APPDATA%\ATAS\Indicators
```

加载指标后搜索：

```text
Futures Reference Price Axis / 期货现货映射轴
```

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
