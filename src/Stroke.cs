using System;
using System.Collections.Generic;
using System.Drawing;

namespace StickyInk;

internal enum Tool
{
    /// <summary>Drawing is off; the mouse works normally.</summary>
    Pointer,
    Pen,
    Highlighter,
    Eraser,
}

/// <summary>One continuous line drawn by the user.</summary>
internal sealed class Stroke
{
    /// <summary>
    /// Points in overlay coordinates AS THEY WERE WHEN DRAWN. They never change.
    /// The stroke's current on-screen position is Points shifted down by <see cref="OffsetY"/>.
    /// </summary>
    public readonly List<PointF> Points = new List<PointF>();

    public Color Color;
    public float Width;

    /// <summary>Total vertical scroll applied so far, in pixels (negative = moved up).</summary>
    public float OffsetY;

    /// <summary>True once the stroke has followed the page at least once.</summary>
    public bool HasScrolled;

    /// <summary>The window that was under the stroke when it was drawn (its scroll container).</summary>
    public IntPtr Pane;

    // Bounding box of Points (without OffsetY).
    public float MinX = float.MaxValue, MinY = float.MaxValue, MaxX = float.MinValue, MaxY = float.MinValue;

    // Anchor bookkeeping, owned by AnchorRelocator.
    public StrokeAnchor Anchor;
    public bool UseWideAnchor;
    public bool AnchorDisabled;
    public int AnchorAttempts;

    /// <summary>Current on-screen top / bottom edge (overlay coordinates).</summary>
    public float Top => MinY + OffsetY;
    public float Bottom => MaxY + OffsetY;

    public bool IsOnScreen(int screenHeight) => Bottom >= -Width && Top <= screenHeight + Width;

    public void Add(PointF p)
    {
        Points.Add(p);
        if (p.X < MinX) MinX = p.X;
        if (p.X > MaxX) MaxX = p.X;
        if (p.Y < MinY) MinY = p.Y;
        if (p.Y > MaxY) MaxY = p.Y;
    }
}

/// <summary>
/// A fingerprint of the page content around a stroke, taken shortly after it was drawn.
/// It is a list of cells (band, row, pixel hash). When that content shows up on screen
/// again, the stroke can be snapped back to the exact right place - see AnchorRelocator.
/// </summary>
internal sealed class StrokeAnchor
{
    public List<int> Bands = new List<int>();

    /// <summary>Cell row minus the stroke's OffsetY at capture time (so it stays valid as the stroke moves).</summary>
    public List<int> RelativeRows = new List<int>();

    public List<ulong> Hashes = new List<ulong>();

    /// <summary>The stroke's OffsetY when the anchor was captured.</summary>
    public int OffsetAtCapture;

    public int Count => Hashes.Count;
}
