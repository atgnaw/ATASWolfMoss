# ATAS 期货—现货同分钟价格轴映射

这是一个适用于 ATAS 8.x 的自定义指标，在期货主图左侧增加参考标的价格轴：

- NQ / MNQ → QQQ
- ES / MES → SPX（Yahoo 代码 `^GSPC`）

自动模式不会把延迟的 Yahoo 价格与当前期货价格直接配比。指标会取得 Yahoo
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
- `Max quote age`：默认拒绝超过 20 分钟的 QQQ 分钟数据。SPX 现金盘未开盘
  时例外使用最近一个有效收盘分钟，并与 ES/MES 的同一分钟成交匹配。
- `Show update status`：关闭后不显示状态面板，但后台仍继续更新。

状态面板显示最近一次尝试状态、尝试时间、当前生效的分钟 K 时间、当时双方
价格和比例。自动更新失败时，最后一次成功比例继续生效，并标记为“已冻结”。

## 数据与限制

- Yahoo Finance chart JSON 不是 Yahoo Developer Network 承诺稳定的正式 Finance
  API，可能发生 429 限流、超时或格式变化。
- QQQ 有盘前、盘后分钟行情；SPX（`^GSPC`）是现金指数，通常只有美股正常
  交易时段才产生新的分钟 K。SPX 未开盘或休市时，ES/MES 自动更新会读取最近
  一个有效 SPX 收盘分钟 `T`，再读取 ES/MES 在同一 `[T,T+1分钟)` 内的最后
  成交价进行映射。它不会拿旧 SPX 与当前 ES 近似配比。
- ATAS 数据连接必须支持近期成交缓存或历史累计成交请求。若不支持，自动模式
  会保留旧比例或回退手动比例，绝不会改用当前期货价近似。
- 最近一次自动比例仅保存在当前指标实例内。建议始终设置一个合理的
  `Manual ratio` 作为重启、休市或网络故障时的兜底。

## 测试

核心测试不需要连接 ATAS：

```powershell
dotnet run --project .\tests\FuturesReferencePriceAxis.Tests\FuturesReferencePriceAxis.Tests.csproj -c Release
```
