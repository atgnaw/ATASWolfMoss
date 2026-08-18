namespace WolfMoss.ATAS.PriceMapping;

using System.ComponentModel;

using global::ATAS.Indicators;

using OFT.Attributes;
using OFT.Localization;

[DisplayName("Futures Reference Price Axis Pro / 期货现货映射轴 Pro（Dealer Heatmap）")]
[Category(IndicatorCategories.Other)]
[Description("NQ/MNQ→QQQ and ES/MES→SPX reference axis with Nightwatch Dealer Heatmap.")]
[HelpLink("https://docs.yehangshe.com/api/introduction")]
public sealed partial class FuturesReferencePriceAxisDealerHeatmapIndicator
    : FuturesReferencePriceAxisIndicatorBase
{
    public override string ToString()
        => $"Reference Axis Pro ({MappingMode}, {PairMode})";
}
