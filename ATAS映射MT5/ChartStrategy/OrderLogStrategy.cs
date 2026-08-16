using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using ATAS.DataFeedsCore;
using ATAS.Strategies.Chart;

namespace ATASOrderLogStrategy
{
    public class OrderTradeRecorder : ChartStrategy
    {
        private readonly string _logFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "ATASLogs",
            "TradeLog.txt");
        private Dictionary<string, decimal> _lastNetPositions = new Dictionary<string, decimal>();
        private Dictionary<string, decimal> _inFlightNetPositions = new Dictionary<string, decimal>();
        private MT5WebSocketClient _MT5WebSocketClient = new MT5WebSocketClient();
        private string? _lastLoggedServerUrl = null;
        private bool _disposed = false;

        [Category("Execution WebSocket")]
        [DisplayName("使用远程执行服务端")]
        [Description("关闭时连接本机执行网关 127.0.0.1；开启时连接远程执行网关的局域网IP。")]
        public bool UseRemoteMt5Server { get; set; } = false;

        [Category("Execution WebSocket")]
        [DisplayName("远程执行服务端 IP")]
        [Description("运行执行网关的电脑局域网IP，例如 192.168.1.20。")]
        public string RemoteMt5Ip { get; set; } = "127.0.0.1";

        [Category("Execution WebSocket")]
        [DisplayName("远程执行服务端端口")]
        [Description("执行网关 WebSocket 端口，默认 8766。")]
        public int RemoteMt5Port { get; set; } = 8766;

        public OrderTradeRecorder()
        {
            try
            {
                // 确保日志目录存在
                var logDirectory = Path.GetDirectoryName(_logFilePath);
                if (!string.IsNullOrEmpty(logDirectory))
                {
                    Directory.CreateDirectory(logDirectory);
                }
                File.AppendAllText(_logFilePath, "监听开始\n");
                

            }
            catch (Exception ex)
            {
                File.AppendAllText(_logFilePath, "报错:"+ ex.Message+"\n");
            }
        }

        protected override void OnStarted()
        {
            // 异步初始化
            _ = InitializeAsync();
        }



