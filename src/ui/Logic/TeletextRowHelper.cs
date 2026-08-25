namespace Nikse.SubtitleEdit.Logic;

/// <summary>
/// Row math for the EBU STL teletext workflow (1-based rows, row 23 is the bottom physical row).
/// The stored/displayed VerticalPosition is the first physical row occupied by the subtitle.
/// </summary>
public static class TeletextRowHelper
{
    public const int BottomRow = 23;

    /// <summary>
    /// The last valid start row for a subtitle.
    /// A double-height text row occupies two physical teletext rows, so the last valid
    /// start position is 22 (occupying 22+23). Single-height may start on row 23.
    /// </summary>
    public static int GetMaximumStartRow(bool doubleHeight)
    {
        return doubleHeight
            ? BottomRow - 1
            : BottomRow;
    }

    /// <summary>
    /// The teletext row a bottom-anchored subtitle with the given number of text lines starts on.
    /// With double height: one text line starts on 22 (occupies 22+23);
    /// two text lines start on 20 (occupy 20+21 and 22+23).
    /// With single height: one line starts on 23 and two lines on 22.
    /// </summary>
    public static int GetBottomStartRow(
        int lineCount,
        bool doubleHeight)
    {
        if (lineCount < 1)
        {
            lineCount = 1;
        }

        var rowsPerLine =
            doubleHeight ? 2 : 1;

        return BottomRow -
               rowsPerLine * lineCount +
               1;
    }

    /// <summary>
    /// Normalizes a requested teletext start row so the complete first text row remains
    /// inside the 23-row page. This mainly converts the legacy double-height start row
    /// 23 to 22.
    /// </summary>
    public static int NormalizeStartRow(
        int row,
        bool doubleHeight)
    {
        return System.Math.Clamp(
            row,
            1,
            GetMaximumStartRow(
                doubleHeight));
    }

    /// <summary>
    /// When a text edit changes the number of lines of a bottom-anchored subtitle, returns the row
    /// that keeps it bottom-anchored. With double height this is 22 &lt;-&gt; 20.
    /// Returns null when the row must be left alone: the subtitle was not on the bottom row for
    /// its previous line count, the line count did not change, or the new row would leave the page.
    /// </summary>
    public static int? GetAdjustedBottomRow(
        string marginV,
        int oldLineCount,
        int newLineCount,
        bool doubleHeight)
    {
        if (oldLineCount == newLineCount ||
            oldLineCount < 1 ||
            newLineCount < 1)
        {
            return null;
        }

        if (!int.TryParse(
                marginV,
                out var row))
        {
            return null;
        }

        row =
            NormalizeStartRow(
                row,
                doubleHeight);

        if (row !=
            GetBottomStartRow(
                oldLineCount,
                doubleHeight))
        {
            return null;
        }

        var newRow =
            GetBottomStartRow(
                newLineCount,
                doubleHeight);

        return newRow >= 1 &&
               newRow <=
               GetMaximumStartRow(
                   doubleHeight)
            ? newRow
            : null;
    }

    /// <summary>
    /// Row for a subtitle whose text is rewritten by a batch tool (split into several events, or
    /// wrapped to another number of lines): the bottom edge stays where it was, so the start row
    /// moves by the rows gained or lost. With double height examples are 20 -> 22 for two lines
    /// becoming one, 18 -> 20, and the inverse.
    ///
    /// Legacy row 23 is normalized to 22 before the calculation, so a double-height subtitle can
    /// never start outside the physical page.
    /// </summary>
    public static int? GetRowKeepingBottomEdge(
        string marginV,
        int oldLineCount,
        int newLineCount,
        bool doubleHeight)
    {
        if (oldLineCount == newLineCount ||
            oldLineCount < 1 ||
            newLineCount < 1)
        {
            return null;
        }

        if (!int.TryParse(
                marginV,
                out var row) ||
            row < 1 ||
            row > BottomRow)
        {
            return null;
        }

        row =
            NormalizeStartRow(
                row,
                doubleHeight);

        var rowsPerLine =
            doubleHeight ? 2 : 1;

        var newRow =
            row +
            (oldLineCount - newLineCount) *
            rowsPerLine;

        return newRow >= 1 &&
               newRow <=
               GetMaximumStartRow(
                   doubleHeight)
            ? newRow
            : null;
    }
}
