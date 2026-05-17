# ATAS 映射 MT5 使用说明

这个工程用于把 ATAS 图表策略中的净持仓同步到 MT5 CFD 账户。它不是价格复制工具，也不复制 ATAS 的止盈、止损、挂单价格；它只复制最终净仓方向和整数仓位单位。

## 工作方式

工程分为两部分：

- `ChartStrategy/`：ATAS 侧 Chart Strategy。挂到 ATAS 图表后监听当前品种持仓变化，并向本机 WebSocket 服务发送 `sync_position`。
- `py_order_api/`：Python WebSocket 服务端。接收 ATAS 的目标净仓，按配置映射到 MT5 品种，并用 MT5 Python API 执行开仓/平仓。

同步模型是“ATAS 净仓目标 -> MT5 单位 ticket 池”：

```text
ATAS 目标 +2，MT5 当前 0：
  MT5 开 2 个 BUY ticket

ATAS 目标 +1，MT5 当前 +2：
  MT5 平 1 个 BUY ticket

ATAS 目标 -1，MT5 当前 +2：
  MT5 先平 2 个 BUY ticket，再开 1 个 SELL ticket

ATAS 目标 0：
  MT5 平掉该映射品种下本工具创建的所有 ticket
```

MT5 只管理本工具创建的订单，使用：

```text
magic = 123456
comment 前缀 = ATAS_SYNC
```

这样可以避免误平手动单或其他 EA 创建的订单。

## 环境要求

- Windows
- ATAS Platform，当前项目按 `net10.0-windows` 编译
- MT5 客户端，建议先手动启动并登录
- Python 3.10 或以上
- Python 依赖：

```powershell
pip install MetaTrader5 websockets
```

MT5 账户需要是 hedging / 对冲模式。净额账户无法实现“每 1 手 ATAS 对应 1 个 MT5 ticket”的同步模型。

## 配置 MT5 映射

编辑：

```text
py_order_api/config.json
```

示例：

```json
{
    "mt5_path": "",
    "server": "",
    "login": "",
    "password": "",
    "log_level": "INFO",
    "symbol_mapping": {
        "Gold": {
            "symbol": "XAUUSD",
            "unit_volume": 0.01,
            "volume_ratio": 0.01
        }
    }
}
```

字段说明：

- `mt5_path`：MT5 终端路径。留空时会尝试连接已经运行的 MT5。
- `server`、`login`、`password`：可选。留空时使用当前 MT5 已登录账户。
- `symbol_mapping`：ATAS 品种到 MT5 品种的映射。
- `unit_volume`：ATAS 每 1 手对应的 MT5 单个 ticket 手数。
- `volume_ratio`：旧字段，保留兼容；新配置优先使用 `unit_volume`。

例如：

```json
"Gold": {
    "symbol": "XAUUSD",
    "unit_volume": 0.1
}
```

表示：

```text
ATAS Gold 1 手 = MT5 XAUUSD 1 个 ticket，手数 0.1
ATAS Gold 2 手 = MT5 XAUUSD 2 个 ticket，每个 0.1
```

注意：`symbol_mapping` 的 key 需要能匹配 ATAS 发来的 `position.Security.ToString()`。如果 ATAS 发来的品种名包含 `Gold`，则可以匹配上面的配置。

## 启动 Python 服务端

先打开并登录 MT5，然后启动服务端：

```powershell
cd "D:\GitHub\ATASWolfMoss\ATAS映射MT5\py_order_api"
python websocket_server.py
```

服务默认监听：

```text
ws://127.0.0.1:8766
```

服务只监听本机，不对局域网开放。

## 编译 ATAS 策略 DLL

在工程根目录运行：

```powershell
cd "D:\GitHub\ATASWolfMoss\ATAS映射MT5"
dotnet build ".\ChartStrategy\ATASOrderLogStrategy.csproj" -c Release
```

生成的 DLL 路径：

```text
ChartStrategy\bin\Release\net10.0-windows\ATASOrderLogStrategy.dll
```

如果编译时看到 `WindowsBase` 版本冲突警告，但结果是：

```text
生成 成功
0 个错误
```

可以先忽略。不要把整个输出目录复制到 ATAS，只复制本策略 DLL。

## 安装到 ATAS

复制：

```text
ChartStrategy\bin\Release\net10.0-windows\ATASOrderLogStrategy.dll
```

到 ATAS 策略目录：

```text
%APPDATA%\ATAS\Strategies
```

展开后一般是：

```text
C:\Users\你的用户名\AppData\Roaming\ATAS\Strategies
```

不要复制这些 ATAS 自带 DLL：

```text
ATAS.DataFeedsCore.dll
ATAS.Indicators.dll
ATAS.Strategies.dll
```

这些由 ATAS 平台自己提供，复制过去可能造成加载冲突。

## 在 ATAS 中使用

1. 先启动 MT5，并确认账户已登录。
2. 启动 `py_order_api/websocket_server.py`。
3. 重启 ATAS，或刷新策略列表。
4. 打开需要同步的 ATAS 图表。
5. 添加 Chart Strategy。
6. 找到策略类：

```text
OrderTradeRecorder
```

7. 挂载到图表。
8. 在 ATAS 中产生持仓变化后，策略会发送当前净仓目标到 Python 服务端。

日志默认写到：

```text
%USERPROFILE%\Documents\ATASLogs\TradeLog.txt
```

## 验证同步是否正常

建议先用模拟账户或小手数测试。

测试流程：

