namespace WolfMoss.ATAS.FootprintOutline.Core;

using System.Drawing;

internal readonly record struct PriceLevel(decimal Price, decimal Volume);
internal readonly record struct ProfileLayout(int Left, int MaximumWidth);

/// <summary>Integer geometry for ATAS 8.0.14.397's primary VolumeHistogram.</summary>
internal static class ProfileGeometry
{
    public static ProfileLayout GetLayout(int barX, decimal barsWidth, bool directionMarker,
        int markerWidth, int offset = 0, decimal widthPercent = 100m)
    {
        if (barsWidth < 1 || barsWidth > 1_000_000 || widthPercent <= 0)
            return default;

        var wholeWidth = (int)barsWidth;
        var profileSpan = Convert.ToInt32(barsWidth * 0.9m);
        if (markerWidth > 0)
            profileSpan = wholeWidth - markerWidth;
        else if (barsWidth >= 5 && barsWidth < 10)
            profileSpan = wholeWidth - 2;
        else if (barsWidth >= 10 && barsWidth <= 20)
            profileSpan = wholeWidth - 3;
        else if (barsWidth > 20 && barsWidth <= 30)
            profileSpan = wholeWidth - 4;
        if (!directionMarker)
            profileSpan = wholeWidth;

        if (profileSpan < 2)
            return default;
        return new ProfileLayout(checked(barX + wholeWidth - profileSpan + offset),
            Math.Max(1, (int)((profileSpan - 1) * widthPercent / 100m)));
    }

    public static int GetWidth(decimal volume, decimal proportion, int maximumWidth)
    {
        if (volume <= 0 || proportion <= 0 || maximumWidth <= 0)
            return 0;
        // Divide before multiplying; even decimal.MaxValue volumes cannot overflow.
        var fraction = volume >= proportion ? 1m : volume / proportion;
        return Math.Max(1, (int)(maximumWidth * fraction));
    }

    public static PriceLevel[] CopyLevels(IEnumerable<PriceLevel> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);

        var sourceCount = levels is ICollection<PriceLevel> collection ? collection.Count : 0;
        var sorted = new List<PriceLevel>(sourceCount);
        foreach (var level in levels)
            if (level.Volume > 0)
                sorted.Add(level);

