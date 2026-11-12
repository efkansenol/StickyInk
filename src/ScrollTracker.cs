using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;

namespace StickyInk;

/// <summary>
/// What the tracker learned by comparing the newest screen capture ("current")
/// with the previous one ("reference").
///
/// For every cell it lists the vertical shifts that would explain the cell's
/// content: "this 64x1 strip of pixels was N rows higher/lower a moment ago".
/// </summary>
internal sealed class FrameMatch
{
    /// <summary>Maximum number of candidate shifts stored per cell.</summary>
    public const int MaxCandidates = 6;

    /// <summary>Value of Counts[cell] meaning "content changed, but was not found anywhere in the reference".</summary>
    public const byte ChangedNoMatch = 255;

    /// <summary>
    /// Counts[cell]: 0 = no information (blank, inked, or ambiguous);
    /// 1..MaxCandidates = number of candidate shifts; ChangedNoMatch = see above.
    /// A cell whose content did not change has exactly one candidate: 0.
    /// </summary>
    public readonly byte[] Counts;

    /// <summary>Candidates[cell * MaxCandidates + i]: candidate shift in rows (positive = content moved down).</summary>
    public readonly short[] Candidates;

    /// <summary>Pixel hash of every cell in the current frame.</summary>
    public ulong[] Hashes;

    public FrameMatch(int cells)
    {
        Counts = new byte[cells];
        Candidates = new short[cells * MaxCandidates];
    }
}

/// <summary>
/// The background thread that detects scrolling by watching the screen.
///
/// About 60 times a second (20 when nothing is happening) it:
///   1. captures the whole screen;
///   2. hashes every cell (a 64x1 pixel strip, see <see cref="Grid"/>);
///   3. matches the hashes against the previous capture to find out how far
///      content moved vertically (<see cref="FrameMatch"/>);
///   4. hands the result to the UI thread, where StrokeFollower moves the strokes
///      and AnchorRelocator corrects any accumulated error.
///
/// Because it measures what actually happened on screen, it works the same for the
/// mouse wheel, the keyboard, a dragged scrollbar or a touchpad, in any application.
///
/// It does nothing while there are no strokes.
/// </summary>
internal sealed class ScrollTracker
{
    readonly Grid grid;
    readonly InkController ink;
    readonly StrokeFollower follower;
    readonly AnchorRelocator anchors;
    readonly Control uiThread;
    volatile bool running = true;

    // ---- tuning -------------------------------------------------------------
    const int ActiveIntervalMs = 15;     // polling period right after something changed
    const int IdleIntervalMs = 45;       // polling period while the screen is quiet
    const int MinChangedCells = 4;       // fewer changed cells than this = nothing happened (e.g. a blinking caret)
    const int MaxHeldFrames = 12;        // how long to keep an old reference when frames cannot be matched
    const int SettleIntervalMs = 120;    // how often the anchor step may run
    const int SettleWindowMs = 1200;     // ...and for how long after the last detected scroll

    public ScrollTracker(Grid grid, InkController ink, StrokeFollower follower, AnchorRelocator anchors, Control uiThread)
    {
        this.grid = grid;
        this.ink = ink;
        this.follower = follower;
        this.anchors = anchors;
        this.uiThread = uiThread;
        new Thread(Run) { IsBackground = true, Name = "StickyInk scroll tracker" }.Start();
    }

    public void Stop() => running = false;

