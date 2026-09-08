# 第一批：性能诊断与可重复基线

## 交付状态

- 日期：2026-09-06。
- 第一批开发与离线验证完成；ATAS 人工显示、真实 IB 行情和实盘性能待验收。
- 第二至六批未开始。必须收到用户明确确认后才进入下一批。
- 普通版 1.1.0、Pro 2.2.0；程序集名、指标入口和既有设置保持不变。
- 本批 Pro 构建标识（模块 MVID）：`f6308ff6d3204282a2cdd1ab405d9665`。
- 没有连接真实 IB／Nightwatch，没有下单，没有替换 ATAS 安装目录中的 DLL。

## 本批改动

新增 `Diagnostics / 诊断` 设置组，设置名称 `Performance diagnostics / 性能诊断`
（代码属性 `PerformanceDiagnosticsMode`）：

| 模式 | 行为 |
|---|---|
| Off（默认） | 不采集详细耗时或写文件；保留极少量连接资源基础计数 |
| Summary | 每秒发布一次性能摘要，显示在原状态面板末尾 |
| Record | Summary + 本地后台汇总文件；不记录原始行情 |

`ShowUpdateStatus=false` 时不显示摘要；Record 仍然记录。窗口过矮时减少诊断行，
避免因为新增内容而使整个原状态面板消失；增高图表查看完整摘要。

指标使用 Stopwatch 与有界的 60 秒分桶直方图。计时覆盖本 Pro 的整个 OnRender、
PublishOptionSnapshots 和 OnOptionMarketData 路径；生产者不获取诊断统计锁。
P95 是 2 的幂微秒直方图上界，所以显示为 `P95≤`，不是精确分位数。

后台摘要每秒读取原子计数或不可变值，不获取 IB 订阅管理锁。
现有业务状态栏本身读取行情线的旧路径尚未改变，其锁问题属于第二批。
本批没有改变 Flow 数学规则、ATM 换档、请求频率或订阅生命周期策略。

## 字段解读

| 字段 | 实际含义 |
|---|---|
| Render | 本插件绘制方法耗时，不是整个 ATAS 帧率；无绘制样本时 count=0 |
| Publish | 本指标快照发布方法耗时，包含当前复制／聚合路径 |
| Callback | 本指标处理 IB 回调的耗时，包含其现有同步等待；不代表交易所传输延迟 |
| Events/s | 分发到此指标的回调数；同一个原始事件可能分发给多个图表 |
| IB 发送尝试／取消 | 当前共享连接的发送尝试数，取消属于发送的子集，不能相加；不代表服务端确认 |
| Lines / Consumers | 此共享连接发起的订阅与消费者数量，不是账户所有客户端的总数 |
| Cached samples / OI | 最近快照发布时统计的本指标缓存条目数，不是占用字节数 |
| Gateway replacements | 诊断会话观察到的连接实例替换次数，不是所有连接失败尝试次数 |
| Background tasks | 受监测的刷新循环、诊断采样／写入循环及当前 IB reader；不是进程全部线程／任务 |
| Queue / Market gaps | 第一批没有独立异步业务队列及完整缺口检测，显示 N/A，CSV 留空 |
| Diagnostic samples dropped | 诊断队列或文件失败导致的诊断汇总丢样，不是市场成交丢失 |
| Last error | 已白名单化的最近 IB 错误分类；不是当前连接健康状态，不包含原始错误文本 |

不能将 ATAS 整个进程的 CPU／内存归为本插件独占开销。本批不声称测得单插件总内存。

## 文件、安全与生命周期

目录：`%LOCALAPPDATA%\WolfMoss\FuturesReferencePriceAxis\diagnostics`。

- `perf-时间-运行ID.json`：构建标识与允许记录的起始配置。
- `perf-时间-运行ID-分片.csv`：每秒汇总；列名 snake_case，数值采用不变文化格式。
- 桶模式、间隔、档数、预算或 OI／Flow 开关改变时建立新记录段。
- 每约 10 秒后台批量写入；正常关闭会刷新剩余内容。突然终止程序可能丢失未落盘段。
- 诊断队列最多 120 条，满时不阻塞业务，增加可见的诊断丢样计数。
- 单 CSV 最多 10 MiB；本程序所有诊断文件合计最多 100 MiB，过期保留期限为 7 天。
- 清理仅处理严格匹配本程序命名规则的文件，不递归、不跟随文件链接、不删除其他文件。
- 运行中的记录段受保护。没有足够可清理空间或写入失败时显示 WRITE_ERROR，停止该段写入，业务继续。
- 写入故障修复后切 Off 再切 Record 开始新段。没有自动重试风暴。
- 不记录 API Key、请求头、账户、完整异常对象或原始行情。无需安装额外诊断软件。

