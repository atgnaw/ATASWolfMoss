# Flow 未来拒绝：毫秒级证据补充

日期：2026-09-10。Pro 保持 2.3.0，仅补充诊断，不调整过滤/分桶/订阅规则。

## 新增显示

在 `Performance diagnostics` 设为 Summary 或 Record，并开启“性能监控信息”分类后显示：

- `FLOW 最近未来拒绝 UTC：源 MM-dd HH:mm:ss.fff；接收 MM-dd HH:mm:ss.fff`
- `FLOW 未来超前 ms：最近 …；累计最大 …`

只在实际分类为 FutureSource 的回调到来时更新以上证据。
成功回调、共享去重或其他拒绝不会覆盖它；原有“FLOW UTC 源/接收”仍指最后一次普通回调。
最近值可以变小，最大值不会因此降低。尚无此类拒绝时显示“暂无/--”，不伪装为 0。
两端时间显示毫秒；超前值显示 4 位小数毫秒，能辨认不足一毫秒的差异。
最大值为本图诊断对象生命周期内、诊断开启期间采集到的累计最大值，不是当前桶最大值。
Off 不采集，重新加载指标会建立新计数；不会从旧 DLL 恢复之前的 328 次证据。

## Record

诊断元数据 schema_version 升级为 4，CSV 原有 52 列顺序/意义不变，在末尾追加：

- `last_future_source_utc`
- `last_future_received_utc`
- `last_future_lead_ms`
- `max_future_lead_ms`

时间以 ISO 8601 UTC 保存原精度，数值使用固定文化格式；未采集到证据为空值。
仍为周期汇总文件，不是逐笔日志；多个拒绝发生在两次汇总间隔内时保留最近一次与累计最大值。
文件不含凭据或账户信息。隐藏性能 UI 不停止 Record。

## 验证与范围

- Release：0 警告，0 错误；127 组离线测试全部通过。
- 新增测试：空状态、跨日/毫秒/亚毫秒、后续成功不覆盖、较小值不降低最大值、
  旧快照不可变、不同区域设置下 UI/CSV 一致、schema 和列对齐、回调路径零分配。
- 未放宽 `sample.SampleUtc > received` 拒绝条件；没有改系统时间、Heatmap/GEX 或进行实盘连接。
- 完全退出 ATAS 后替换 DLL；重现计数增长后截取新增两行即可继续定位。
  若面板高度不足，可隐藏其他状态分类或增高图表。

Pro DLL：`src/FuturesReferencePriceAxis.DealerHeatmap/bin/Release/FuturesReferencePriceAxis.DealerHeatmap.dll`

SHA-256：`9A9D1FC161F3830A940706C3228350AE65A55542BEF32D423720EC8B2853002C`
