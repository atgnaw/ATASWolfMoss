namespace WolfMoss.ATAS.PriceMapping.Core;

using System.ComponentModel.DataAnnotations;

public enum PairMode
{
    [Display(Name = "Auto / 自动识别")]
    Auto,

    [Display(Name = "NQ/MNQ → QQQ")]
    NqQqq,

    [Display(Name = "ES/MES → SPX")]
    EsSpx
}

public enum MappingMode
{
    [Display(Name = "Automatic / 自动")]
    Automatic,

    [Display(Name = "Manual / 手动")]
    Manual
}

public enum UpdateState
{
    Waiting,
    Fetching,
    Success,
    Failed,
    Frozen,
    Manual
}

public readonly record struct InstrumentPair(
    string FutureRoot,
    string ReferenceSymbol,
    string YahooSymbol,
    int ReferenceDigits);

public sealed record ReferenceMinuteClose(
    string Symbol,
    DateTime MinuteStartUtc,
    decimal Close,
    string Source = "Yahoo");

public sealed record MappingSnapshot(
    InstrumentPair Pair,
    DateTime MinuteStartUtc,
    DateTime MinuteStartMarket,
    decimal FuturesClose,
    decimal ReferenceClose,
    decimal Ratio,
    DateTime AppliedUtcTime);

public sealed record UpdateAttemptSnapshot(
    UpdateState State,
    DateTime AttemptUtcTime,
    string Message)
{
    public static UpdateAttemptSnapshot Waiting(DateTime time, string message = "等待首次更新")
        => new(UpdateState.Waiting, time, message);
}

public readonly record struct MarketTradeSample(DateTime MarketTime, decimal Price);
