# 稳定性第五批：诊断分离、共享统计性能与最终验收

2026-09-10。第四、第五批连续实施，到此停止本轮开发，不自动扩展到其他功能。

## 交付

最终 Release 构建：0 警告、0 错误；全量离线回归：121 组全部通过。
其中新增第四批四组测试、第五批一组测试，并扩充原有诊断、重连及共享统计回归断言。

普通版仍为 1.1.0，Pro 仍为 2.3.0；程序集与指标类型名称不变，Pro 不增加需手动部署的 DLL。

Pro：`src/FuturesReferencePriceAxis.DealerHeatmap/bin/Release/FuturesReferencePriceAxis.DealerHeatmap.dll`

```text
SHA256: 6B21E2CB1D1934472AE4C466AB60AE0EDBBB8C855BF7C62E09B38D49F97950FD
普通版 SHA256: A1EB596875DB6E0A01DB05296B7FCF65A8A4CA23B4A47642F6C9E67C8787620D
```

普通版哈希与此前交付一致。版本号相同，请按哈希区分 Pro 构建。

## 诊断含义已调整

- 写入：这张图表的回调成功进入样本。
- 重复/共享去重：相同合约和源时间的回调已处理，单独计数，不计入真正拒绝。
- 拒绝：分别说明未来源时间、观察开始前、范围外、交易区段外、倒序、观察暂停或无效值。
- 跨发布到达：事件源时间早于上次发布窗口结束、但接收发生在发布窗口结束之后。该项与是否写入独立，可能是正常传输延迟，不等同拒绝。

固定和 Rolling 共享组均使用精确分类，不再将所有未写入都塞入 RollingBoundary。
两个同组图表的写入计数仍可能不同，但应看到另一张图表写入、当前图表去重，不能将各图表的缓存数相加当成共享内存总量。

Record CSV 元数据 schema_version 升为 3：保留原列顺序，末尾增加 flow_duplicates、flow_observation_gaps。
flow_rejected 现在明确排除重复，不能与旧版包含去重的计数直接比较。
Summary 仍只显示 UI，不写文件；Record 仍只记录诊断，不是逐笔历史数据库。

## 性能修改与证据

- 相同或内容相等的合约梯级重新配置采用无临时分配的快速路径，避免每张同组图表每秒建立 LINQ 闭包／迭代器。
- 裁剪相关闭包只在真正执行裁剪时创建，不再在每次调用进入时分配。
- 同秒快照复用之前不再重复创建交易区段查询闭包。
- Rolling 中间结果字典在组内复用，行列表预分配合理容量；发布的行仍是独立不可变结果，不暴露可变字典。
- 10,000 次重复回调以及 1,000 次内容相等梯级重配置均有零新增托管分配的离线断言。

基准源：[优化前](batch-5-before.csv)、[优化后](batch-5-after.csv)。.NET 10.0.5，关闭分层编译，同一脚本、42 个合约、预热 3 次、测量 30 次、1/3/5/10 分钟、1/2/8 个同组消费者。
统计包括共享引擎配置和发布，不包括 ATAS 绘图、真实网络、订阅协调或整个进程；不代表所有实际场景，也不能与旧 performance 目录的不同测试框架直接拼接。

| 场景 | 优化前 B/刷新 | 优化后 B/刷新 |
| --- | ---: | ---: |
| 固定桶，同组 1 张 | 1184 | 624 |
| 固定桶，同组 8 张 | 4936 | 624 |
| Rolling，同组 1 张 | 46440 | 23760 |
| Rolling，同组 8 张 | 49296 | 23760 |

这是临时分配量减少，不是常驻内存或整机 CPU 减少百分比。
短基准耗时有波动，部分 Rolling 场景耗时上升，因此不据此宣称统一 CPU 提速；实盘前后对比仍需要用户在相近负载下采集 Record。

复现命令：

```powershell
dotnet build FuturesReferencePriceAxis.sln -c Release --no-restore
dotnet run --project tests/FuturesReferencePriceAxis.Tests -c Release --no-build
$env:DOTNET_TieredCompilation = '0'
dotnet run --project tests/FuturesReferencePriceAxis.Tests -c Release --no-build -- --shared-flow-baseline
Remove-Item Env:\DOTNET_TieredCompilation
```

## 人工验收清单

1. 完整退出 ATAS 后替换 Pro DLL并重新打开；同插件所有图表使用相同 host/port/client_id，其他独立插件使用不同 Client ID。
2. Flow 运行中反复打开／关闭 OI：Flow 不应重回首桶预热，不应仅因 OI 开关产生重新订阅／取消；正常 ATM 换档与数据配置改变除外。
3. 两张同设置 SPX 图表：已发布数据相同，一张写入、另一张去重属于正常；真正拒绝应能对应具体分类。
4. OI 全零时仍显示 0，并有上游核对提示；非零更新后提示消失。
5. 短暂断开 Gateway 后恢复：旧帧明确显示故障／冻结，新连接恢复后不得因版本倒退永久卡住，也不得跨断线缺口计算巨大累计差额。
6. 权限缺失／超预算时保留相应错误，不出现无限订阅风暴。单合约恢复只清除该合约错误。
7. 对比旧版和本版相同模式、周期、档数、图表数量、行情时段下的 Record，重点看 Publish/Callback/Render 与事件速率；不要把休盘数据和活跃开盘数据直接比较。

本轮未连接真实 IB/Nightwatch，未修改 ATAS 安装目录、用户日缓存或系统时间。
此前连接断开签名修复仍包含在本 DLL 中。所有实盘项需要用户验收，离线通过不能保证上游一定提供 OI 或 Flow 数据。
