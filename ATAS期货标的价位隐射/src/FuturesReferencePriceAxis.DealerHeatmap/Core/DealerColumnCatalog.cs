namespace WolfMoss.ATAS.PriceMapping.Core;

public readonly record struct DealerColumnDefinition(DealerColumnKind Kind, DealerColumnVisibility Visibility);

// Immutable ordered metadata; layout does not need to know how a column gets or draws data.
public sealed class DealerColumnCatalog
{
    private readonly DealerColumnDefinition[] _columns;
    public static DealerColumnCatalog Default { get; } = new(Enum.GetValues<DealerColumnKind>()
        .Select(static kind => new DealerColumnDefinition(kind, (DealerColumnVisibility)(1 << (int)kind))));
    public IReadOnlyList<DealerColumnDefinition> Columns { get; }

    public DealerColumnCatalog(IEnumerable<DealerColumnDefinition> columns)
    {
        _columns = columns.ToArray();
        var kinds = new HashSet<DealerColumnKind>();
        var flags = new HashSet<DealerColumnVisibility>();
        foreach (var column in _columns)
        {
            var bits = (uint)column.Visibility;
            if (bits == 0 || (bits & (bits - 1)) != 0 || !kinds.Add(column.Kind) || !flags.Add(column.Visibility))
                throw new ArgumentException("Columns require unique kinds and unique single-bit visibility flags", nameof(columns));
        }
        Columns = Array.AsReadOnly(_columns);
    }

    public int Count(DealerColumnVisibility visible)
    {
        var count = 0;
        foreach (var column in _columns) if ((visible & column.Visibility) != 0) count++;
        return count;
    }

    public bool TryGetVisibleIndex(DealerColumnVisibility visible, DealerColumnKind kind, out int index)
    {
        index = 0;
        foreach (var column in _columns)
        {
            if ((visible & column.Visibility) == 0) continue;
            if (column.Kind == kind) return true;
            index++;
        }
        index = -1;
        return false;
    }
}
