using System.Globalization;
using System.Net;
using System.Text;
namespace IbOptionFlowProbe;

internal static class ProbeHtml
{
    private static string H(object? value) => WebUtility.HtmlEncode(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "—");
    public static string Render(AnalysisReport report)
    {
        var text = new StringBuilder("""
<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>期权成交观察 · 方向与不确定性</title></head><body><main>
<header class="masthead"><div><span class="eyebrow">IB 期权成交观察 / 独立验证报告</span><h1>方向，不只看一个数字。</h1><p class="subtitle">比较成交与买卖报价，观察偏向、覆盖，以及仍不能确定的部分。本报告不是投资建议，也不证明真实主动买卖方向。</p></div>
""");
        text.Append("<span class='version'>离线分析 · ").Append(H(report.AlgorithmVersion)).Append("</span></header>");
        var disconnects = report.Robustness?.Diagnostics.GetValueOrDefault("OBSERVATION_GAP") ?? report.Boundaries;
        text.Append("<div class='capture-strip'><span>数据来源 <b>").Append(report.Synthetic ? "模拟数据，非真实行情" : "本地真实采集")
            .Append("</b></span><span>本地记录 <b>").Append(report.Integrity.AbnormalEnd ? "不完整" : "完整")
            .Append("</b></span><span>成交回报 <b>").Append(report.TradeCallbacks.ToString("N0", CultureInfo.InvariantCulture))
            .Append("</b></span><span>可识别成交 <b>").Append(report.AcceptedTradeEvents.ToString("N0", CultureInfo.InvariantCulture))
            .Append("</b></span><span>运行中断 <b>").Append(disconnects).Append("</b></span></div>");
        var caution = report.Integrity.AbnormalEnd ? "本地记录不完整：只能查看已收到的部分，不能据此判断完整成交方向。"
            : report.Conclusion == "UNSUITABLE_FOR_FULL_FLOW_INFERENCE" ? "本地记录完整不等于市场数据完整。存在对账差额或数据质量限制，以下仅描述已观察成交，不能外推全部成交方向。"
            : "阅读顺序：先看分类覆盖，再看未知边界，最后看倾向。高覆盖不等于高准确率。";
        text.Append("<p class='notice'>").Append(H(caution)).Append("</p>");
        if (report.Robustness != null) text.Append(DirectionHtml.Render(report.Robustness, report.OutsideQuoteRobustness));
        text.Append("<details class='audit'><summary>数据质量、计算约定与下载</summary><ul>");
        foreach (var warning in report.Warnings) text.Append("<li>").Append(H(warning)).Append("</li>");
        text.Append("</ul><p>全部原始对账与七组旧阈值统计仍保留在 <a href='report.json'>完整数据文件</a>；没有覆盖先前报告。</p>")
            .Append("<p>运行编号：<code>").Append(H(report.RunId)).Append("</code>。逐笔证据位于同目录 decisions-*.jsonl，包含原始序号和候选报价引用。</p>")
            .Append("<p>原始事件 ").Append(report.RawEvents).Append(" 条。所有时间随所选时区显示；底层证据保留世界标准时间。</p></details>")
            .Append("<footer class='site-footer'>独立离线报告 · 无外部字体或网络请求 · 不连接交易网关</footer></main></body></html>");
        return text.ToString();
    }
}