        private async Task InitializeAsync()
        {
            try
            {
                File.AppendAllText(_logFilePath, "开始初始化WebSocket连接\n");
                if (!EnsureWebSocketClientForCurrentSettings())
                {
                    return;
                }
                
                // 异步连接WebSocket
                bool connected = await _MT5WebSocketClient.Connect();
                
                if (connected)
                {
                    // 异步发送请求
                    bool requestSent = await _MT5WebSocketClient.SendRequest("get_account_info", new object());
                    
                    if (requestSent)
                    {
                        File.AppendAllText(_logFilePath, "WebSocket初始化完成\n");
                    }
                    else
                    {
                        File.AppendAllText(_logFilePath, "发送账户信息请求失败\n");
                    }
                }
                else
                {
                    File.AppendAllText(_logFilePath, "WebSocket连接失败\n");
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(_logFilePath, "WebSocket初始化报错: " + ex.Message + "\n");

            }
        }

        //protected override void OnNewOrder(Order order)
        //{
        //    base.OnNewOrder(order);
        //    if (!myorders.Contains(order.Id))
        //    {
        //        myorders.Add(order.Id);
        //    }
 
        //    string logEntry = $"{DateTime.Now}: 下单New Order - ID: {order.Id}, Price: {order.Price}, 触发价格: {order.TriggerPrice},未成交量: {order.Unfilled}, 成交量: {order.QuantityToFill}, Direction: {order.Direction}, State: {order.State}, WasActive: {order.WasActive}, Canceled: {order.Canceled}\n";
        //    File.AppendAllText(_logFilePath, logEntry);
        //}

        //protected override void OnNewMyTrade(MyTrade myTrade)
        //{
        //    base.OnNewMyTrade(myTrade);
        //    if (myorders.Contains(myTrade.Id))
        //    {
        //        File.AppendAllText(_logFilePath, $"成交{DateTime.Now}: New Trade - ID: {myTrade.Id},New Trade - ID: {myTrade.AccountID}, Price: {myTrade.Price}, Volume: {myTrade.Volume}, Direction: {myTrade.OrderDirection}\n");
        //    }

        //    //string logEntry = $"{DateTime.Now}: New Trade - ID: {myTrade.Id},New Trade - ID: {myTrade.AccountID}, Price: {myTrade.Price}, Volume: {myTrade.Volume}, Direction: {myTrade.OrderDirection}\n";
        //    //File.AppendAllText(_logFilePath, logEntry);

        //}

        protected override void OnPositionChanged(Position position)
        {
            if (!EnsureWebSocketClientForCurrentSettings())
            {
                File.AppendAllText(_logFilePath, "WebSocket配置无效，跳过净仓同步\n");
                return;
            }

            var Securityid = position.Security.ToString();
            // 获取当前持仓信息
            string logEntry = $"{DateTime.Now}: 持仓变化 - 合约: {position.Security},数量: {position.Volume}, 均价: {position.AveragePrice}, IsInPosition: {position.IsInPosition}\n";

            if (_MT5WebSocketClient.IsConnected &&
                _lastNetPositions.TryGetValue(Securityid, out var lastVolume) &&
                lastVolume == position.Volume)
            {
                File.AppendAllText(_logFilePath, "净仓未变化，跳过同步 " + logEntry);
                return;
            }

            if (_inFlightNetPositions.TryGetValue(Securityid, out var inFlightVolume) &&
                inFlightVolume == position.Volume)
            {
                File.AppendAllText(_logFilePath, "相同净仓同步请求正在进行中，跳过重复发送 " + logEntry);
                return;
            }

            _inFlightNetPositions[Securityid] = position.Volume;
            _ = SendPositionSyncAsync(position, Securityid);
            File.AppendAllText(_logFilePath, "同步净仓" + logEntry);
        }

        // 异步发送当前净仓目标
        private async Task SendPositionSyncAsync(Position position, string securityId)
        {
            if (!EnsureWebSocketClientForCurrentSettings())
            {
                _inFlightNetPositions.Remove(securityId);
                File.AppendAllText(_logFilePath, "WebSocket配置无效，无法发送净仓同步\n");
                return;
            }

            if (!_MT5WebSocketClient.IsConnected)
            {
                File.AppendAllText(_logFilePath, "WebSocket未连接，尝试重新连接\n");
                bool reconnected = await _MT5WebSocketClient.Connect();
                if (!reconnected)
                {
                    _inFlightNetPositions.Remove(securityId);
                    File.AppendAllText(_logFilePath, "WebSocket重连失败，无法发送净仓同步\n");
                    return;
                }
            }

            try
            {
                var positionInfo = new
                {
                    symbol = position.Security.ToString(),
                    net_volume = position.Volume,
                    average_price = position.AveragePrice,
                    source = "ATAS",
                    timestamp = DateTime.Now
                };

                bool success = await _MT5WebSocketClient.SendRequest("sync_position", positionInfo);
                if (success)
                {
                    _lastNetPositions[securityId] = position.Volume;
                    _inFlightNetPositions.Remove(securityId);
                    File.AppendAllText(_logFilePath, $"已发送净仓同步消息到WebSocket服务器，目标净仓: {position.Volume}\n");
                }
                else
                {
                    _inFlightNetPositions.Remove(securityId);
                    File.AppendAllText(_logFilePath, "发送净仓同步消息失败\n");
                }
            }
            catch (Exception ex)
            {
                _inFlightNetPositions.Remove(securityId);
                File.AppendAllText(_logFilePath, $"发送净仓同步消息异常: {ex.Message}\n");
            }
        }

        private bool EnsureWebSocketClientForCurrentSettings()
        {
            if (!TryBuildServerUrl(out var serverUrl))
            {
                return false;
            }

            if (_lastLoggedServerUrl != serverUrl)
            {
                var mode = UseRemoteMt5Server ? "远程执行服务端" : "本机执行服务端";
                File.AppendAllText(_logFilePath, $"当前使用{mode}: {serverUrl}\n");
                _lastLoggedServerUrl = serverUrl;
            }

            if (_MT5WebSocketClient.ServerUrl == serverUrl)
            {
                return true;
            }

            File.AppendAllText(_logFilePath, $"WebSocket目标变更为: {serverUrl}\n");
            _MT5WebSocketClient.Dispose();
            _MT5WebSocketClient = new MT5WebSocketClient(serverUrl);
            return true;
        }

        private bool TryBuildServerUrl(out string serverUrl)
        {
            serverUrl = MT5WebSocketClient.DefaultServerUrl;
            var port = RemoteMt5Port;

            if (port <= 0 || port > 65535)
            {
                File.AppendAllText(_logFilePath, $"远程MT5端口无效: {port}，有效范围是1-65535\n");
                return false;
            }

            if (!UseRemoteMt5Server)
            {
                serverUrl = MT5WebSocketClient.DefaultServerUrl;
                return true;
            }

            var host = (RemoteMt5Ip ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(host))
            {
                File.AppendAllText(_logFilePath, "已启用远程执行服务端，但远程IP为空\n");
                return false;
            }

            if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
            {
                File.AppendAllText(_logFilePath, $"远程执行服务端IP/主机名无效: {host}\n");
                return false;
            }

            var builder = new UriBuilder("ws", host, port);
            serverUrl = builder.Uri.ToString();
            return true;
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            // 策略计算逻辑，暂时不需要实现
            // 我们主要使用此策略监控订单和持仓变化
        }

        protected override void OnSuspended()
        {
            Disconnect();
        }

        protected override void OnStopped()
        {
            Disconnect();
        }

        public void Disconnect()
        {
            if (_disposed) return;

            try
            {
                File.AppendAllText(_logFilePath, "正在释放资源\n");
                _MT5WebSocketClient?.Dispose();
                _disposed = true;
            }
            catch (Exception ex)
            {
                File.AppendAllText(_logFilePath, $"释放资源时出错: {ex.Message}\n");
            }
        }
    }
}
