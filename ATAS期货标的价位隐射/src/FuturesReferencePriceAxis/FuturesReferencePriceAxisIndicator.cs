namespace WolfMoss.ATAS.PriceMapping;

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Drawing;

using global::ATAS.Indicators;

using OFT.Attributes;
using OFT.Localization;
using OFT.Rendering.Tools;

using WolfMoss.ATAS.PriceMapping.Core;

using DrawingColor = System.Drawing.Color;

[DisplayName("Futures Reference Price Axis / 期货现货映射轴")]
[Category(IndicatorCategories.Other)]
[Description("NQ/MNQ→QQQ and ES/MES→SPX time-aligned left reference price axis.")]
[HelpLink("https://docs.atas.net/en/")]
public sealed partial class FuturesReferencePriceAxisIndicator : Indicator
{
    private static readonly RenderStringFormat CenteredStringFormat = new()
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center
    };

    private static readonly RenderStringFormat RightCenteredStringFormat = new()
    {
        Alignment = StringAlignment.Far,
        LineAlignment = StringAlignment.Center
    };

    private static readonly RenderStringFormat LeftCenteredStringFormat = new()
    {
        Alignment = StringAlignment.Near,
        LineAlignment = StringAlignment.Center
    };

    private static readonly DrawingColor CurrentPriceLabelColor =
        DrawingColor.FromArgb(255, 47, 158, 101);

    private readonly ValueDataSeries _hiddenSeries = new("ReferencePriceAxisHidden")
    {
        VisualType = VisualMode.Hide,
        IsHidden = true,
        ScaleIt = false,
        ShowCurrentValue = false,
        ShowZeroValue = false
    };

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<IReadOnlyList<MarketTradeSample>>>
        _historyRequests = new();
    private readonly object _priceSync = new();
    private readonly object _scheduleSync = new();
    private readonly AxisLayoutCache _axisLayoutCache = new();

    private CancellationTokenSource? _lifetimeCancellation;
    private CancellationTokenSource? _scheduleCancellation;
    private ITradesCache? _tradesCache;
    private MappingSnapshot? _successfulMapping;
    private PendingReferenceSnapshot? _pendingReference;
    private UpdateAttemptSnapshot _latestAttempt =
        UpdateAttemptSnapshot.Waiting(DateTime.MinValue);
    private decimal _latestChartPrice;
    private long _generation;
    private bool _initialized;
    private StatusTextCacheKey? _statusTextCacheKey;
    private string[] _statusTextCache = Array.Empty<string>();
    private CurrentLabelCacheKey? _currentLabelCacheKey;
    private string _currentLabelCache = string.Empty;

    private PairMode _pairMode = Core.PairMode.Auto;
    private MappingMode _mappingMode = Core.MappingMode.Automatic;
    private decimal _manualRatio;
    private int _refreshIntervalMinutes = 30;
    private int _maxQuoteAgeMinutes = 20;
    private bool _showUpdateStatus = true;
    private int _statusPanelOffsetX;
    private int _statusPanelOffsetY;
    private decimal _uiUtcOffsetHours;
    private bool _showCrosshairPriceLabel = true;
    private int _axisWidth = 72;
    private DrawingColor _axisTextColor = DrawingColor.FromArgb(255, 226, 232, 240);
    private DrawingColor _axisBackgroundColor = DrawingColor.FromArgb(210, 22, 27, 34);
    private DrawingColor _axisBorderColor = DrawingColor.FromArgb(255, 92, 105, 121);
    private DrawingColor _statusBackgroundColor = DrawingColor.FromArgb(225, 18, 22, 28);

    public FuturesReferencePriceAxisIndicator()
        : base(true)
    {
        Panel = IndicatorDataProvider.CandlesPanel;
        DenyToChangePanel = true;
        DataSeries[0] = _hiddenSeries;
        DrawAbovePrice = true;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    public override string ToString()
        => $"Reference Axis ({_mappingMode}, {_pairMode})";

    private enum RefreshOutcome
    {
        Completed,
        RetrySoon
    }

    private sealed record PendingReferenceSnapshot(
        long Generation,
        InstrumentPair Pair,
        ReferenceMinuteClose Reference,
        DateTime MarketMinute,
        DateTime FetchedUtc);

    private readonly record struct CurrentLabelCacheKey(
        decimal FuturesPrice,
        decimal Ratio,
        int ReferenceDigits);

    private readonly record struct StatusTextCacheKey(
        InstrumentPair Pair,
        UpdateAttemptSnapshot Attempt,
        MappingSnapshot? Mapping,
        decimal? EffectiveRatio,
        decimal UiUtcOffsetHours);

    private sealed class AtasDataNotReadyException : Exception;

    private sealed class AtasHistoricalRequestException : Exception
    {
        public AtasHistoricalRequestException(Exception innerException)
            : base(innerException.Message, innerException)
        {
        }
    }
}