        return CopyLevelsInPlace(sorted);
    }

    public static PriceLevel[] CopyLevelsInPlace(List<PriceLevel> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);

        if (levels.Count == 0)
            return [];

        levels.Sort(static (left, right) => left.Price.CompareTo(right.Price));
        var result = new PriceLevel[levels.Count];
        var count = 0;
        foreach (var level in levels)
        {
            if (level.Volume <= 0)
                continue;
            if (count > 0 && result[count - 1].Price == level.Price)
                result[count - 1] = result[count - 1] with { Volume = result[count - 1].Volume + level.Volume };
            else
                result[count++] = level;
        }

        if (count != result.Length)
            Array.Resize(ref result, count);
        return result;
    }

    public static Rectangle[] GetRectangles(PriceLevel[] levels, decimal step,
        decimal rowHeight, ProfileLayout layout, decimal proportion, Func<decimal, int> getRowTop)
    {
        if (step <= 0 || rowHeight <= 0 || layout.MaximumWidth <= 0 || proportion <= 0)
            return [];

        var result = new Rectangle[levels.Length];
        var count = 0;
        var previousTop = 0;
        decimal? previousPrice = null;
        var nominalHeight = Math.Max(1, (int)Math.Min(rowHeight, 1_000_000));
        // The native renderer visits ascending prices (bottom to top). For adjacent
        // levels it uses the preceding row's top, preserving fractional-height rounding.
        foreach (var level in levels)
        {
            var top = checked(getRowTop(level.Price) + 1);
            var height = previousPrice.HasValue && level.Price - previousPrice.Value == step
                ? Math.Max(1, previousTop - top)
                : nominalHeight;
            var width = GetWidth(level.Volume, proportion, layout.MaximumWidth);
            if (width > 0)
                result[count++] = new Rectangle(layout.Left, top, width, height);
            previousPrice = level.Price;
            previousTop = top;
        }
        if (count != result.Length)
            Array.Resize(ref result, count);
        return result;
    }

    private readonly record struct EdgeEvent(int Y, int Width, int Change);
    private readonly record struct Band(int Top, int Bottom, int Width);

    /// <summary>Union of rectangles with a common left edge, including subpixel-row overlap.</summary>
    public static Point[][] TraceOutline(IReadOnlyList<Rectangle> rectangles)
    {
        if (rectangles.Count == 0)
            return [];
        if (TryTraceNonOverlapping(rectangles, out var simplePaths))
            return simplePaths;

        var left = rectangles[0].Left;
        var events = new List<EdgeEvent>(rectangles.Count * 2);
        foreach (var rect in rectangles)
        {
            if (rect.Left != left)
                throw new ArgumentException("Profile rectangles must share their left edge.", nameof(rectangles));
            if (rect.Width <= 0 || rect.Height <= 0)
                continue;
            events.Add(new(rect.Top, rect.Width, 1));
            events.Add(new(rect.Bottom, rect.Width, -1));
        }
        events.Sort((a, b) => a.Y.CompareTo(b.Y));
        var active = new SortedDictionary<int, int>();
        var widths = new SortedSet<int>();
        var bands = new List<Band>();
        var paths = new List<Point[]>();
        for (var index = 0; index < events.Count;)
        {
            var y = events[index].Y;
            do
            {
                var edge = events[index++];
                active.TryGetValue(edge.Width, out var count);
                count += edge.Change;
                if (count == 0)
                {
                    active.Remove(edge.Width);
                    widths.Remove(edge.Width);
                }
                else
                {
                    active[edge.Width] = count;
                    widths.Add(edge.Width);
                }
            } while (index < events.Count && events[index].Y == y);

            if (widths.Count == 0)
            {
                FinishRun(left, bands, paths);
                continue;
            }
            if (index == events.Count)
                break;
            var bottom = events[index].Y;
            var width = widths.Max;
            if (bands.Count > 0 && bands[^1].Width == width && bands[^1].Bottom == y)
                bands[^1] = bands[^1] with { Bottom = bottom };
            else
                bands.Add(new(y, bottom, width));
        }
        FinishRun(left, bands, paths);
        return paths.ToArray();
    }

    private static bool TryTraceNonOverlapping(IReadOnlyList<Rectangle> rectangles, out Point[][] paths)
    {
        var left = rectangles[0].Left;
        var direction = 0;
        var previousTop = 0;
        var hasPrevious = false;
        for (var index = 0; index < rectangles.Count; index++)
        {
            var rectangle = rectangles[index];
            if (rectangle.Left != left)
                throw new ArgumentException("Profile rectangles must share their left edge.", nameof(rectangles));
            if (rectangle.Width <= 0 || rectangle.Height <= 0)
                continue;
            if (hasPrevious)
            {
                var currentDirection = Math.Sign(rectangle.Top.CompareTo(previousTop));
                if (currentDirection == 0 || direction != 0 && currentDirection != direction)
                {
                    paths = [];
                    return false;
                }
                direction = currentDirection;
            }
            previousTop = rectangle.Top;
            hasPrevious = true;
        }

        if (!hasPrevious)
        {
            paths = [];
            return true;
        }

        var bands = new List<Band>();
        var result = new List<Point[]>();
        var previousBottom = int.MinValue;
        for (var position = 0; position < rectangles.Count; position++)
        {
            var index = direction < 0 ? rectangles.Count - 1 - position : position;
            var rectangle = rectangles[index];
            if (rectangle.Width <= 0 || rectangle.Height <= 0)
                continue;
            if (rectangle.Top < previousBottom)
            {
                paths = [];
                return false;
            }
            if (rectangle.Top > previousBottom)
                FinishRun(left, bands, result);
            if (bands.Count > 0 && bands[^1].Width == rectangle.Width && bands[^1].Bottom == rectangle.Top)
                bands[^1] = bands[^1] with { Bottom = rectangle.Bottom };
            else
                bands.Add(new(rectangle.Top, rectangle.Bottom, rectangle.Width));
            previousBottom = rectangle.Bottom;
        }
        FinishRun(left, bands, result);
        paths = result.ToArray();
        return true;
    }

    private static void FinishRun(int left, List<Band> bands, List<Point[]> paths)
    {
        if (bands.Count == 0)
            return;
        var points = new List<Point>(bands.Count * 2 + 3) { new(left, bands[0].Top) };
        foreach (var band in bands)
        {
            points.Add(new(left + band.Width, band.Top));
            points.Add(new(left + band.Width, band.Bottom));
        }
        points.Add(new(left, bands[^1].Bottom));
        points.Add(points[0]);
        paths.Add(points.ToArray());
        bands.Clear();
    }
}
