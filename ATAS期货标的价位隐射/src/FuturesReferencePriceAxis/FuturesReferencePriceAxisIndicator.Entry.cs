namespace WolfMoss.ATAS.PriceMapping;

using System.ComponentModel;

using global::ATAS.Indicators;

using OFT.Attributes;
using OFT.Localization;

[DisplayName("Futures Reference Price Axis / 期货现货映射轴")]
[Category(IndicatorCategories.Other)]
[Description("NQ/MNQ→QQQ and ES/MES→SPX time-aligned left reference price axis.")]
[HelpLink("https://docs.atas.net/en/")]
public sealed class FuturesReferencePriceAxisIndicator
    : FuturesReferencePriceAxisIndicatorBase
{
}
