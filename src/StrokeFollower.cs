using System;
using System.Collections.Generic;

namespace StickyInk;

/// <summary>
/// Decides, for each stroke, how far the content under it just scrolled, and
/// moves the stroke by that amount. Runs on the UI thread.
///
/// Not everything on screen scrolls together (two windows side by side, a fixed
/// header above a scrolling page...), so every stroke gets its own verdict. The
/// evidence is looked for in widening circles: right around the stroke first,
/// then its column, then its whole window, then the whole screen.
/// </summary>
internal sealed class StrokeFollower
{
    readonly Grid grid;
    readonly InkController ink;
    readonly AnchorRelocator anchors;

    // Scratch space for Tally: votes per shift, indexed by shift + grid.Height.
    readonly int[] votesByShift;
    readonly List<int> usedShifts = new List<int>();

    // ---- tuning -------------------------------------------------------------
    const int NearbyRows = 250;          // "right around the stroke" = this many rows above and below it
    const int MinStaticRows = 40;        // a "did not scroll" verdict needs at least this much evidence

    public StrokeFollower(Grid grid, InkController ink, AnchorRelocator anchors)
    {
        this.grid = grid;
        this.ink = ink;
        this.anchors = anchors;
        votesByShift = new int[2 * grid.Height + 1];
    }

    /// <summary>The result of counting votes in a region.</summary>
    readonly struct Verdict
    {
        public readonly int Shift;   // the most popular shift
        public readonly int Votes;   // how many cells voted for it
        public readonly int Rows;    // how many cells had an opinion at all
        public Verdict(int shift, int votes, int rows) { Shift = shift; Votes = votes; Rows = rows; }

        /// <summary>True if at least minRows cells voted and at least percent% of them agree on Shift.</summary>
        public bool IsConfident(int minRows, int percent) => Rows >= minRows && Votes * 100 >= Rows * percent;
    }

    public void Apply(FrameMatch match)
    {
        bool anyMoved = false;
        CellRange screen = CellRange.FullScreen(grid);
        Verdict? wholeScreen = null;

        foreach (Stroke s in ink.Strokes)
        {
            bool hasPane = Pane.TryGetCells(s.Pane, grid, out CellRange pane);

            // The stroke's own bands plus one on each side, limited to its pane.
            int band0 = Math.Max(pane.Band0, grid.ClampBand((int)(s.MinX / Grid.BandWidth) - 1));
            int band1 = Math.Min(pane.Band1, grid.ClampBand((int)(s.MaxX / Grid.BandWidth) + 1));
            var nearby = new CellRange(band0, band1,
                                       Math.Max(pane.Row0, (int)s.Top - NearbyRows),
                                       Math.Min(pane.Row1, (int)s.Bottom + NearbyRows));
            var column = new CellRange(band0, band1, pane.Row0, pane.Row1);

            // 1) Right around the stroke. Unchanged cells count here: they are how a
            //    stroke on something that is NOT scrolling knows to stay put.
            Verdict v = Tally(match, nearby, changedOnly: false);
            bool confident = v.IsConfident(6, 60);
            int shift = v.Shift;

            // A few unchanged cells (a window button, a thin border) are not enough to say "static".
            if (confident && shift == 0 && v.Rows < MinStaticRows) confident = false;

            // A stroke that is known to scroll with the page must not get stuck when it comes
            // near something fixed (a sticky header): if the content in its column is moving,
            // follow that.
            if (confident && shift == 0 && s.HasScrolled)
            {
                Verdict c = Tally(match, column, changedOnly: true);
                if (c.IsConfident(MinStaticRows, 50) && c.Shift != 0) shift = c.Shift;
            }

            // From here on only CHANGED cells vote. Fixed chrome would otherwise outvote the
            // scrolling content of a stroke drawn in a blank margin.
            // 2) The stroke's column, top to bottom of the pane.
            if (!confident)
            {
                v = Tally(match, column, changedOnly: true);
                confident = v.IsConfident(10, 50);
                shift = v.Shift;
            }
            // 3) The whole pane.
            if (!confident && hasPane)
            {
                v = Tally(match, pane, changedOnly: true);
                confident = v.IsConfident(10, 40);
                shift = v.Shift;
            }
            // 4) The whole screen (only when the pane is unknown).
            if (!confident && !hasPane)
            {
                wholeScreen ??= Tally(match, screen, changedOnly: true);
                confident = wholeScreen.Value.IsConfident(30, 40);
                shift = wholeScreen.Value.Shift;
            }

            if (confident && shift != 0)
            {
                s.OffsetY += shift;
                s.HasScrolled = true;
                anyMoved = true;
                anchors.DropStaticCells(s, match);
            }
        }

        if (anyMoved) ink.RedrawEverything();
    }

    /// <summary>
    /// Counts the candidate shifts of all cells in a region.
    /// With changedOnly, cells whose content did not change are skipped, and cells
    /// that changed without a match count as "had an opinion" (they dilute the result).
    /// </summary>
    Verdict Tally(FrameMatch match, CellRange range, bool changedOnly)
    {
        if (range.IsEmpty) return default;
        const int K = FrameMatch.MaxCandidates;
        int height = grid.Height, rows = 0;
        usedShifts.Clear();

        for (int band = range.Band0; band <= range.Band1; band++)
        {
            int first = band * height;
            for (int row = range.Row0; row <= range.Row1; row++)
            {
                int cell = first + row, count = match.Counts[cell];
                if (count == 0) continue;
                if (count == FrameMatch.ChangedNoMatch)
                {
                    if (changedOnly) rows++;
                    continue;
                }
                if (changedOnly && match.Candidates[cell * K] == 0) continue;

                rows++;
                for (int k = 0; k < count; k++)
                {
                    int index = match.Candidates[cell * K + k] + height;
                    if (votesByShift[index]++ == 0) usedShifts.Add(index);
                }
            }
        }

        int bestVotes = 0, bestIndex = height;
        foreach (int index in usedShifts)
        {
            if (votesByShift[index] > bestVotes) { bestVotes = votesByShift[index]; bestIndex = index; }
            votesByShift[index] = 0;
        }
        return new Verdict(bestIndex - height, bestVotes, rows);
    }
}
