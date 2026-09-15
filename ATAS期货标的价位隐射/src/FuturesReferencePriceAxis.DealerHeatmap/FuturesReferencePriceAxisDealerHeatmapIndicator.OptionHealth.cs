namespace WolfMoss.ATAS.PriceMapping;

using WolfMoss.ATAS.PriceMapping.Core;

public sealed partial class FuturesReferencePriceAxisDealerHeatmapIndicator
{
    private OptionInputHealth? _optionInputHealth;
    private OptionInputHealth InputHealth => _optionInputHealth ??= new();

    private void ReconfigureOpenInterestVisibility()
    {
        if (!_showOptionPremiumFlow) { ReconfigureOptionData(); return; }
        if (!IsIndicatorInitialized) return;
        // Flow always includes tick 101 on its existing lines. A display toggle
        // must not cancel the generation, subscription or observation lease.
        lock (_optionDataSync)
        {
            _oiRevision++;
            if (_showOptionOpenInterest) RefreshOiConfirmationEpoch(RealtimeUtcNow());
        }
        if (!_showOptionOpenInterest)
            SetOptionOpenInterestSnapshot(OptionOpenInterestSnapshot.Disabled(RealtimeUtcNow()));
        RequestRedraw();
    }
}
