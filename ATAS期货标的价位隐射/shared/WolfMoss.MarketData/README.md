# 标准行情事件与历史接口（schema 1）

本模块只提供数据库无关契约和可选的后台记录边界。
**当前 Pro 没有历史录制开关，不创建数据库，不启动真实行情历史写入，不读取历史来恢复实时基线。**
`PerformanceDiagnosticsMode.Record` 仍只记录性能汇总，不是行情录制。

可独立导入 `WolfMoss.MarketData.props`，不依赖 IB、ATAS、WPF 或具体数据库。
IB 模块已经自动导入它；同一工程不要重复导入。源码内使用相同版本，运行时各插件独立。

## 接口与所有权

| 接口／类型 | 契约 |
| --- | --- |
| IMarketEventSink.TryPublish | 立即返回 Accepted、Disabled、Congested 或 Faulted；不在回调里等磁盘 |
| DisabledMarketEventSink | 始终 Disabled；没有队列／任务／文件 |
| BufferedMarketEventSink | 接收入队、单后台任务批量调用 writer，暴露状态与累计记录缺口 |
| IMarketEventBatchWriter | 未来存储适配器实现异步批量写入；必须遵守取消，自己管理 I/O 资源 |
| IHistoricalDataReader | 按来源、种类、ticker、到期日、con_id、时间和成交口径查询，支持分页和取消 |
| DisabledHistoricalDataReader | 明确返回 Disabled，不把没有存储误报为查询成功但没有成交 |

`MarketEventHub` 是每个消费程序集自己的源码内接入点。生产默认没有附着的记录器；
快速禁用分支连事件对象都不构造。只有未来宿主明确创建 `BufferedMarketEventSink` 并调用
`MarketEventHub.Attach` 才有后台任务。一次只能有一个记录宿主，不能为每张图表分别 Attach。
Attach 返回异步释放句柄；先停止入口再等待剩余批量写入。旧句柄重复释放不会关闭新记录段。
失效的 writer 会关闭采集对象构造，仍需宿主释放旧句柄后才能换一个 writer。

未来源码内接入示意（本工程没有运行这段真实录制配置）：

```csharp
// writer must be supplied by a future, explicitly enabled storage adapter.
await using var sink = new BufferedMarketEventSink(writer);
await using var recording = MarketEventHub.Attach(sink);
// Normal shared IB/Nightwatch intake now offers normalized events to this sink.
// Do not attach inside OnRender, or once per chart.
```

运行示例仅在测试内：`MarketHistoryTests.MemoryStore` 为最多 4096 条的内存 writer／reader；
`SlowStore` 模拟慢写入、取消和故障。它们不编译进 Pro。

## 事件身份与时间

- `SchemaVersion=1`。未来写入格式若使用 JSON，应统一 snake_case 字段与不变文化数值；
  当前没有固定数据库表或磁盘序列化协议。
- `RecordingRunId + ReceiveSequence` 是一个记录段的唯一身份，序号可有缺口。
  不是交易所成交编号，不用于推断交易所漏单；并发生产者的队列到达顺序不保证等于序号顺序。
- `SourceStreamId` 标识来源连接／供应商采集流；IB `ConnectionGeneration` 标识此模块的连接实例。
  不包含 Key、Key 指纹、账户或完整网络地址。
- `ObservationGeneration` 为 IB 实际接收请求 ID，订阅替换后改变；不是 ATAS 图表或 ATM 锁定段。
- 期权身份包含 con_id、ticker、expiration、执行价、Call／Put、交易类、交易所和乘数。
- `SourceUtc` 与 `ReceivedUtc` 严格分开；OI 没有可靠来源时间，因此 SourceUtc 为空，
  接收时间不能当作清算日或发布日。
- IB TradingContext 保留供应商交易时段原文和时区，不在底层猜测 RTH/GTH 分段。
  未确定的交易日／区段起止为空；后续业务应使用对应版本的时段解析器解析。
- Nightwatch 同时保存桶起点及目前接口约定的采样时间（桶起点 +4 分钟）。
  GEX 的 session_date 作为交易日，不冒充 expiration；Heatmap 保留实际 expiration。
- 查询范围约定为 ReceivedUtc 的 `[FromUtc, ToUtc)`。来源时间或桶时间查询可在未来增加明确选项，
  不要默默把这三个时间混用。

## 数值、质量和观察边界

