namespace WolfMoss.ATAS.PriceMapping.Core;

public static class MappingMath
{
    public static bool TryCalculateRatio(
        decimal futuresClose,
        decimal referenceClose,
        out decimal ratio)
    {
        ratio = 0m;

        if (futuresClose <= 0m || referenceClose <= 0m)
            return false;

        ratio = futuresClose / referenceClose;
        return ratio > 0m;
    }

    public static decimal ToReferencePrice(decimal futuresPrice, decimal ratio)
    {
        if (ratio <= 0m)
            throw new ArgumentOutOfRangeException(nameof(ratio), "Ratio must be positive.");

        return futuresPrice / ratio;
    }

    public static decimal ToFuturesPrice(decimal referencePrice, decimal ratio)
    {
        if (ratio <= 0m)
            throw new ArgumentOutOfRangeException(nameof(ratio), "Ratio must be positive.");

        return referencePrice * ratio;
    }

    public static decimal? SelectEffectiveRatio(
        MappingMode mode,
        decimal manualRatio,
        MappingSnapshot? automatic)
    {
        if (mode == MappingMode.Manual)
            return manualRatio > 0m ? manualRatio : null;

        if (automatic?.Ratio > 0m)
            return automatic.Ratio;

        return manualRatio > 0m ? manualRatio : null;
    }
}
