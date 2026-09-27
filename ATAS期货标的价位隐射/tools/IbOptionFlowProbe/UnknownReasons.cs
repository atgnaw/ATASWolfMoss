namespace IbOptionFlowProbe;

internal sealed record UnknownReason(string Code, string Key, string Label, string Color, string Explanation, string Example);
internal static class UnknownReasons
{
    // Stable identifiers: never recycle numbers when adding new reasons.
    public static readonly UnknownReason[] All =
    [
        new("U01", "MISSING_QUOTE_HISTORY", "没有历史报价", "#64748b", "候选时刻之前没有可引用的报价，或历史因中断被清除。", "第一份报价在 .100 到达，要判断的候选时刻却是 .050。"),
        new("U02", "HISTORY_UNAVAILABLE", "超出历史窗口", "#475569", "候选早于保留的 60 秒历史或容量保护范围。", "只能查最近 60 秒，却要找 70 秒前的报价。"),
        new("U03", "FUTURE_CANDIDATE", "候选晚于接收", "#9333ea", "目标时刻晚于成交回调到达，禁止使用后到报价。", "成交 .100 到达，但源时间加偏移后为 .250。"),
        new("U04", "OUTSIDE_TRADING_SEGMENT", "候选跨交易区段", "#7c3aed", "偏移后的时刻不在该成交所属交易区段。", "开盘后 100 毫秒的成交，向前移 250 毫秒落到开盘前。"),
        new("U05", "TRADING_SEGMENT_MISMATCH", "源与接收区段不同", "#a21caf", "成交发生时和程序接收时属于不同交易区段。", "休市前的成交，直到下一区段才到达。"),
        new("U06", "NOT_CONFIRMED_REALTIME", "未确认实时行情", "#b45309", "行情为延迟、冻结或实时类型尚未确认。", "报价完整，但接口将其标为延迟行情。"),
        new("U07", "MISSING_QUOTE", "买价或卖价缺失", "#0e7490", "两侧价格尚未齐全，无法计算价差和中间价。", "已收到买价 1.00，卖价仍未返回。"),
        new("U08", "INVALID_QUOTE", "报价价格无效", "#c2410c", "报价不可解析或价格不为正；不回退使用先前报价。", "最新买价被撤销并返回 -1。"),
        new("U09", "QUOTE_FLAGGED", "报价带限制标记", "#ea580c", "报价带开盘前或越界标记，本实验不用于分类。", "买卖价完整，但一侧带 PreOpen 标记。"),
        new("U10", "INVALID_QUOTE_SIZE", "挂单数量无效", "#d97706", "已返回的任一侧挂单数量为零、负数或无法解析。未返回数量不单独触发。", "卖价 1.20，但卖方挂单数量已变为 0。"),
        // U11 retired from unknown: LOCKED_QUOTE now belongs to Mid.
        new("U12", "CROSSED_QUOTE", "买价高于卖价", "#e11d48", "交叉报价不适合用来判断成交方向。", "买价 1.20，卖价 1.00。"),
        new("U13", "INVALID_ARRIVAL_CLOCK", "报价时间异常", "#6d28d9", "价格年龄缺失或为负，不能确认报价在目标时刻之前。", "候选在 .100，引用价格的时间却为 .150。"),
        new("U14", "QUOTE_TOO_OLD", "报价超过年龄阈值", "#ca8a04", "买价或卖价任意一侧超过所选阈值；数量更新不刷新价格年龄。", "阈值 500 毫秒，买价年龄已达 800 毫秒。"),
        new("U15", "OUTSIDE_QUOTE", "成交在报价范围外", "#2563eb", "不把报价外成交硬判买卖，也不用逐笔涨跌补救。", "买卖报价 1.00 / 1.20，成交却为 1.25。"),
        new("U16", "INSIDE_SPREAD", "成交在价差内部", "#0891b2", "仅贴价法只判断恰好位于买价或卖价的成交。", "买卖报价 1.00 / 1.20，成交 1.15，不等于两侧。"),
        // U17 retired from unknown: MIDPOINT_UNKNOWN now belongs to Mid.
        new("U18", "TICK_NO_PREVIOUS", "没有可比较前笔", "#0f766e", "正中间成交需要逐笔补判，但没有有效前笔。", "没有前笔价格，无法判断当前 1.10 是涨还是跌。"),
        new("U19", "TICK_NO_CHANGE", "连续同价无方向", "#059669", "近期成交一直同价，从未建立非零涨跌方向。", "连续成交 1.10、1.10、1.10。"),
        new("U20", "TICK_PREVIOUS_TOO_OLD", "距前笔超过五秒", "#65a30d", "不跨超过 5 秒的成交间隔比较涨跌。", "上一笔 10:00:00，本笔 10:00:06。"),
        new("U21", "TICK_DIRECTION_TOO_OLD", "上次涨跌已过期", "#4d7c0f", "虽有连续同价成交，但最近非零变动距今超过 5 秒。", "六秒前涨到 1.10，此后每秒成交 1.10。"),
        new("U22", "TICK_INTERRUPTED", "逐笔连续性中断", "#0d9488", "断线、重置、倒序或歧义事件之后，不沿用旧涨跌方向。", "上涨后发生倒序回调，之后的中间价成交不能沿用该上涨。"),
        new("U23", "INSUFFICIENT_TIME_CANDIDATES", "可行时间候选不足", "#7e22ce", "一致性模式要求至少两个在接收前可行的源时间候选。", "五个候选中四个都晚于接收，只剩一个。"),
        new("U24", "DIRECTION_CONFLICT", "时间候选买卖冲突", "#db2777", "不同时间假设对同一笔成交给出相反方向；即使还存在未知候选，也记录冲突。", "最新报价判买，早 100 毫秒的报价判卖。"),
        new("U25", "UNRESOLVED_CANDIDATE", "部分候选无法判断", "#c026d3", "一致性模式下没有买卖冲突，但至少一个必要候选仍未知。点击本行色段可继续展开原因组合与真实候选报价。", "最新报价判买，但历史候选的报价范围无法包含这笔成交。"),
        new("U26", "MID_DIRECTION_DISAGREEMENT", "候选中性与方向不一致", "#6366f1", "部分候选判中性，其他候选判买或卖；一致性模式不强行合并。", "最新报价判买，历史候选判 Mid。"),
        new("U99", "UNSPECIFIED", "未归档原因", "#78716c", "兼容保护：新出现但尚未收录的原因，保留原始原因码。", "离线分析器新增了一个尚未加入图例的原因。")
    ];
}
