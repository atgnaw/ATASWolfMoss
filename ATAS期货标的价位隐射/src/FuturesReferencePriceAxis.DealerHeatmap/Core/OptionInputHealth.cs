namespace WolfMoss.ATAS.PriceMapping.Core;

// Scheduled/data owner only. One contract failure must not clear healthy rows.
internal sealed class OptionInputHealth
{
    private readonly Dictionary<long, string> _errors = new();
    private readonly HashSet<long> _delayed = new();
    private long _revision, _publishedRevision = -1;
    private OptionFlowSnapshot? _source, _published;
    private OptionOpenInterestSnapshot? _oiSource, _oiPublished;
    private long _oiPublishedRevision = -1;
    public bool IsDelayed => _delayed.Count != 0;
    public void Error(long id, string code) { _errors[id] = code; _revision++; }
    public void Received(long id, bool delayed, bool hasData)
    {
        if (delayed) _delayed.Add(id); else _delayed.Remove(id);
        if (hasData && _errors.Remove(id)) _revision++;
    }
    public void Retain(IReadOnlyList<OptionContractDescriptor> contracts)
    {
        var ids = contracts.Select(c => c.ConId).ToHashSet();
        foreach (var id in _errors.Keys.Where(id => !ids.Contains(id)).ToArray()) { _errors.Remove(id); _revision++; }
        _delayed.RemoveWhere(id => !ids.Contains(id));
    }
    public OptionFlowSnapshot Apply(OptionFlowSnapshot frame)
    {
        if (_errors.Count == 0) return frame;
        if (ReferenceEquals(_source, frame) && _publishedRevision == _revision) return _published!;
        var status = frame.Rows.Any(r => r.CallPremium.HasValue || r.PutPremium.HasValue) ? OptionDataStatus.Partial
            : _errors.Values.Contains("LINE_LIMIT") ? OptionDataStatus.LineLimit
            : _errors.Values.Contains("NO_PERMISSION") ? OptionDataStatus.NoPermission : OptionDataStatus.Frozen;
        _source = frame; _publishedRevision = _revision;
        return _published = frame with { Status = status,
            Message = frame.Message + $"；订阅异常 {_errors.Count}（等待对应合约恢复）" };
    }
    public OptionOpenInterestSnapshot Apply(OptionOpenInterestSnapshot frame)
    {
        // Confirmed daily OI remains valid even if the live Flow subscription fails.
        if (_errors.Count == 0 || frame.Status == OptionDataStatus.Daily) return frame;
        if (ReferenceEquals(_oiSource, frame) && _oiPublishedRevision == _revision) return _oiPublished!;
        var populated = frame.Rows.Any(r => r.CallOpenInterest.HasValue || r.PutOpenInterest.HasValue);
        var status = populated ? OptionDataStatus.Partial
            : _errors.Values.Contains("LINE_LIMIT") ? OptionDataStatus.LineLimit
            : _errors.Values.Contains("NO_PERMISSION") ? OptionDataStatus.NoPermission : OptionDataStatus.Frozen;
        _oiSource = frame; _oiPublishedRevision = _revision;
        return _oiPublished = frame with { Status = status, Message = frame.Message + $"；订阅异常 {_errors.Count}（保留已收到的日 OI）" };
    }
}