IB 存实际接收的累计量、VWAP、乘数和可用的最后一笔价／量，RegularTrades 与 AllTimeAndSales 分开。
OI 零是有效数据。Nightwatch 存独立复制的不可变执行价列表与 GEX summary，
不把旧稀疏节点合并进去，也不记录原始响应／请求头。
供应商状态只保留白名单值；不保存底层异常文字。

质量包括来源连续性未知、来源时间未知、延迟、观察开始／结束、计数器重置、乱序、部分覆盖与来源错误。
订阅取消、generic ticks 重订和连接关闭生成 ObservationBoundary；它们是本地观察边界，
不是声称在边界间取得了完整成交。
非法 IB 时间不再换成本机当前时间：实时路径拒绝该输入，启用记录时仅留下 InvalidSourceSample 标记。
重置／乱序标记只描述原始输入；记录接口不改写实时 OI／Flow 的聚合状态。

接收端只记录一次：

- IB：共享解析后的推送进入图表扇出之前；同一合约多个消费者不重复记录。
- Nightwatch：共享 Download 的成功解析完成处；其他图表缓存命中不再记录。
- 新一轮真实获取即便返回相同 as-of，也是一次新的观察，可有相同 SourceUtc。
- 不跨插件、不同凭据请求或不同 IB 连接做全局事件去重。

## 背压、错误与停机

默认队列容量 1024、批量上限 64、空闲检查 100 ms。只用 TryWrite，不使用等待入队；
最多保留队列容量加一个在途批次，不因慢存储持续增大等待集合。
每个供应商帧最多 2048 节点，适配器复制后保存；真实 Heatmap/GEX 帧受原解析器更小的限制。

Accepted 只说明已入队，不表示已经持久化。writer 返回成功后才增加 Written。
记录缺口以每个运行段累计账本表示：Congested、CaptureFailed、WriteFailed、UndeliveredOnStop。
账本有 revision；下一次成功批量写入携带它，即使当时没有新行情，也会尝试发送空批次写入账本。
writer 应把批次与账本 revision 原子提交，按运行段／事件序号幂等处理。

写入异常停止该记录器，不自动重试，不阻断实时行情。
此时无法保证把“存储自身坏了”的标记写回同一个坏存储：宿主必须读取最终 Status，
将该运行段标为未完整完成。Written、Accepted 与损失量只用于本段审计，不代表上游行情完整性。
半途写入后抛异常的事件属于未确认，可能已经在介质上；不能直接无条件重放而产生重复。

正常释放尝试排空队列；默认 2 秒后取消，剩余项计入未确认交付。
writer 必须真正异步、不能在返回 ValueTask 前无限同步阻塞，并及时响应取消。
非合作式第三方 I/O 无法被 .NET 安全强杀；超时后仍可能在外部继续运行，不能宣称已成功落盘。
批次不复用可变缓冲，因此即便迟结束也不会读到下一批被覆盖的数据。
writer 自己负责关闭其连接；sink 负责自己的队列、任务、计时器和取消源。

记录缺口账本保守作用于整个记录段，不精确推断哪个执行价、哪段分钟丢失。
上游未订阅、断线、供应商漏样、盘前未覆盖等与存储拥塞不是同一概念。
当前查询示例始终 `SourceContinuityKnown=false`，不承诺完整逐笔历史。

## 查询与未来派生结果

Query 要求 UTC、非空正向区间、Limit 1–10000；支持取消和 continuation token。
实际 reader 必须提供稳定快照／游标，不得因分页间新写入悄悄跳过或重复数据。
内存测试 reader 在数据版本变化后返回 Unavailable，要求重新查询；它不是可用于生产的历史数据库。
结果带记录段缺口，不用空数组掩盖存储禁用或不可用。

预留 MarketAggregationContext：算法版本、桶模式／区间、桶起止、ATM 与上下执行价边界及部分覆盖。
本批不生产派生历史结果，不把实时显示值直接当可重算历史。
后续数据库实现（例如 SQLite）需另定 schema、迁移、事务、磁盘限额、保留期和完整性协议。
历史 reader 不会自动连接到当前实时基线；回补／恢复必须另行设计和显式启用。

## 离线验证

```powershell
dotnet run --project tests/FuturesReferencePriceAxis.Tests -c Release -- --history-tests
```

测试使用内存数据和假 Socket，不访问 IB／Nightwatch，不写行情文件，不需要登录 Gateway。
