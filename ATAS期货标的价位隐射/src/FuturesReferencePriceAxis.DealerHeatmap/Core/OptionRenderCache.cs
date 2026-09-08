namespace WolfMoss.ATAS.PriceMapping.Core;

public readonly record struct OptionRenderRow(OptionStrikeRow Row, decimal Lower, decimal Upper,
    string CallLabel, string PutLabel);
public sealed record OptionRenderFrame(OptionRenderRow[] Rows, decimal[] Strikes, decimal Maximum);

// Per-column, render-thread-owned, one-entry cache. No pixel coordinates are cached:
// Y projection must follow every chart pan, zoom and mapping-ratio change.
public sealed class OptionRenderCache
{
    private IReadOnlyList<OptionStrikeRow>? _source;
    private bool _oi;
    private OptionDataStatus _status;
    private OptionRenderFrame? _frame;
    public int BuildCount { get; private set; }

    public OptionRenderFrame Get(IReadOnlyList<OptionStrikeRow> source, bool oi, OptionDataStatus status)
    {
        if (_frame != null && ReferenceEquals(source, _source) && oi == _oi && status == _status)
            return _frame;
        var rows = source.OrderBy(static row => row.StrikeUsd).ToArray();
        var strikes = rows.Select(static row => row.StrikeUsd).ToArray();
        var rendered = new OptionRenderRow[rows.Length];
        var maximum = oi ? OptionPresentation.GetOpenInterestMaximum(rows) : OptionPresentation.GetPremiumMaximum(rows);
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var bounds = row.FlowLowerBoundUsd.HasValue && row.FlowUpperBoundUsd.HasValue
                ? (Lower: row.FlowLowerBoundUsd.Value, Upper: row.FlowUpperBoundUsd.Value)
                : OptionStrikeSelection.GetRowBounds(strikes, i);
            var call = oi ? row.CallOpenInterest : row.CallPremium;
            var put = oi ? row.PutOpenInterest : row.PutPremium;
            var callText = call.HasValue ? OptionPresentation.FormatFlowValue(call.Value, !oi && row.CallFlowIsPartial)
                : oi ? OptionPresentation.UnknownDataMarker : OptionPresentation.GetFlowMissingMarker(status, row.CallFlowCoverage);
            var putText = put.HasValue ? OptionPresentation.FormatFlowValue(put.Value, !oi && row.PutFlowIsPartial)
                : oi ? OptionPresentation.UnknownDataMarker : OptionPresentation.GetFlowMissingMarker(status, row.PutFlowCoverage);
            rendered[i] = new(row, bounds.Lower, bounds.Upper, callText, putText);
        }
        _source = source; _oi = oi; _status = status; BuildCount++;
        return _frame = new(rendered, strikes, maximum);
    }
}