## 离线基线

完整结果见同目录 `batch-1-baseline.csv`（48 个配置）。

环境：Intel Core i7-12700H（14 核、20 逻辑处理器），Windows 10.0.26200，.NET 10.0.5，Release，
随机种子 2210，`DOTNET_TieredCompilation=0`。

测量使用构建出的 Pro 实际 `CreateFlowSnapshot` 方法，外围模拟当前发布路径的列表复制；
包括反射测试宿主开销，不是完整 OnRender 或 IB 吞吐测试。
每配置预热 3 次、测量 20 次；固定同一个时间点，测试结果未推进时仍存在的刷新成本。
固定模式输入两小时缓存；Rolling 输入所选窗口长度；每合约每 5 秒一个合成事件。

`contracts=42` 是一个标的 21 档 Call/Put，`84` 是两个标的各 42 个合约；
`consumers` 是每个标的消费者数，因此 84／8 场景是 16 个指标实例的总刷新成本。
`input_events` 包含多消费者的回放副本，不能视为 Gateway 原始流量。

| 模式 | 分钟 | 合约 | 每标的消费者 | 平均刷新 ms | P95 ms | 每刷新分配字节 |
|---|---:|---:|---:|---:|---:|---:|
| Fixed | 5 | 42 | 1 | 1.1633 | 4.3627 | 5,836,366 |
| Fixed | 5 | 84 | 8 | 16.0240 | 19.1284 | 93,355,288 |
| Rolling | 1 | 42 | 1 | 0.0618 | 0.0861 | 116,392 |
| Rolling | 5 | 42 | 1 | 0.0998 | 0.1057 | 309,928 |
| Rolling | 5 | 84 | 8 | 1.7848 | 2.0399 | 4,958,937 |
| Rolling | 10 | 84 | 8 | 3.3696 | 3.7013 | 8,829,724 |

结论：当前固定模式的历史列表复制成本值得优先改造；Rolling 也仍有窗口扫描和临时对象开销。
这些是合成负载下的基线，不是实盘保证，也不是本批已经实现的优化收益。后续对照需要相同环境和负载。

复现（PowerShell，环境变量只对当前会话／子进程生效）：

```powershell
dotnet build .\FuturesReferencePriceAxis.sln -c Release
dotnet run --project .\tests\FuturesReferencePriceAxis.Tests\FuturesReferencePriceAxis.Tests.csproj -c Release --no-build
$env:DOTNET_TieredCompilation = '0'
dotnet run --project .\tests\FuturesReferencePriceAxis.Tests\FuturesReferencePriceAxis.Tests.csproj -c Release --no-build -- --performance-baseline
```

## 自动化验证

现有 51 项测试保留，新总数 57 项。新增测试组覆盖：

- 并发直方图、60 秒过期、P95 上界、Off 计时范围零分配。
- 共享连接速率、连接替换、受监测任务释放和错误文本白名单。
- 三个记录实例、关闭刷新、重复释放、CSV 列结构、文件失败。
- 120 条队列容量和非阻塞溢出、文件保留／容量清理不影响非本程序文件。
- 实际 Pro 属性默认值、回调、绘制和快照入口插桩，30 次快速 Summary／Off 切换。
- 1／3／5／10 分钟的无事件、突发、断线和范围退出确定性回放。
- 没有下一条样本时的 10 秒定时刷新、10 MiB 文件轮转。

测试加载现有 ATAS／WPF 程序集用于真实方法入口验证，但不打开 ATAS、不连接任何数据源。

## 用户验收步骤

1. 保留当前使用的旧 DLL；自行替换候选 Pro DLL 并重新启动 ATAS。本轮没有自动部署。
2. 新设置默认 Off，确认旧列、旧模板和既有设置正常。
3. 将诊断改为 Summary，并开启 ShowUpdateStatus：状态面板末尾应出现 PERF 摘要。
4. 不开 IB 列也能查看绘制耗时；要查看回调／订阅指标，再登录 Gateway 并开启 OI／Flow。
5. 切换 Record，运行至少 15 秒，检查上述目录有 JSON 和非空 CSV；隐藏状态面板后仍应继续写入。
6. 将诊断改回 Off：摘要消失，记录停止；不会关闭 OI／Flow 或改变其业务设置。
7. 可用一张／多张图表各观察数分钟。共享连接指标会在各图表重复显示，不应相加。
8. 有问题时提供问题时间、PERF 截图和对应诊断文件；无需开启 Gateway 逐条 API 消息日志。

待用户验收：真实绘制表现、不同 DPI／窗口尺寸、真实 IB 资源指标、实盘长期运行。
本批之后暂停，不继续第二批。
