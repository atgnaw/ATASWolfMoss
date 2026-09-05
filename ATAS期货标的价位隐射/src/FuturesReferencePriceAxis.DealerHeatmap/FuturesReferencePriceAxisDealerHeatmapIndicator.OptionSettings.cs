namespace WolfMoss.ATAS.PriceMapping;

using System.ComponentModel.DataAnnotations;

using WolfMoss.ATAS.PriceMapping.Core;

public sealed partial class FuturesReferencePriceAxisDealerHeatmapIndicator
{
    private bool _showOptionOpenInterest;
    private bool _showOptionPremiumFlow;
    private int _optionStrikeLevels = 21;
    private int _optionFlowIntervalMinutes = 5;
    private OptionFlowBucketMode _optionFlowBucketMode =
        OptionFlowBucketMode.PreviousCompletedFixed;
    private OptionFlowTradeScope _optionFlowTradeScope =
        OptionFlowTradeScope.RegularTrades;
    private string _ibGatewayHost = "127.0.0.1";
    private int _ibGatewayPort = 4001;
    private int _ibGatewayClientId = 2210;
    private int _ibOptionMarketDataLineBudget = 84;

    [Display(
        Name = "Show Option OI / 显示期权 OI",
        GroupName = "IB Gateway / 期权数据",
        Order = 300)]
    public bool ShowOptionOpenInterest
    {
        get => _showOptionOpenInterest;
        set
        {
            if (_showOptionOpenInterest == value)
                return;

            _showOptionOpenInterest = value;
            ReconfigureOptionData();
        }
    }

    [Display(
        Name = "Show Option Premium/Volume / 显示权利金与成交量",
        GroupName = "IB Gateway / 期权数据",
        Order = 310)]
    public bool ShowOptionPremiumFlow
    {
        get => _showOptionPremiumFlow;
        set
        {
            if (_showOptionPremiumFlow == value)
                return;

            _showOptionPremiumFlow = value;
            ReconfigureOptionData(resetFlowAggregation: true);
        }
    }

    [Display(
        Name = "Strike levels / 执行价档数",
        Description = "Odd levels centered on ATM / 以 ATM 为中心的奇数档",
        GroupName = "IB Gateway / 期权数据",
        Order = 320)]
    [Range(5, 21)]
    public int OptionStrikeLevels
    {
        get => _optionStrikeLevels;
        set
        {
            var normalized = OptionStrikeSelection.NormalizeLevelCount(value);

            if (_optionStrikeLevels == normalized)
                return;

            _optionStrikeLevels = normalized;
            ReconfigureOptionData(resetFlowAggregation: true);
        }
    }

    [Display(
        Name = "Flow interval / 权利金时间区间",
        Description = "Allowed: 1, 3, 5 or 10 minutes / 可选 1、3、5、10 分钟",
        GroupName = "IB Gateway / 期权数据",
        Order = 330)]
    public int OptionFlowIntervalMinutes
    {
        get => _optionFlowIntervalMinutes;
        set
        {
            var normalized = OptionFlowAggregation.NormalizeInterval(value);

            if (_optionFlowIntervalMinutes == normalized)
                return;

            _optionFlowIntervalMinutes = normalized;
            ReconfigureOptionData(resetFlowAggregation: true);
        }
    }

    [Display(
        Name = "Flow bucket mode / 权利金时间桶模式",
        GroupName = "IB Gateway / 期权数据",
        Order = 340)]
    public OptionFlowBucketMode OptionFlowBucketMode
    {
        get => _optionFlowBucketMode;
        set
        {
            if (_optionFlowBucketMode == value)
                return;

            _optionFlowBucketMode = value;
            ReconfigureOptionData(resetFlowAggregation: true);
        }
    }

    [Display(
        Name = "Trade scope / 成交统计口径",
        GroupName = "IB Gateway / 期权数据",
        Order = 350)]
    public OptionFlowTradeScope OptionFlowTradeScope
    {
        get => _optionFlowTradeScope;
        set
        {
            if (_optionFlowTradeScope == value)
                return;

            _optionFlowTradeScope = value;
            ReconfigureOptionData(resetFlowAggregation: true);
        }
    }

    [Display(
        Name = "Gateway host / Gateway 地址",
        GroupName = "IB Gateway / 期权数据",
        Order = 360)]
    public string IbGatewayHost
    {
        get => _ibGatewayHost;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value)
                ? "127.0.0.1"
                : value.Trim();

            if (string.Equals(_ibGatewayHost, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            _ibGatewayHost = normalized;
            ReconfigureOptionData(resetGateway: true, resetFlowAggregation: true);
        }
    }

    [Display(
        Name = "Gateway port / Gateway 端口",
        GroupName = "IB Gateway / 期权数据",
        Order = 370)]
    [Range(1, 65535)]
    public int IbGatewayPort
    {
        get => _ibGatewayPort;
        set
        {
            var normalized = Math.Clamp(value, 1, 65535);

            if (_ibGatewayPort == normalized)
                return;

            _ibGatewayPort = normalized;
            ReconfigureOptionData(resetGateway: true, resetFlowAggregation: true);
        }
    }

    [Display(
        Name = "Client ID / 客户端 ID",
        GroupName = "IB Gateway / 期权数据",
        Order = 380)]
    [Range(0, int.MaxValue)]
    public int IbGatewayClientId
    {
        get => _ibGatewayClientId;
        set
        {
            var normalized = Math.Max(0, value);

            if (_ibGatewayClientId == normalized)
                return;

            _ibGatewayClientId = normalized;
            ReconfigureOptionData(resetGateway: true, resetFlowAggregation: true);
        }
    }

    [Display(
        Name = "Option line budget / 期权行情线预算",
        GroupName = "IB Gateway / 期权数据",
        Order = 390)]
    [Range(10, 500)]
    public int IbOptionMarketDataLineBudget
    {
        get => _ibOptionMarketDataLineBudget;
        set
        {
            var normalized = Math.Clamp(value, 10, 500);

            if (_ibOptionMarketDataLineBudget == normalized)
                return;

            _ibOptionMarketDataLineBudget = normalized;
            ReconfigureOptionData(resetFlowAggregation: true);
        }
    }

    private void ReconfigureOptionData(
        bool resetGateway = false,
        bool resetFlowAggregation = false)
    {
        Interlocked.Increment(ref _optionDataGeneration);

        if (!IsIndicatorInitialized)
            return;

        if (resetGateway)
            _ = ReleaseOptionGatewayAsync();

        if (resetFlowAggregation)
            ResetOptionFlowAggregation();

        RestartOptionDataSchedule();
        RequestRedraw();
    }
}
