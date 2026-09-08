# WolfMoss.IB：可独立编译的 IB 源码模块

这是源码共享模块，不是需要一起部署的业务 DLL，也不是运行中的共享服务。
Pro 与未来其他插件分别把源码及官方依赖嵌入自己的程序集，可单独安装、运行。
本模块不引用 ATAS、OFT、WPF、Nightwatch 或 Pro 工程。
当前以 .NET 10 验证；不承诺适用于旧 .NET Framework、裁剪或 Native AOT。

## 编译接入

把 `shared/WolfMoss.IB` 与其同级 `shared/WolfMoss.MarketData` 一起复制／作为源码依赖固定到另一工程，保持目录关系和同一套版本来源。
不要同时引用其源码和 Pro DLL，也不要把它放进宿主默认 `**/*.cs` 扫描范围再重复导入。
新插件需使用自己的程序集名、具体 ATAS 指标类型名、配置和日志目录。

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>enable</Nullable>
  <LangVersion>latest</LangVersion>
  <!-- true：包含当前期权适配器；false：仅底层，供其他适配器复用 -->
  <WolfMossIbIncludeOptions>true</WolfMossIbIncludeOptions>
</PropertyGroup>
<Import Project="path/to/WolfMoss.IB/WolfMoss.IB.props" />
```

ATAS 插件仍需自行加入 ATAS 引用和 Windows 目标框架。仅使用底层的独立例子见
`tests/IbModule.ConsumerB`，包含期权层的例子见 `tests/IbModule.ConsumerA`。
两者不引用 Pro，也不会连接 Gateway。

构建机须独立安装并接受官方 TWS API 10.45 许可，默认目录
`C:\TWS API\source\CSharpClient\client`，且具有其 `bin\Release\net8.0\CSharpAPI.dll`。
本仓库 `tools/Test-IbApiSource.ps1` 验证必需源码、运行库及 10.45 版本。
自定义路径用 `-p:IBApiSourceDir="..."`；Release 缺失依赖会明确失败。
模块沿用 Google.Protobuf 3.29.5，通过 NuGet 构建时恢复并嵌入。
官方 SDK 源码不提交至本仓库；许可声明随资源嵌入，复用时不得丢弃。
构建好的插件不要求用户另装 SDK 或复制 `CSharpAPI.dll`／`Google.Protobuf.dll`。

## 源码职责

| 位置 | 可复用内容 | 不负责 |
| --- | --- | --- |
| Transport | 私有依赖加载、EWrapper 回调代理、Socket／Reader 构造、有界异步发送、请求编号、共享异步等待、Client ID 占用检查、诊断计数类型 | 历史查询实现、订单管理、数据库 |
| Options | 期权合约发现、连接池、订阅租约、generic ticks 合并、行情线预算与基础期权模型 | ATAS 绘制、Flow 时间桶、OI 日缓存 |
| Pro | 映射、目标交易日、OI 确认与缓存、Flow 聚合、列绘制、性能记录 | 账户与订单操作 |

为保持现有序列化兼容，模块暂保留 `WolfMoss.ATAS.PriceMapping`／`.Core` 命名空间。
名称中含 ATAS 不表示程序集依赖 ATAS。大部分传输类型为 internal，源内导入后可以直接调用；
公开期权枚举完整类型名不变。不跨插件传递这些私有编译得到的业务对象。

## 连接与生命周期契约

当前期权消费者用 `IbOptionGatewayPool.Acquire(options)` 获得 `IAsyncDisposable` 租约，
随后按需 `Client.ConnectAsync`、合约发现及 `SubscribeAsync`。
订阅租约只管理自己的合约和 generic tick 需求；停止时先释放订阅，再等待连接租约释放。
同一插件的相同 host/port/client_id 复用连接与请求；最后一个租约退出才断开。
快速重新开启通过旧连接退休屏障等待，不用新 ID 规避旧连接未释放。

未来使用仅 Transport 的适配器必须自己拥有完整会话生命周期：

1. 在建立连接前取得 `IbSessionReservation.Reserve`，失败不接管别人的身份。
2. 用 `EmbeddedDependencyResolver` 获得本程序集私有 SDK，`IbSocketRuntime` 创建客户端／Reader。
3. 负责读取循环、连接握手、按请求编号路由回调、超时及服务端错误。
4. 经 `IbOutboundDispatcher` 发送，配置符合数据类型的节流；不能认为实时行情节流适用于所有历史请求。
5. 停止时取消自己的待处理请求、断开自己的 Socket、等待发送与读取退出，再释放身份占用。

`IbSocketRuntime` 是构造／调用边界，不是自动重连会话服务。
不要直接 `DispatchProxy.Create` 绕过模块的代理工厂：代理基类和生成程序集也必须隔离。

## 多插件同时运行

- 每个插件用不同的、非 Master 的 Client ID，例如 Pro 2210、另一个插件 2310；不自动递增或接管。
- 同进程、本模块的不同源码副本共享的只有身份排他登记，不共享 Socket、订单、订阅和请求字典。
- 同一身份重复使用显示 `CLIENT_ID_IN_USE`；IB 326 同样转为明确冲突，不等到一般连接超时。
- localhost、127.0.0.1、::1 归一；任意 DNS 别名不自动解析。其他进程／未采用本模块的插件仍由 IB 检测冲突。
- Client ID 分离不提供额外账户行情额度。各插件、Gateway/TWS 窗口仍需共同留出额度，
  本模块预算只约束本插件，不是账户级统一资源调度器。
- SDK、Protobuf、动态 EWrapper 代理按消费程序集隔离；不按全局同名程序集搜索，
  不注册全局 AssemblyResolve。每个消费程序集使用一个私有非可回收上下文，重连复用。
  这带来每个插件独立 SDK 常驻开销；不承诺热卸载或安全沙箱。

Client ID 连接约定见 [IBKR Connections](https://interactivebrokers.github.io/tws-api/connection.html)。
加载上下文的类型隔离而非安全边界见 [Microsoft AssemblyLoadContext](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.loader.assemblyloadcontext?view=net-10.0)。

## 未来历史数据／交易插件

第六批已加入独立的标准事件、禁用记录器、有界后台批量写入和可取消查询契约，
见 [历史接口指南](../WolfMoss.MarketData/README.md)。它不连接数据库，也不会自动启动历史录制。

可以复用底层代码，不需要本 Pro 已安装。**本阶段没有实现历史查询或交易服务。**
新增历史适配器仍需处理历史专用 pacing、分页／结束回调、数据权限和取消。
新增交易适配器仍需单独设计显式授权、订单生命周期、幂等、重连恢复和风险限制。
`IbRequestIdSequence` 仅是请求编号，不可用作 order_id；订单编号须按 IB nextValidId 和订单状态管理。
约定见 [IBKR Placing Orders](https://interactivebrokers.github.io/tws-api/order_submission.html)。
Pro 保持只请求期权数据，不请求账户、持仓或订单，不配置 Master Client ID。

## 离线自检

在仓库根目录执行（不连接 Gateway，不下单）：

```powershell
dotnet build FuturesReferencePriceAxis.sln -c Release
dotnet run --project tests/IbModule.ConsumerA -c Release --no-build
dotnet run --project tests/IbModule.ConsumerB -c Release --no-build
dotnet run --project tests/FuturesReferencePriceAxis.Tests -c Release --no-build -- --ib-module-tests
```

两个消费者各自的独立进程验证无 ATAS 依赖；综合测试还在同一进程同时加载双方与 Pro，
实例化未连接 Socket、验证回调不串用、重复身份拒绝及租约释放隔离。
这不能代替最终在 ATAS 中使用不同 Client ID 的多插件实盘验收。
