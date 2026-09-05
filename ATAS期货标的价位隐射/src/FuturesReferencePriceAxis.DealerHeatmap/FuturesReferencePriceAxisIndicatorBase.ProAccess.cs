namespace WolfMoss.ATAS.PriceMapping;

using WolfMoss.ATAS.PriceMapping.Core;

public abstract partial class FuturesReferencePriceAxisIndicatorBase
{
    protected decimal? CurrentEffectiveMappingRatio
        => MappingMath.SelectEffectiveRatio(_mappingMode, _manualRatio,
            Volatile.Read(ref _successfulMapping));
}