1. 确认 Python 服务端显示 MT5 连接成功。
2. 在 ATAS 对映射品种开多 1 手。
3. MT5 应该出现 1 个对应品种 BUY ticket，手数为 `unit_volume`。
4. ATAS 加仓到 2 手。
5. MT5 应该再新增 1 个 BUY ticket，而不是把原 ticket 改成 2 倍手数。
6. ATAS 减仓到 1 手。
7. MT5 应该平掉 1 个 BUY ticket。
8. ATAS 反手为空 1 手。
9. MT5 应该先平剩余 BUY，再开 1 个 SELL ticket。
10. ATAS 平仓到 0。
11. MT5 应该平掉本工具创建的对应 SELL ticket。

## 常见问题

### ATAS 找不到策略

确认 DLL 放在：

```text
%APPDATA%\ATAS\Strategies
```

并且复制的是：

```text
ATASOrderLogStrategy.dll
```

不是整个 `bin` 目录。

### Python 服务端提示 MT5 未连接

先手动启动 MT5，并登录账户。若 MT5 安装路径特殊，可以在 `config.json` 中填写 `mt5_path`。

### MT5 没有下单

检查：

- Python 服务端是否正在运行。
- ATAS 日志中是否有 WebSocket 连接成功。
- `config.json` 中品种映射 key 是否能匹配 ATAS 发来的品种名。
- MT5 品种名是否真实存在，例如 `XAUUSD`、`BTCUSDm`。
- MT5 账户是否允许交易。
- MT5 账户是否为 hedging / 对冲模式。

### 为什么不复制 SL/TP

ATAS 可能交易 CME 期货，而 MT5 侧交易的是 CFD。两边价格、点值、合约规格、小数位和交易时间都可能不同，不能简单映射 ATAS 的止盈止损价格。当前工具只复制净仓方向和仓位单位。

### 为什么 ATAS 2 手不是 MT5 一笔 0.2 手

为了后续减仓和反手能精确匹配，MT5 侧每 1 手 ATAS 都对应一个独立 ticket。例如 `unit_volume = 0.1` 时：

```text
ATAS 2 手 -> MT5 两个 0.1 ticket
```

这样 ATAS 从 2 手减到 1 手时，只需要平掉其中 1 个 ticket。

## 开发与测试

Python 单元测试：

```powershell
cd "D:\GitHub\ATASWolfMoss\ATAS映射MT5\py_order_api"
python -m unittest discover -s tests
```

Python 语法检查：

```powershell
python -m py_compile websocket_server.py mt5_trader.py symbol_mapper.py position_sync.py sync_contract.py websocket_client.py
```

C# 编译：

```powershell
cd "D:\GitHub\ATASWolfMoss\ATAS映射MT5"
dotnet build ".\ChartStrategy\ATASOrderLogStrategy.csproj" -c Release
```
## LAN remote MT5 deployment

By default the ATAS strategy connects to the local Python server:

```text
ws://127.0.0.1:8766
```

To execute orders on another computer in the same LAN:

1. On the MT5 computer, start MT5, log in, and enable Algo Trading.
2. On the MT5 computer, edit `py_order_api/config.json`:

```json
{
    "websocket": {
        "listen_host": "0.0.0.0",
        "port": 8766
    }
}
```

3. Allow Python or TCP port `8766` through Windows Firewall on the MT5 computer.
4. Start `py_order_api/websocket_server.py` on the MT5 computer.
5. On the ATAS computer, open the strategy settings:
   - Enable `使用远程MT5服务端`.
   - Set `远程MT5 IP` to the MT5 computer LAN IP, for example `192.168.1.20`.
   - Keep `远程MT5端口` as `8766` unless you changed the Python server port.
6. Confirm the MT5 computer PowerShell log shows a new WebSocket client connection before trading live.

If remote mode is disabled, the strategy always falls back to local mode and connects to `127.0.0.1:8766`.
## Unified execution gateway for MT5 and Bookmap

ATAS now sends `sync_position` to one execution gateway. The gateway fans the
same request out to enabled executors such as MT5 and Bookmap.

Default port layout:

```text
ATAS -> Gateway:             ws://127.0.0.1:8766
Gateway -> MT5 executor:     ws://127.0.0.1:8767
Bookmap Add-on -> Gateway:   registers on ws://127.0.0.1:8766
```

Start order for local testing:

```powershell
cd "D:\GitHub\ATASWolfMoss\ATAS映射MT5\py_order_api"
python websocket_server.py
python -m gateway.execution_gateway
```

For LAN deployment, run the gateway on the machine that ATAS should connect to
and open TCP port `8766` in Windows Firewall. If MT5 is on another machine, run
the MT5 executor there, expose its configured port, and update `targets.mt5.url`
in `py_order_api/config.json`.

Gateway config example:

```json
{
    "gateway": {
        "listen_host": "127.0.0.1",
        "port": 8766,
        "request_timeout_seconds": 30
    },
    "targets": {
        "mt5": {
            "enabled": true,
            "url": "ws://127.0.0.1:8767"
        },
        "bookmap": {
            "enabled": true,
            "connection": "registered"
        }
    },
    "websocket": {
        "listen_host": "127.0.0.1",
        "port": 8767
    }
}
```

Bookmap Java Add-on lives in `bookmap_addon/`. It contains the net-position
planner, Gateway registration client, and a message handler that can be wired to
Bookmap API order/position callbacks. Keep `dryRun=true` until Bookmap simulated
orders are verified.