    unsafe void Run()
    {
        Native.timeBeginPeriod(1);   // makes Thread.Sleep accurate to ~1 ms

        // A 32-bit bitmap we BitBlt the screen into and then read directly.
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr captureDc = Native.CreateCompatibleDC(screenDc);
        var header = Native.BITMAPINFOHEADER.TopDown32(grid.Width, grid.Height);
        IntPtr captureBitmap = Native.CreateDIBSection(screenDc, ref header, 0, out IntPtr pixels, IntPtr.Zero, 0);
        Native.SelectObject(captureDc, captureBitmap);

        int cells = grid.Cells, height = grid.Height;
        ulong[] referenceHash = new ulong[cells], currentHash = new ulong[cells];
        byte[] referenceFlags = new byte[cells], currentFlags = new byte[cells];
        var match = new FrameMatch(cells);

        // Scratch data for matching: per band, a hash -> rows lookup of the reference frame.
        var rowsByHash = new Dictionary<ulong, int>(height * 2);
        int[] nextWithSameHash = new int[cells], sameHashCount = new int[cells];
        int[] shiftHistogram = new int[2 * height + 1];

        // The ink masks of the two previous captures. Ink that just moved may still be
        // on screen at its old place for a frame, so old masks stay in force a little.
        byte[] previousMask = null, olderMask = null;

        bool haveReference = false, everScrolled = false;
        int heldFrames = 0, lastChangeTick = 0, lastScrollTick = 0, lastSettleTick = 0;

        while (running)
        {
            if (ink.StrokeCount == 0)
            {
                haveReference = false;
                previousMask = olderMask = null;
                Thread.Sleep(80);
                continue;
            }

            int now = Environment.TickCount;
            byte[] mask = ink.InkMask;
            Native.BitBlt(captureDc, 0, 0, grid.Width, height, screenDc, grid.Left, grid.Top, Native.SRCCOPY | Native.CAPTUREBLT);
            HashFrame((uint*)pixels, currentHash, currentFlags, mask, previousMask, olderMask);
            olderMask = previousMask;
            previousMask = mask;

            bool adoptAsReference = true;
            if (haveReference)
            {
                bool scrollDetected = false;

                int changed = 0;
                for (int i = 0; i < cells; i++)
                    if (currentFlags[i] == 0 && (referenceFlags[i] & CellFlags.Inked) == 0 && currentHash[i] != referenceHash[i])
                        changed++;

                if (changed >= MinChangedCells)
                {
                    lastChangeTick = now;
                    int bestVotes = MatchFrames(referenceHash, referenceFlags, currentHash, currentFlags, match,
                                                rowsByHash, nextWithSameHash, sameHashCount, shiftHistogram);

                    // A scroll is "detected" when enough cells agree on one non-zero shift.
                    // The absolute threshold matters: with only a ratio, anything else moving
                    // on screen (a video, a spinner) would drown out a real scroll.
                    scrollDetected = bestVotes >= 16 || (bestVotes >= 6 && bestVotes * 10 >= changed * 3);

                    if (scrollDetected)
                    {
                        match.Hashes = currentHash;
                        if (!RunOnUiThread(() => follower.Apply(match))) break;
                        heldFrames = 0;
                        everScrolled = true;
                        lastScrollTick = now;
                    }
                    else if (++heldFrames <= MaxHeldFrames)
                    {
                        // The frame changed but could not be matched (mid-animation blur, a page
                        // jump...). Keep the OLD reference for a while: a later, sharper frame
                        // can often still be matched against it, so no movement is lost.
                        adoptAsReference = false;
                    }
                    else heldFrames = 0;
                }
                else heldFrames = 0;

                // Anchor step: capture anchors for new strokes and snap existing strokes
                // back onto their anchors. Runs when a stroke was just finished and for a
                // short while after each scroll.
                bool settleDue = ink.AnchorRequested || (everScrolled && now - lastScrollTick < SettleWindowMs);
                if (!scrollDetected && settleDue && now - lastSettleTick >= SettleIntervalMs)
                {
                    lastSettleTick = now;
                    bool relocated = false;
                    ulong[] cur = currentHash, reference = referenceHash;
                    byte[] flags = currentFlags;
                    if (!RunOnUiThread(() => relocated = anchors.Settle(cur, flags, reference))) break;
                    if (relocated)
                    {
                        // Positions are now exact for THIS frame, so it must become the reference.
                        adoptAsReference = true;
                        heldFrames = 0;
                    }
                }
            }

            if (adoptAsReference)
            {
                (referenceHash, currentHash) = (currentHash, referenceHash);
                (referenceFlags, currentFlags) = (currentFlags, referenceFlags);
                haveReference = true;
            }

            int elapsed = Environment.TickCount - now;
            int interval = now - lastChangeTick < 1000 ? ActiveIntervalMs : IdleIntervalMs;
            Thread.Sleep(Math.Max(1, interval - elapsed));
        }
    }

