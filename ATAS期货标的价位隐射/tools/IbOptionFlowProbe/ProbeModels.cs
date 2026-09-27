using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using WolfMoss.ATAS.PriceMapping;
using WolfMoss.ATAS.PriceMapping.Core;

namespace IbOptionFlowProbe;

internal static class ProbeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}

internal sealed record ProbeConfig
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 4001;
    public int ClientId { get; init; } = 2290;
    public int DurationMinutes { get; init; } = 30;
    public int StrikesEachSide { get; init; } = 1;
    [JsonIgnore] public int RequiredMarketDataLines => ReferenceSpots.Count * (2 * StrikesEachSide + 1) * 2;
    public Dictionary<string, decimal> ReferenceSpots { get; init; } = new();
    public string OutputDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WolfMoss", "IbOptionFlowProbe", "runs");
    public void Validate()
    {
        if (StrikesEachSide is < 0 or > 20)
            throw new ArgumentException("ATM 上下各档数需为 0–20 的整数（0 表示只采 ATM）。");
        if (string.IsNullOrWhiteSpace(Host) || Port is < 1 or > 65535 || ClientId <= 0
            || DurationMinutes is < 1 or > 240 || ReferenceSpots.Count is < 1 or > 2
            || string.IsNullOrWhiteSpace(OutputDirectory)) throw new ArgumentException("配置无效：检查连接、时长和参考现价。");
        if (ReferenceSpots.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ReferenceSpots.Count
            || ReferenceSpots.Any(x => !OptionUnderlyingProfile.TryResolve(x.Key, out _) || x.Value <= 0))
            throw new ArgumentException("只支持 QQQ/SPX，各填一个正数参考现价。");
    }
}

internal sealed record ProbeTrade(decimal Price, decimal Size, long SourceUnixMilliseconds,
    DateTime SourceUtc, decimal TotalVolume, decimal Vwap, bool SingleMarketMaker);
internal sealed record ProbeEvent(int SchemaVersion, Guid RunId, int ConnectionEpoch,
    IbRawMarketEvent Raw, ProbeTrade? Trade, string? ParseError);
internal sealed record ProbeContract(OptionContractDescriptor Contract, OptionTradingSegment Segment,
    string Session, decimal ReferenceSpot, decimal AtmStrike);
internal sealed record ProbeManifest(int SchemaVersion, Guid RunId, string ProgramVersion,
    DateTime CreatedUtc, long MonotonicFrequency, ProbeConfig Config,
    IReadOnlyList<ProbeContract> Contracts, bool Synthetic = false);
internal sealed record CaptureSummary(Guid RunId, DateTime EndedUtc, string StopReason,
    bool Complete, long Accepted, long Written, long Dropped, long QueuePeak, long Bytes,
    long RawObservationFailures, string? WriterFailure, DateTime? CaptureStartedUtc = null);
