# 独立期权方向稳健度实验 0.4.0

## 范围及交付

2026-09-19 完成。只修改 `tools/IbOptionFlowProbe` 及探针测试、文档；不修改 ATAS Flow、共享 IB 接口或指标设置。构建过程中使用现有官方 API 嵌入方案。开发与测试没有连接真实 Gateway。

- 历史报价：60 秒窗口及窗口前检查点，单合约最多 65,536 状态；价格/数量独立时间，失效报价不回退旧值。
- 对齐：接收时最新报价、源时间偏移 -250/-100/0/+100/+250 ms；单调时间查找，拒绝接收之后的候选，不夹边、不使用未来报价。
- 方法：AtQuote、Midpoint、MidpointTick；7 组年龄场景和 5 组历史对齐，共 36 组。
- 主结果：无年龄上限 Midpoint，在最新及全部时间可行候选一致、至少两个源时间候选时分类；否则未知。候选共享报价不算独立证据。
- 稳健度：B/S/U、分类覆盖、未知边界、抗误判余量，1%/2%/5% 压力，单笔/前五笔集中度及质量门槛。它们不是正确概率，也不验证真实主动方向。
- HTML：品种、实际执行价、Call/Put、口径、周期、方法、对齐、参数、时间桶筛选；默认 1m RegularTrades 主结果，50 行分页，详情保留全部场景及原因。无网络依赖。
- 原始 schema 1 兼容；报告和逐笔证据 schema 2；每次新目录，不覆盖旧结果。

## 边界修正

跨桶累计比较无法分摊标记 `RECONCILIATION_BOUNDARY`，不误等同于采集缺口；首尾截断、基线重置及真正缺口仍门控。重复 realtime 类型通知不清除新报价历史，但保留旧统计管线的基线规则；基线不继承旧 tick 方向。正常结束后的 socket 清理不标为运行时断线。

## 验证

- `dotnet build FuturesReferencePriceAxis.sln -c Release`：0 警告、0 错误。
- 探针离线测试：69/69，通过；指标回归：130/130，通过。
- `node tests/IbOptionFlowProbe.Tests/report-ui.test.cjs <report.html>`：实际嵌入脚本在最小 DOM 环境执行，验证默认值、数值执行价、Call/Put、分页、36 场景选择、详情、汇总。此测试捕获并验证修复了错误执行价字段绑定。
- `node tests/IbOptionFlowProbe.Tests/verify-direction-replay.cjs <新分析目录> <旧report.json>`：验证旧全部 totals/buckets 不变、质量汇总权利金守恒、逐笔候选及引用。
- 浏览器自动化入口连续两次启动失败，因此未完成真实浏览器视觉/布局验收；不将脚本测试称作浏览器验收。
- 原始采集压测未重复十分钟跑；本次记录的是离线分析实测，不代表实盘数据质量或指标渲染成本。

## 真实数据回放

运行：`20260916-133647-a792d7fb5c3142ad830a1efd9cd1e88a`。

最终报告目录：

```text
C:\Users\gnaw\AppData\Local\WolfMoss\IbOptionFlowProbe\runs\20260916-133647-a792d7fb5c3142ad830a1efd9cd1e88a\analysis-20260919-144801-98e0e884f64141a0a44f56917e6040ea
```

424,154 条原始记录；42,628 条成交证据；42,598 个可识别成交。新旧原七组 totals 与 buckets 逐项一致；1,296 行主结果/对照总权利金与旧已观察权利金相等；未发现后到报价引用。三卷原始 JSONL SHA-256 回放前后相同。

机器：Intel i7-12700H、20 逻辑处理器、约 31.7 GiB RAM。自包含程序回放耗时 15.653 秒，轮询采样的峰值工作集 573.69 MiB（500 ms 采样，可能漏掉更短瞬时峰值）。历史状态峰值 1,901/合约，容量驱逐 0。报告约 43 MiB HTML、50 MiB JSON，逐笔证据约 312 MiB；旧报告仍保留。

在 1m RegularTrades 桶中：QQQ 6 个 BUY、4 个 SELL、135 个 UNCERTAIN、41 个 INSUFFICIENT_EVIDENCE；SPX 1 个 BUY、1 个 SELL、59 个 UNCERTAIN、125 个 INSUFFICIENT_EVIDENCE。这些 BUY/SELL 只是未知量边界不跨零的条件性结果，不代表所有结果均通过额外 5% 误判压力，更不是准确率验证。

主分类两口径合计：12,656 个事件的时间场景一致；5,089 个方向冲突；24,853 个候选未解决。233 与 375 的权利金不能相加使用，此处仅展示程序事件计数。正常清理连接 2 次；没有被误算为运行时断线。

## 使用

原采集方式不变：发布目录 `Start-Probe.cmd`。重新分析已有数据使用 `IbOptionFlowProbe.exe analyze <运行目录>`，不要求开启 Gateway。发布包在 `tools/IbOptionFlowProbe/bin/publish/win-x64`。报告应先读覆盖、未知边界及质量，再看倾向；详情中的算术压力不越过质量门槛作结论。