    /// <summary>Runs an action on the UI thread and waits for it. Returns false if the app is closing.</summary>
    bool RunOnUiThread(Action action)
    {
        try
        {
            uiThread.Invoke(action);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Computes a hash and flags for every cell of a captured frame.</summary>
    unsafe void HashFrame(uint* pixels, ulong[] hashes, byte[] flags, byte[] mask, byte[] previousMask, byte[] olderMask)
    {
        int width = grid.Width, height = grid.Height, bands = grid.Bands;
        for (int row = 0; row < height; row++)
        {
            uint* line = pixels + (long)row * width;
            for (int band = 0; band < bands; band++)
            {
                int x0 = band * Grid.BandWidth, x1 = Math.Min(width, x0 + Grid.BandWidth);
                uint first = line[x0] & 0xFFFFFF;
                ulong hash = 1469598103934665603UL;   // FNV-1a
                bool flat = true;
                for (int x = x0; x < x1; x += 2)       // every other pixel is plenty
                {
                    uint pixel = line[x] & 0xFFFFFF;
                    if (pixel != first) flat = false;
                    hash = (hash ^ pixel) * 1099511628211UL;
                }

                int cell = band * height + row;
                bool inked = mask[cell] != 0
                             || (previousMask != null && previousMask[cell] != 0)
                             || (olderMask != null && olderMask[cell] != 0);
                hashes[cell] = hash;
                flags[cell] = (byte)((flat ? CellFlags.Flat : 0) | (inked ? CellFlags.Inked : 0));
            }
        }
    }

    /// <summary>
    /// Fills <paramref name="match"/> with candidate shifts for every cell and returns
    /// the number of cells that agree on the most popular non-zero shift.
    /// </summary>
    int MatchFrames(ulong[] referenceHash, byte[] referenceFlags, ulong[] currentHash, byte[] currentFlags, FrameMatch match,
                    Dictionary<ulong, int> rowsByHash, int[] nextWithSameHash, int[] sameHashCount, int[] shiftHistogram)
    {
        int height = grid.Height;
        const int K = FrameMatch.MaxCandidates;
        Array.Clear(shiftHistogram, 0, shiftHistogram.Length);

        for (int band = 0; band < grid.Bands; band++)
        {
            int first = band * height;

            // Index the reference band: hash -> linked list of rows with that hash.
            rowsByHash.Clear();
            for (int row = 0; row < height; row++)
            {
                int cell = first + row;
                if (referenceFlags[cell] != 0) continue;
                if (rowsByHash.TryGetValue(referenceHash[cell], out int head))
                {
                    nextWithSameHash[cell] = head;
                    sameHashCount[cell] = sameHashCount[head] + 1;
                }
                else
                {
                    nextWithSameHash[cell] = -1;
                    sameHashCount[cell] = 1;
                }
                rowsByHash[referenceHash[cell]] = cell;
            }

            // Look every current cell up in that index.
            for (int row = 0; row < height; row++)
            {
                int cell = first + row;
                match.Counts[cell] = 0;
                if (currentFlags[cell] != 0) continue;

                if (currentHash[cell] == referenceHash[cell])
                {
                    // Unchanged: evidence that this spot did NOT scroll.
                    match.Candidates[cell * K] = 0;
                    match.Counts[cell] = 1;
                    continue;
                }
                if ((referenceFlags[cell] & CellFlags.Inked) != 0) continue;

                if (!rowsByHash.TryGetValue(currentHash[cell], out int head))
                {
                    match.Counts[cell] = FrameMatch.ChangedNoMatch;
                    continue;
                }
                if (sameHashCount[head] > K) continue;   // repeated pattern: too ambiguous to use

                int n = 0;
                for (int j = head; j >= 0 && n < K; j = nextWithSameHash[j])
                {
                    int shift = row - (j - first);
                    match.Candidates[cell * K + n++] = (short)shift;
                    shiftHistogram[shift + height]++;
                }
                match.Counts[cell] = (byte)n;
            }
        }

        int best = 0;
        for (int i = 0; i < shiftHistogram.Length; i++)
            if (shiftHistogram[i] > best) best = shiftHistogram[i];
        return best;
    }
}
