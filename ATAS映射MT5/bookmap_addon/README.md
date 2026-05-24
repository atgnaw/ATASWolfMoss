# ATAS Bookmap Bridge

这是 Bookmap 端的执行插件，用来接收 ATAS 统一执行网关转发过来的净仓同步消息。

整体链路如下：

```text
ATAS ChartStrategy -> Python execution_gateway.py -> Bookmap Java Add-on -> Bookmap Api.sendOrder(...)
```

这个 JAR 里包含一个真实的 Bookmap Simplified API 模块：

```text
ATAS Bookmap Bridge
```

插件加载到 Bookmap 后，会主动连接 Python Gateway，并注册为 `bookmap` 执行端。收到 `sync_position` 后，它会读取当前 Bookmap 净仓，根据 ATAS 目标净仓计算差量；当 `Dry run=false` 时，用 Bookmap API 发送市价单。

## 编译

需要先安装 JDK 17 和 Gradle，然后执行：

```powershell
cd bookmap_addon
gradle jar
```

生成的 JAR 在：

```text
bookmap_addon/build/libs/ATASBookmapBridge-0.1.0.jar
```

## 加载到 Bookmap

在 Bookmap 中打开：

```text
Settings -> Api plugins configuration -> Add
```

选择生成的 `ATASBookmapBridge-0.1.0.jar`。

加载后，把 `ATAS Bookmap Bridge` 挂到需要执行交易的 Bookmap 图表/合约上。

## Bookmap 参数

首次加载建议保持安全设置：

```text
Enabled = false
Dry run = true
Gateway URL = ws://127.0.0.1:8766
ATAS symbol = MNQM6@CME
Bookmap alias = 留空时使用当前 Bookmap 图表合约 alias
Unit multiplier = 1
Max delta per sync = 3
```

参数含义：

- `Enabled`：是否启用插件。默认关闭，避免加载后立即执行。
- `Dry run`：干跑模式。为 `true` 时只计算和返回结果，不真实下单。
- `Gateway URL`：Python Gateway 地址。本机通常是 `ws://127.0.0.1:8766`，局域网则填写 Gateway 所在电脑 IP。
- `ATAS symbol`：ATAS 发出的品种名，必须和 Gateway 收到的 `symbol` 一致。
- `Bookmap alias`：Bookmap 里的合约 alias。留空时使用当前图表的 alias。
- `Unit multiplier`：ATAS 1 手对应 Bookmap 几手期货。通常先设为 `1`。
- `Max delta per sync`：单次同步允许的最大净仓变化，防止配置错误导致大额下单。

## 首次测试流程

1. 在 Bookmap 所在电脑启动 Python Gateway：

   ```powershell
   python -m gateway.execution_gateway
   ```

2. 在 Bookmap 中加载 JAR。
3. 把 `ATAS Bookmap Bridge` 挂到目标合约图表。
4. 设置 `Gateway URL`、`ATAS symbol`，必要时设置 `Bookmap alias`。
5. 设置 `Enabled=true`，但保持 `Dry run=true`。
6. 在 ATAS 里发送 1 手测试信号。
7. 查看 Gateway 日志，确认出现 `bookmap` 执行端结果，并检查方向和数量是否正确。
8. 干跑确认无误后，在模拟账号里设置 `Dry run=false`，再测试 1 手市价单。

## 执行模型

Bookmap 期货账号是净仓模式，不像 MT5 CFD 那样每手拆成独立 ticket。

```text
delta = ATAS 目标净仓 - 当前 Bookmap 净仓
delta > 0 -> BUY delta * Unit multiplier
delta < 0 -> SELL abs(delta) * Unit multiplier
delta = 0 -> 不操作
```

示例：

```text
当前 0，目标 +2  -> BUY 2
当前 +2，目标 +1 -> SELL 1
当前 +2，目标 -1 -> SELL 3
当前 -3，目标 0  -> BUY 3
```

## 注意事项

- 插件不复制 ATAS 的价格、均价、SL/TP 或挂单。
- 插件只同步最终净仓目标。
- 超过 `Max delta per sync` 时会拒绝执行。
- 建议先用 `Dry run=true` 验证 Gateway 注册、品种映射、当前仓位读取和差量计算。
- ATAS 仍然只连接 Gateway；MT5 和 Bookmap 可以同时作为下游执行端存在。
