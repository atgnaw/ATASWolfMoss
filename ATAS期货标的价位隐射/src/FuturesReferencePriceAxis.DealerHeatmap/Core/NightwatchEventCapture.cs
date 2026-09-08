using WolfMoss.MarketData;

namespace WolfMoss.ATAS.PriceMapping.Core;

// Called INSIDE shared download completion, never from a chart's cached snapshot/render path.
internal static class NightwatchEventCapture
{
    private static readonly Guid StreamId = Guid.NewGuid();
    public static DealerHeatmapFrame Heatmap(DealerHeatmapFrame frame, DateTime receivedUtc)
    {
        if (MarketEventHub.Current is { } recorder)
        {
            try
            {
                recorder.Publish(new(1, recorder.RunId, recorder.NextSequence(), MarketEventSource.Nightwatch,
                    MarketEventKind.DealerHeatmap, StreamId, 1, 0, frame.Ticker, frame.Expiration, null,
                    DealerSamplingTime.FromBucketStartUtc(frame.MinuteAtUtc), receivedUtc, null,
                    MarketTradeScope.NotApplicable, MarketEventQuality.SourceContinuityUnknown,
                    Provider: new(frame.SpotUsd, frame.Cells.Select(static c => new MarketStrikeValue(c.StrikeUsd, c.NetDealerGexUsd)), frame.MinuteAtUtc)));
            }
            catch { recorder.CaptureFailed(); /* History cannot invalidate an otherwise valid provider frame. */ }
        }
        return frame;
    }
    public static DealerGexFrame DealerGex(DealerGexFrame frame, DateTime receivedUtc)
    {
        if (MarketEventHub.Current is { } recorder)
        {
            try
            {
                var s = frame.Summary;
                recorder.Publish(new(1, recorder.RunId, recorder.NextSequence(), MarketEventSource.Nightwatch,
                    MarketEventKind.DealerGex, StreamId, 1, 0, frame.Ticker, null, null,
                    DealerSamplingTime.FromBucketStartUtc(frame.SnapshotAtUtc), receivedUtc,
                    new(frame.SessionDateEt, null, null, null, "America/New_York"), MarketTradeScope.NotApplicable,
                    MarketEventQuality.SourceContinuityUnknown,
                    Provider: new(frame.SpotUsd, frame.Nodes.Select(static n => new MarketStrikeValue(n.StrikeUsd,
                        n.NetGexUsd, n.NodeType, n.Rank, n.RelativeStrength)), frame.SnapshotAtUtc,
                        new(s.TotalGexUsd, s.KingStrikeUsd, s.GammaFlipUsd, s.CallWallStrikeUsd, s.PutWallStrikeUsd,
                            s.MajorPositiveStrikeUsd, s.MajorNegativeStrikeUsd), frame.ApiState.ToLowerInvariant())));
            }
            catch { recorder.CaptureFailed(); }
        }
        return frame;
    }
}
