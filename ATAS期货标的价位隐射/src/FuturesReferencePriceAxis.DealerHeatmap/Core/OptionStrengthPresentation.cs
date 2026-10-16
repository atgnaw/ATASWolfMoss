namespace WolfMoss.ATAS.PriceMapping.Core;

using System.Drawing;
using System.Globalization;

public static class OptionStrengthPresentation
{
    public static Color Color(OptionBuyStrength? strength, bool call)
    {
        if (strength?.Status != BuyStrengthStatus.Ready || strength.Midpoint.BuyShare is not { } p)
            return System.Drawing.Color.FromArgb(235, 112, 120, 130);
        // Fixed linear RGB interpolation; no other strike/bucket changes this scale.
        var dark = call ? (R: 20, G: 58, B: 41) : (R: 63, G: 25, B: 32);
        var bright = call ? (R: 80, G: 240, B: 155) : (R: 255, G: 113, B: 130);
        return System.Drawing.Color.FromArgb(235, (int)Math.Round(dark.R + (bright.R - dark.R) * p),
            (int)Math.Round(dark.G + (bright.G - dark.G) * p), (int)Math.Round(dark.B + (bright.B - dark.B) * p));
    }
    public static string[] Details(OptionBuyStrength? value)
    {
        var v = value ?? OptionBuyStrength.Missing;
        static string Percent(decimal? p) => p.HasValue ? (p.Value * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%" : "无法计算";
        static string Count(decimal n) => n.ToString("#,0.############################", CultureInfo.InvariantCulture);
        var state = v.Status switch
        {
            BuyStrengthStatus.Ready => "通过：两种方法的买方占比接近（不保证方向正确）",
            BuyStrengthStatus.Filtered => "× 方法差超过1个百分点：仅显示为中性灰色",
            BuyStrengthStatus.Partial => "? 本桶观察不完整：等待下一个完整桶",
            _ => "? 缺少方向数据或任一方法没有买卖参考量"
        };
        var reasons = new List<string>();
        foreach (var reason in Enum.GetValues<DirectionUnknownReason>())
        {
            var size = v.MidpointUnknownReasons[reason];
            if (size <= 0) continue;
            var label = reason switch
            {
                DirectionUnknownReason.MarketTypeUnconfirmed => "行情类型未确认",
                DirectionUnknownReason.NonRealtime => "非实时行情（延迟/冻结）",
                DirectionUnknownReason.MissingQuote => "买价或卖价缺失",
                DirectionUnknownReason.InvalidPrice => "报价价格无效",
                DirectionUnknownReason.QuoteFlagged => "报价带盘前/越界标记",
                DirectionUnknownReason.InvalidSize => "买卖挂单数量缺失或为零",
                DirectionUnknownReason.CrossedQuote => "买价高于卖价",
                DirectionUnknownReason.SessionMismatch => "成交与报价不在同一交易时段",
                _ => "其他"
            };
            reasons.Add($"未知：{label} {Count(size)}张（占未知{Percent(v.Midpoint.Unknown > 0 ? size / v.Midpoint.Unknown : null)}）");
        }
        if (v.AtQuoteUnknownReasons.InsideSpread > 0)
            reasons.Add($"贴价法另有价差内成交 {Count(v.AtQuoteUnknownReasons.InsideSpread)}张未定向");
        return ["颜色：233全部成交，按张数统计",
            state,
            .. reasons,
            "报价：接收时最新，不限年龄；报价外按方向归类",
            "主动买入占比：" + Percent(v.Midpoint.BuyShare),
            "贴价法买方占比：" + Percent(v.AtQuote.BuyShare),
            "方法差：" + (v.Gap.HasValue ? (v.Gap.Value * 100).ToString("0.00") + "个百分点（上限1）" : "无法计算"),
            "买卖覆盖：中间价 " + Percent(v.Midpoint.Coverage) + " / 贴价 " + Percent(v.AtQuote.Coverage),
            $"张数：买 {Count(v.Midpoint.Buy)} / 卖 {Count(v.Midpoint.Sell)}",
            $"中性 {Count(v.Midpoint.Mid)} / 未知 {Count(v.Midpoint.Unknown)}",
            "越亮表示买入占比越高；Put亮不代表看涨",
            "1个百分点阈值仅经5分钟桶研究；其他周期为扩展使用"];
    }
}
