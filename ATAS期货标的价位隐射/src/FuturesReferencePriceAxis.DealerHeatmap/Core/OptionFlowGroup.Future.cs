namespace WolfMoss.ATAS.PriceMapping.Core;

public sealed record FlowFutureBufferSnapshot(int Pending, long Buffered, long Released, long Discarded, double MaxLeadMilliseconds);

internal sealed partial class OptionFlowGroup
{
    internal static readonly TimeSpan FutureLeadLimit = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan FutureResidenceLimit = TimeSpan.FromSeconds(3);
    internal const int FuturePerContractLimit = 128;
    internal const int FutureTotalLimit = 4096;
    private readonly record struct PendingFuture(OptionCumulativeSample Sample, DateTime Received);
    private readonly Dictionary<long, Queue<PendingFuture>> _future = new();
    private readonly List<long> _futureKeys = new();
    private int _futurePending;
    private long _futureBuffered, _futureReleased, _futureDiscarded;
    private double _futureMaxLead;

    public FlowFutureBufferSnapshot FutureSnapshot()
    {
        lock (_sync) return new(_futurePending, _futureBuffered, _futureReleased, _futureDiscarded, _futureMaxLead);
    }

    public FlowSampleDisposition ReceiveDetailed(OptionCumulativeSample sample, DateTime received)
    {
        lock (_sync)
        {
            // Pump before successors. Never change the original source timestamp.
            DrainFuture(received);
            if (!sample.HasValidCumulativeValue()) return FlowSampleDisposition.Invalid;
            if (sample.SampleUtc - received > FutureLeadLimit) return FlowSampleDisposition.FutureSource;
            if (!_future.TryGetValue(sample.ConId, out var queue) && sample.SampleUtc <= received)
                return ApplyReceived(sample, received);
            if (_observers == 0) return FlowSampleDisposition.ObservationGap;
            if (sample.SampleUtc < _created) return FlowSampleDisposition.BeforeCoverage;
            if (!_series.TryGetValue(sample.ConId, out var series) || !series.Active) return FlowSampleDisposition.OutsideLadder;
            if (series.Runs.Count > 0 && sample.SampleUtc < series.Runs[^1].Start) return FlowSampleDisposition.BeforeCoverage;
            var inSegment = false;
            foreach (var segment in _segments) if (segment.Contains(sample.SampleUtc)) { inSegment = true; break; }
            if (!inSegment) return FlowSampleDisposition.OutsideSession;
            // A follower may deliver its captured callback after another chart has
            // already drained it. Remember the admitted timestamp across drains.
            if (series.LastDeferredSource == sample.SampleUtc) return FlowSampleDisposition.Duplicate;
            if (sample.SampleUtc < series.LastDeferredSource) return FlowSampleDisposition.OutOfOrder;
            if (queue != null)
                foreach (var pending in queue)
                    if (pending.Sample.SampleUtc == sample.SampleUtc) return FlowSampleDisposition.Duplicate;
            if ((queue?.Count ?? 0) >= FuturePerContractLimit || _futurePending >= FutureTotalLimit)
            {
                _futureDiscarded++;
                RetireCore(sample.ConId, received); // Fresh observation on next configuration.
                return FlowSampleDisposition.FutureBufferOverflow;
            }
            if (queue == null) _future.Add(sample.ConId, queue = new());
            queue.Enqueue(new(sample, received));
            series.LastDeferredSource = sample.SampleUtc;
            _futurePending++; _futureBuffered++;
            _futureMaxLead = Math.Max(_futureMaxLead, (sample.SampleUtc - received).TotalMilliseconds);
            return FlowSampleDisposition.BufferedFuture;
        }
    }

    // Scheduled publication pumps this even when no new market callback arrives.
    // All callers hold the group lock. No timer, I/O or per-sample task.
    private void DrainFuture(DateTime now)
    {
        if (_futurePending == 0) return;
        _futureKeys.Clear();
        foreach (var id in _future.Keys) _futureKeys.Add(id);
        foreach (var id in _futureKeys)
        {
            if (!_future.TryGetValue(id, out var queue)) continue;
            while (queue.TryPeek(out var pending))
            {
                if (now - pending.Received > FutureResidenceLimit)
                {
                    RetireCore(id, now); // Do not subtract across an expired queue.
                    break;
                }
                if (pending.Sample.SampleUtc > now) break;
                queue.Dequeue(); _futurePending--;
                // Eligibility was checked against real UTC above. Advance rolling
                // event state to the original event, not past a session boundary
                // merely because this pump ran after close. Receipt evidence is unchanged.
                var disposition = ApplyReceived(pending.Sample, pending.Sample.SampleUtc);
                if (disposition == FlowSampleDisposition.Accepted) _futureReleased++;
                else if (disposition != FlowSampleDisposition.Duplicate) _futureDiscarded++;
            }
            if (queue.Count == 0) _future.Remove(id);
        }
    }

    private void DropFuture(long conId)
    {
        if (!_future.Remove(conId, out var queue)) return;
        _futurePending -= queue.Count; _futureDiscarded += queue.Count;
    }
    private void DropAllFuture()
    {
        _futureDiscarded += _futurePending;
        _futurePending = 0; _future.Clear();
    }
}
