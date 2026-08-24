namespace WolfMoss.ATAS.PriceMapping.Core;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

public static class DealerGexParser
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false
    };

    public static DealerGexFrame ParseSnapshot(
        ReadOnlySpan<byte> json,
        string expectedTicker)
    {
        DealerGexResponseDto? response;

        try
        {
            response = JsonSerializer.Deserialize<DealerGexResponseDto>(
                json,
                SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new DealerGexDataException(
                "InvalidJson",
                "Nightwatch Dealer GEX JSON 无效",
                innerException: exception);
        }

        var data = response?.Data
                   ?? throw new DealerGexDataException(
                       "EmptyData",
                       "Nightwatch Dealer GEX 暂无数据");
        var ticker = expectedTicker.ToUpperInvariant();

        if (!string.Equals(data.Ticker, ticker, StringComparison.OrdinalIgnoreCase))
        {
            throw new DealerGexDataException(
                "TickerMismatch",
                "Nightwatch Dealer GEX 返回了非目标品种");
        }

        if (!DateTimeOffset.TryParse(
                data.SnapshotAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var snapshotAt))
        {
            throw new DealerGexDataException(
                "InvalidSnapshotTime",
                "Nightwatch Dealer GEX snapshot_at 无效");
        }

        if (!DateOnly.TryParseExact(
                data.SessionDateEt,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var sessionDateEt))
        {
            throw new DealerGexDataException(
                "InvalidSessionDate",
                "Nightwatch Dealer GEX session_date_et 无效");
        }

        if (string.IsNullOrWhiteSpace(data.State))
        {
            throw new DealerGexDataException(
                "InvalidState",
                "Nightwatch Dealer GEX state 无效");
        }

        if (data.SpotUsd is not > 0m)
        {
            throw new DealerGexDataException(
                "InvalidSpot",
                "Nightwatch Dealer GEX spot_usd 无效");
        }

        if (data.Strikes is { Count: > 5 })
        {
            throw new DealerGexDataException(
                "TooManyNodes",
                "Nightwatch Dealer GEX 节点数超过 5 个");
        }

        var sourceNodes = data.Strikes ?? [];
        var strikeSet = new HashSet<decimal>();
        var nodes = new List<DealerGexNode>(sourceNodes.Count);

        foreach (var node in sourceNodes)
        {
            if (node.StrikeUsd is not > 0m
                || !node.NetGexUsd.HasValue
                || node.Rank is not (>= 1 and <= 5)
                || node.RelativeStrength is not (>= 0m and <= 1m))
            {
                throw new DealerGexDataException(
                    "InvalidNode",
                    "Nightwatch Dealer GEX 节点无效");
            }

            if (!strikeSet.Add(node.StrikeUsd.Value))
            {
                throw new DealerGexDataException(
                    "DuplicateStrike",
                    "Nightwatch Dealer GEX 返回了重复执行价");
            }

            nodes.Add(new DealerGexNode(
                node.StrikeUsd.Value,
                node.NetGexUsd.Value,
                string.IsNullOrWhiteSpace(node.NodeType)
                    ? "standard"
                    : node.NodeType.Trim().ToLowerInvariant(),
                node.Rank.Value,
                node.RelativeStrength.Value));
        }

        nodes.Sort(static (left, right) => left.StrikeUsd.CompareTo(right.StrikeUsd));
        var summary = data.Summary;
        ValidateOptionalStrike(summary?.KingStrikeUsd, "king_strike_usd");
        ValidateOptionalStrike(summary?.GammaFlipUsd, "gamma_flip_usd");
        ValidateOptionalStrike(summary?.CallWallStrikeUsd, "call_wall_strike_usd");
        ValidateOptionalStrike(summary?.PutWallStrikeUsd, "put_wall_strike_usd");
        ValidateOptionalStrike(summary?.MajorPositiveStrikeUsd, "major_positive_strike_usd");
        ValidateOptionalStrike(summary?.MajorNegativeStrikeUsd, "major_negative_strike_usd");

        return new DealerGexFrame(
            ticker,
            snapshotAt.UtcDateTime,
            sessionDateEt,
            data.State.Trim().ToLowerInvariant(),
            data.SpotUsd.Value,
            nodes,
            new DealerGexSummary(
                summary?.TotalGexUsd,
                summary?.KingStrikeUsd,
                summary?.GammaFlipUsd,
                summary?.CallWallStrikeUsd,
                summary?.PutWallStrikeUsd,
                summary?.MajorPositiveStrikeUsd,
                summary?.MajorNegativeStrikeUsd));
    }

    private static void ValidateOptionalStrike(decimal? value, string field)
    {
        if (value.HasValue && value.Value <= 0m)
        {
            throw new DealerGexDataException(
                "InvalidSummary",
                $"Nightwatch Dealer GEX {field} 无效");
        }
    }

    private sealed class DealerGexResponseDto
    {
        [JsonPropertyName("data")]
        public DealerGexDataDto? Data { get; init; }
    }

    private sealed class DealerGexDataDto
    {
        [JsonPropertyName("ticker")]
        public string? Ticker { get; init; }

        [JsonPropertyName("snapshot_at")]
        public string? SnapshotAt { get; init; }

        [JsonPropertyName("session_date_et")]
        public string? SessionDateEt { get; init; }

        [JsonPropertyName("state")]
        public string? State { get; init; }

        [JsonPropertyName("spot_usd")]
        public decimal? SpotUsd { get; init; }

        [JsonPropertyName("strikes")]
        public List<DealerGexNodeDto>? Strikes { get; init; }

        [JsonPropertyName("summary")]
        public DealerGexSummaryDto? Summary { get; init; }
    }

    private sealed class DealerGexNodeDto
    {
        [JsonPropertyName("strike_usd")]
        public decimal? StrikeUsd { get; init; }

        [JsonPropertyName("net_gex_usd")]
        public decimal? NetGexUsd { get; init; }

        [JsonPropertyName("node_type")]
        public string? NodeType { get; init; }

        [JsonPropertyName("rank")]
        public int? Rank { get; init; }

        [JsonPropertyName("relative_strength")]
        public decimal? RelativeStrength { get; init; }
    }

    private sealed class DealerGexSummaryDto
    {
        [JsonPropertyName("total_gex_usd")]
        public decimal? TotalGexUsd { get; init; }

        [JsonPropertyName("king_strike_usd")]
        public decimal? KingStrikeUsd { get; init; }

        [JsonPropertyName("gamma_flip_usd")]
        public decimal? GammaFlipUsd { get; init; }

        [JsonPropertyName("call_wall_strike_usd")]
        public decimal? CallWallStrikeUsd { get; init; }

        [JsonPropertyName("put_wall_strike_usd")]
        public decimal? PutWallStrikeUsd { get; init; }

        [JsonPropertyName("major_positive_strike_usd")]
        public decimal? MajorPositiveStrikeUsd { get; init; }

        [JsonPropertyName("major_negative_strike_usd")]
        public decimal? MajorNegativeStrikeUsd { get; init; }
    }
}
