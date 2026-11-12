using System;
using System.Collections.Generic;

namespace StickyInk;

/// <summary>
/// Fixes drift. Following scroll frame by frame is never perfect: scroll a long
/// page far enough and small misses add up, so a stroke would come back a few
/// pixels (or a lot) off.
///
/// The cure is an ANCHOR: shortly after a stroke is drawn we remember the page
/// content around it (see <see cref="StrokeAnchor"/>). Whenever that content is
/// visible again, we search for it and put the stroke exactly where it belongs.
///
/// Runs on the UI thread.
/// </summary>
internal sealed class AnchorRelocator
{
    readonly Grid grid;
    readonly InkController ink;

    // Per-band lookup of the current frame: hash -> linked list of rows. Built lazily.
    readonly Dictionary<ulong, int>[] rowsByHash;
    readonly bool[] bandIndexed;
    readonly int[] nextWithSameHash, sameHashCount;
    readonly Dictionary<int, int> votesByOffset = new Dictionary<int, int>();

    // ---- tuning -------------------------------------------------------------
    const int AnchorRows = 200;          // rows above and below the stroke that go into the anchor
    const int AnchorSideBands = 2;       // bands left and right of the stroke that go into the anchor
    const int MaxAnchorCells = 1500;
    const int MinAnchorCells = 12;
    const int MinVotes = 12;             // cells that must agree before a stroke is relocated
    const int MaxRepeats = 8;            // a hash seen more often than this in a band is too ambiguous

    public AnchorRelocator(Grid grid, InkController ink)
    {
        this.grid = grid;
        this.ink = ink;
        rowsByHash = new Dictionary<ulong, int>[grid.Bands];
        bandIndexed = new bool[grid.Bands];
        nextWithSameHash = new int[grid.Cells];
        sameHashCount = new int[grid.Cells];
    }

    /// <summary>
    /// Captures anchors for strokes that have none, and snaps strokes that have one
    /// onto it. Returns true if any stroke was moved.
    /// </summary>
    /// <param name="hashes">Cell hashes of the newest frame.</param>
    /// <param name="flags">Cell flags of the newest frame.</param>
    /// <param name="referenceHashes">Cell hashes of the frame before it (to tell whether the screen is still moving).</param>
    public bool Settle(ulong[] hashes, byte[] flags, ulong[] referenceHashes)
    {
        ink.AnchorRequested = false;
        Array.Clear(bandIndexed, 0, bandIndexed.Length);
        bool anyMoved = false;

        foreach (Stroke s in ink.Strokes)
        {
            if (ink.IsInProgress(s)) continue;
            if (s.Anchor == null)
            {
                Capture(s, hashes, flags, referenceHashes);
                continue;
            }
            if (TryLocate(s, hashes, flags, out int offset) && offset != (int)Math.Round(s.OffsetY))
            {
                s.OffsetY = offset;
                anyMoved = true;
            }
        }

        if (anyMoved) ink.RedrawEverything();
        return anyMoved;
    }

    // =========================================================================
    // Capture
    // =========================================================================

    void Capture(Stroke s, ulong[] hashes, byte[] flags, ulong[] referenceHashes)
    {
        if (s.AnchorDisabled || s.Bottom < 0 || s.Top > grid.Height - 1) return;

        Pane.TryGetCells(s.Pane, grid, out CellRange pane);   // falls back to the full screen
        int row0 = Math.Max(pane.Row0, (int)s.Top - AnchorRows);
        int row1 = Math.Min(pane.Row1, (int)s.Bottom + AnchorRows);
        int band0 = Math.Max(pane.Band0, (int)(s.MinX / Grid.BandWidth) - AnchorSideBands);
        int band1 = Math.Min(pane.Band1, (int)(s.MaxX / Grid.BandWidth) + AnchorSideBands);
        if (row0 > row1 || band0 > band1) return;

        int offset = (int)Math.Round(s.OffsetY);
        StrokeAnchor anchor = null;
        if (!s.UseWideAnchor) anchor = Collect(new CellRange(band0, band1, row0, row1), hashes, flags, offset);

        // A stroke in a blank margin has nothing next to it: use the full width of the pane.
        if (anchor == null || anchor.Count < 30)
            anchor = Collect(new CellRange(pane.Band0, pane.Band1, row0, row1), hashes, flags, offset);
        if (anchor.Count < MinAnchorCells) return;

        // If the area is changing right now (a scroll animation is still running), the
        // stroke's position cannot be trusted yet. Ask to be called again.
        int unstable = 0;
        for (int k = 0; k < anchor.Count; k++)
        {
            int cell = grid.Cell(anchor.Bands[k], anchor.RelativeRows[k] + offset);
            if (referenceHashes[cell] != hashes[cell]) unstable++;
        }
        if (unstable * 10 > anchor.Count)
        {
            ink.AnchorRequested = true;
            return;
        }

        s.Anchor = anchor;
    }

