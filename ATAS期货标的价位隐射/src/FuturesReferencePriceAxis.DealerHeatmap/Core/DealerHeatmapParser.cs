namespace WolfMoss.ATAS.PriceMapping.Core;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

public static class DealerHeatmapParser
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false
    };

    public static DealerHeatmapFrame ParseLatest(
        ReadOnlySpan<byte> json,
        string ticker,
        DateOnly targetExpiration)
    {
        DealerHeatmapHistoryResponseDto? response;

        try
        {
            response = JsonSerializer.Deserialize<DealerHeatmapHistoryResponseDto>(
                json,
                SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new DealerHeatmapDataException(
                "InvalidJson",
                "Nightwatch JSON 无效",
                innerException: exception);
        }

        var row = response?.Data?.FirstOrDefault()
                  ?? throw new DealerHeatmapDataException(
                      "EmptyData",
                      "Nightwatch 暂无目标到期日数据");

        if (!DateOnly.TryParseExact(
                row.Expiration,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var expiration)
            || expiration != targetExpiration)
        {
            throw new DealerHeatmapDataException(
                "ExpirationMismatch",
                "Nightwatch 返回了非目标到期日数据");
        }

        if (!DateTimeOffset.TryParse(
                row.MinuteAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var minuteAt))
        {
            throw new DealerHeatmapDataException(
                "InvalidMinute",
                "Nightwatch minute_at 无效");
        }

        if (row.SpotUsd is not > 0m)
        {
            throw new DealerHeatmapDataException(
                "InvalidSpot",
                "Nightwatch spot_usd 无效");
        }

        if (row.Cells is not { Count: > 0 })
        {
            throw new DealerHeatmapDataException(
                "EmptyCells",
                "Nightwatch 暂无有效热力图节点");
        }

        var strikes = new HashSet<decimal>();
        var cells = new List<DealerHeatmapCell>(row.Cells.Count);

        foreach (var cell in row.Cells)
        {
            if (cell.StrikeUsd is not > 0m || cell.NetDealerGexUsd is null or 0m)
            {
                throw new DealerHeatmapDataException(
                    "InvalidCell",
                    "Nightwatch 热力图节点无效");
            }

            if (!strikes.Add(cell.StrikeUsd.Value))
            {
                throw new DealerHeatmapDataException(
                    "DuplicateStrike",
                    "Nightwatch 返回了重复执行价");
            }

            cells.Add(new DealerHeatmapCell(
                cell.StrikeUsd.Value,
                cell.NetDealerGexUsd.Value));
        }

        cells.Sort(static (left, right) => left.StrikeUsd.CompareTo(right.StrikeUsd));
        return new DealerHeatmapFrame(
            ticker.ToUpperInvariant(),
            expiration,
            minuteAt.UtcDateTime,
            row.SpotUsd.Value,
            cells);
    }

    public static DealerHeatmapFrame ParseSnapshot(
        ReadOnlySpan<byte> json,
        string ticker,
        DateOnly targetExpiration)
    {
        DealerHeatmapSnapshotResponseDto? response;

        try
        {
            response = JsonSerializer.Deserialize<DealerHeatmapSnapshotResponseDto>(
                json,
                SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new DealerHeatmapDataException(
                "InvalidJson",
                "Nightwatch snapshot JSON 无效",
                innerException: exception);
        }

        var data = response?.Data
                   ?? throw new DealerHeatmapDataException(
                       "EmptyData",
                       "Nightwatch snapshot 暂无数据");

        if (!string.Equals(data.Ticker, ticker, StringComparison.OrdinalIgnoreCase))
        {
            throw new DealerHeatmapDataException(
                "TickerMismatch",
                "Nightwatch snapshot 返回了非目标品种");
        }

        if (!DateTimeOffset.TryParse(
                data.GeneratedAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var generatedAt))
        {
            throw new DealerHeatmapDataException(
                "InvalidMinute",
                "Nightwatch generated_at 无效");
        }

        if (data.SpotUsd is not > 0m)
        {
            throw new DealerHeatmapDataException(
                "InvalidSpot",
                "Nightwatch spot_usd 无效");
        }

        var expirationText = targetExpiration.ToString(
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture);
        var targetCells = data.Cells?
            .Where(cell => string.Equals(
                cell.Expiration,
                expirationText,
                StringComparison.Ordinal))
            .ToArray();

        if (targetCells is not { Length: > 0 })
        {
            throw new DealerHeatmapDataException(
                "EmptyCells",
                "Nightwatch snapshot 暂无目标到期日节点");
        }

        var strikes = new HashSet<decimal>();
        var cells = new List<DealerHeatmapCell>(targetCells.Length);

        foreach (var cell in targetCells)
        {
            if (cell.StrikeUsd is not > 0m || cell.NetDealerGexUsd is null or 0m)
            {
                throw new DealerHeatmapDataException(
                    "InvalidCell",
                    "Nightwatch snapshot 热力图节点无效");
            }

            if (!strikes.Add(cell.StrikeUsd.Value))
            {
                throw new DealerHeatmapDataException(
                    "DuplicateStrike",
                    "Nightwatch snapshot 返回了重复执行价");
            }

            cells.Add(new DealerHeatmapCell(
                cell.StrikeUsd.Value,
                cell.NetDealerGexUsd.Value));
        }

        cells.Sort(static (left, right) => left.StrikeUsd.CompareTo(right.StrikeUsd));
        return new DealerHeatmapFrame(
            ticker.ToUpperInvariant(),
            targetExpiration,
            generatedAt.UtcDateTime,
            data.SpotUsd.Value,
            cells);
    }

    private sealed class DealerHeatmapSnapshotResponseDto
    {
        [JsonPropertyName("data")]
        public DealerHeatmapSnapshotDataDto? Data { get; init; }
    }

    private sealed class DealerHeatmapSnapshotDataDto
    {
        [JsonPropertyName("ticker")]
        public string? Ticker { get; init; }

        [JsonPropertyName("generated_at")]
        public string? GeneratedAt { get; init; }

        [JsonPropertyName("session_date_et")]
        public string? SessionDateEt { get; init; }

        [JsonPropertyName("spot_usd")]
        public decimal? SpotUsd { get; init; }

        [JsonPropertyName("expirations")]
        public List<string>? Expirations { get; init; }

        [JsonPropertyName("cells")]
        public List<DealerHeatmapCellDto>? Cells { get; init; }
    }

    private sealed class DealerHeatmapHistoryResponseDto
    {
        [JsonPropertyName("data")]
        public List<DealerHeatmapRowDto>? Data { get; init; }
    }

    private sealed class DealerHeatmapRowDto
    {
        [JsonPropertyName("minute_at")]
        public string? MinuteAt { get; init; }

        [JsonPropertyName("expiration")]
        public string? Expiration { get; init; }

        [JsonPropertyName("spot_usd")]
        public decimal? SpotUsd { get; init; }

        [JsonPropertyName("cells")]
        public List<DealerHeatmapCellDto>? Cells { get; init; }
    }

    private sealed class DealerHeatmapCellDto
    {
        [JsonPropertyName("strike_usd")]
        public decimal? StrikeUsd { get; init; }

        [JsonPropertyName("expiration")]
        public string? Expiration { get; init; }

        [JsonPropertyName("net_dealer_gex_usd")]
        public decimal? NetDealerGexUsd { get; init; }
    }
}