    /// <summary>Gathers the usable cells of a region into an anchor, evenly thinned to MaxAnchorCells.</summary>
    StrokeAnchor Collect(CellRange range, ulong[] hashes, byte[] flags, int offset)
    {
        int usable = 0;
        for (int band = range.Band0; band <= range.Band1; band++)
            for (int row = range.Row0; row <= range.Row1; row++)
                if (flags[grid.Cell(band, row)] == 0) usable++;

        int step = Math.Max(1, usable / MaxAnchorCells), n = 0;
        var anchor = new StrokeAnchor { OffsetAtCapture = offset };
        for (int band = range.Band0; band <= range.Band1; band++)
        {
            for (int row = range.Row0; row <= range.Row1; row++)
            {
                int cell = grid.Cell(band, row);
                if (flags[cell] != 0 || (n++ % step) != 0) continue;
                anchor.Bands.Add(band);
                anchor.RelativeRows.Add(row - offset);
                anchor.Hashes.Add(hashes[cell]);
            }
        }
        return anchor;
    }

    // =========================================================================
    // Relocation
    // =========================================================================

    /// <summary>
    /// Searches the current frame for the stroke's anchor. Every anchor cell found
    /// votes for the stroke offset it implies; a clear winner is the answer.
    /// </summary>
    bool TryLocate(Stroke s, ulong[] hashes, byte[] flags, out int offset)
    {
        StrokeAnchor anchor = s.Anchor;
        votesByOffset.Clear();

        for (int k = 0; k < anchor.Count; k++)
        {
            int band = anchor.Bands[k];
            IndexBand(band, hashes, flags);
            if (!rowsByHash[band].TryGetValue(anchor.Hashes[k], out int head) || sameHashCount[head] > MaxRepeats) continue;

            for (int cell = head; cell >= 0; cell = nextWithSameHash[cell])
            {
                int row = cell - band * grid.Height;
                int implied = row - anchor.RelativeRows[k];
                votesByOffset.TryGetValue(implied, out int votes);
                votesByOffset[implied] = votes + 1;
            }
        }

        int best = 0, second = 0;
        offset = 0;
        foreach (KeyValuePair<int, int> entry in votesByOffset)
        {
            if (entry.Value > best) { second = best; best = entry.Value; offset = entry.Key; }
            else if (entry.Value > second) second = entry.Value;
        }

        // Needs enough votes, a clear lead over the runner-up, and at least a quarter of
        // the anchor cells that should be on screen at that offset.
        if (best < MinVotes || best < 2 * second) return false;
        int expectedOnScreen = 0;
        for (int k = 0; k < anchor.Count; k++)
        {
            int row = anchor.RelativeRows[k] + offset;
            if (row >= 0 && row < grid.Height) expectedOnScreen++;
        }
        return best * 4 >= expectedOnScreen;
    }

    void IndexBand(int band, ulong[] hashes, byte[] flags)
    {
        if (bandIndexed[band]) return;
        bandIndexed[band] = true;

        Dictionary<ulong, int> index = rowsByHash[band] ??= new Dictionary<ulong, int>(grid.Height);
        index.Clear();
        int first = band * grid.Height;
        for (int row = 0; row < grid.Height; row++)
        {
            int cell = first + row;
            if (flags[cell] != 0) continue;
            if (index.TryGetValue(hashes[cell], out int head))
            {
                nextWithSameHash[cell] = head;
                sameHashCount[cell] = sameHashCount[head] + 1;
            }
            else
            {
                nextWithSameHash[cell] = -1;
                sameHashCount[cell] = 1;
            }
            index[hashes[cell]] = cell;
        }
    }

    // =========================================================================
    // Pruning
    // =========================================================================

    /// <summary>
    /// Called right after a stroke followed a scroll. Any anchor cell that is still
    /// sitting unchanged at its original screen position did NOT scroll with the
    /// stroke - it belongs to something fixed (a sidebar, a sticky header). Such
    /// cells would later pull the stroke back to where it was drawn, so they go.
    /// </summary>
    public void DropStaticCells(Stroke s, FrameMatch match)
    {
        StrokeAnchor anchor = s.Anchor;
        if (anchor == null) return;

        var kept = new StrokeAnchor { OffsetAtCapture = anchor.OffsetAtCapture };
        for (int k = 0; k < anchor.Count; k++)
        {
            int row = anchor.RelativeRows[k] + anchor.OffsetAtCapture;
            bool isStatic = false;
            if (row >= 0 && row < grid.Height)
            {
                int cell = grid.Cell(anchor.Bands[k], row);
                isStatic = match.Counts[cell] == 1
                           && match.Candidates[cell * FrameMatch.MaxCandidates] == 0
                           && match.Hashes[cell] == anchor.Hashes[k];
            }
            if (isStatic) continue;
            kept.Bands.Add(anchor.Bands[k]);
            kept.RelativeRows.Add(anchor.RelativeRows[k]);
            kept.Hashes.Add(anchor.Hashes[k]);
        }
        if (kept.Count == anchor.Count) return;

        if (kept.Count >= MinAnchorCells)
        {
            s.Anchor = kept;
            return;
        }

        // Nearly everything around the stroke turned out to be fixed. Try again from the
        // full width of the pane; after a couple of failures, give up on this stroke.
        s.Anchor = null;
        if (s.AnchorAttempts++ < 2)
        {
            s.UseWideAnchor = true;
            ink.AnchorRequested = true;
        }
        else s.AnchorDisabled = true;
    }
}
