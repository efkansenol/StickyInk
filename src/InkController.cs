using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace StickyInk;

internal enum MouseKind { Down, Move, Up }

internal readonly struct MouseEvent
{
    public readonly MouseKind Kind;
    public readonly int ScreenX, ScreenY;
    public MouseEvent(MouseKind kind, int screenX, int screenY) { Kind = kind; ScreenX = screenX; ScreenY = screenY; }
}

/// <summary>
/// Owns the drawing: the list of strokes, the current tool / color / size, and
/// rendering them onto the overlay.
///
/// THREADING: everything here runs on the UI thread, except
///  - <see cref="PostMouse"/>, which any thread may call, and
///  - the volatile fields (<see cref="DrawMode"/>, <see cref="StrokeCount"/>,
///    <see cref="InkMask"/>, <see cref="AnchorRequested"/>), which other threads read.
/// </summary>
internal sealed class InkController : IDisposable
{
    public readonly Grid Grid;
    readonly OverlayWindow overlay;
    readonly Control uiThread;

    readonly List<Stroke> strokes = new List<Stroke>();
    Stroke current;      // the stroke being drawn right now, or null
    bool erasing;

    public Tool Tool { get; private set; } = Tool.Pointer;

    /// <summary>The drawing tool to return to when drawing is toggled back on.</summary>
    public Tool LastDrawingTool { get; private set; } = Tool.Pen;

    public Color Color = Palette.Colors[0];
    public float PenSize = Palette.Sizes[1];

    /// <summary>DPI scale (1.0 = 96 DPI), so strokes have the same physical thickness on every display.</summary>
    public float UiScale = 1f;

    /// <summary>Raised whenever something the toolbar shows has changed.</summary>
    public event Action StateChanged;

    /// <summary>Raised when drawing is switched on or off.</summary>
    public event Action<bool> DrawModeChanged;

    // ---- state read by other threads ---------------------------------------

    /// <summary>True while a drawing tool (anything but Pointer) is selected. Read by InputHook.</summary>
    public volatile bool DrawMode;

    /// <summary>Number of strokes. The scroll tracker sleeps while this is 0.</summary>
    public volatile int StrokeCount;

    /// <summary>
    /// One byte per grid cell: non-zero where our own ink is on screen. The scroll
    /// tracker ignores those cells, otherwise it would "see" the ink move when we
    /// move it and conclude the page scrolled again - a runaway feedback loop.
    /// INVARIANT: the mask is updated BEFORE the ink is presented on screen.
    /// </summary>
    public volatile byte[] InkMask;

    /// <summary>Set when a stroke was just finished and needs an anchor. Read by ScrollTracker.</summary>
    public volatile bool AnchorRequested;

    // ---- mouse input queue --------------------------------------------------
    readonly ConcurrentQueue<MouseEvent> mouseQueue = new ConcurrentQueue<MouseEvent>();
    int drainScheduled;
    readonly MethodInvoker drain;

    public InkController(Grid grid, Control uiThread)
    {
        Grid = grid;
        this.uiThread = uiThread;
        overlay = new OverlayWindow(grid);
        InkMask = new byte[grid.Cells];
        drain = DrainMouseQueue;
    }

    public IReadOnlyList<Stroke> Strokes => strokes;
    public IntPtr OverlayHandle => overlay.Handle;
    public bool IsInProgress(Stroke stroke) => stroke == current;

    public void Dispose() => overlay.Dispose();

    // =========================================================================
    // Tools
    // =========================================================================

    public void SetTool(Tool tool)
    {
        Tool = tool;
        if (tool != Tool.Pointer) LastDrawingTool = tool;

        bool drawMode = tool != Tool.Pointer;
        if (drawMode != DrawMode)
        {
            DrawMode = drawMode;
            DrawModeChanged?.Invoke(drawMode);
        }
        StateChanged?.Invoke();
    }

    /// <summary>F9: switch between the pointer and the last drawing tool.</summary>
    public void ToggleDrawMode() => SetTool(Tool == Tool.Pointer ? LastDrawingTool : Tool.Pointer);

    public void Undo()
    {
        if (strokes.Count == 0) return;
        if (current == strokes[strokes.Count - 1]) current = null;
        strokes.RemoveAt(strokes.Count - 1);
        StrokeCount = strokes.Count;
        RedrawEverything();
    }

    public void ClearAll()
    {
        strokes.Clear();
        current = null;
        StrokeCount = 0;
        RedrawEverything();
    }

    // =========================================================================
    // Mouse input
    // =========================================================================

    /// <summary>
    /// Thread-safe. Queues a mouse event and makes sure the UI thread will process it.
    /// Events that arrive while the UI thread is busy are handled together in one pass,
    /// so a 1000 Hz mouse cannot flood the UI thread with repaints.
    /// </summary>
    public void PostMouse(MouseKind kind, int screenX, int screenY)
    {
        mouseQueue.Enqueue(new MouseEvent(kind, screenX, screenY));
        if (Interlocked.Exchange(ref drainScheduled, 1) == 0)
        {
            try { uiThread.BeginInvoke(drain); }
            catch { /* the app is shutting down */ }
        }
    }

    void DrainMouseQueue()
    {
        Interlocked.Exchange(ref drainScheduled, 0);
        bool needPresent = false, needFullRedraw = false;

        while (mouseQueue.TryDequeue(out MouseEvent e))
        {
            float x = e.ScreenX - Grid.Left, y = e.ScreenY - Grid.Top;
            switch (e.Kind)
            {
                case MouseKind.Down:
                    if (Tool == Tool.Eraser)
                    {
                        erasing = true;
                        needFullRedraw |= EraseAt(x, y);
                    }
                    else if (Tool != Tool.Pointer)
                    {
                        BeginStroke(x, y, e.ScreenX, e.ScreenY);
                        if (IsTranslucent(current)) needFullRedraw = true;
                        else { DrawStroke(overlay.Graphics, current); needPresent = true; }
                    }
                    break;

                case MouseKind.Move:
                    if (erasing) needFullRedraw |= EraseAt(x, y);
                    else if (current != null && ExtendStroke(x, y, out PointF from))
                    {
                        // An opaque pen can simply add the new segment. A translucent
                        // highlighter must be redrawn as one path, or the overlapping
                        // segment ends would show up as darker dots.
                        if (IsTranslucent(current)) needFullRedraw = true;
                        else
                        {
                            using (Pen pen = MakePen(current))
                                overlay.Graphics.DrawLine(pen, from.X, from.Y + current.OffsetY, x, y);
                            needPresent = true;
                        }
                    }
                    break;

                case MouseKind.Up:
                    current = null;
                    erasing = false;
                    AnchorRequested = true;
                    break;
            }
        }

        if (needFullRedraw) { Render(); needPresent = true; }
        if (needPresent) overlay.Present();
    }

    static bool IsTranslucent(Stroke stroke) => stroke.Color.A < 255;

    void BeginStroke(float x, float y, int screenX, int screenY)
    {
        bool highlighter = Tool == Tool.Highlighter;
        current = new Stroke
        {
            Color = highlighter ? Color.FromArgb(Palette.HighlighterAlpha, Color) : Color,
            Width = (highlighter ? PenSize * 4f + 8f : PenSize) * UiScale,
            Pane = Pane.FromScreenPoint(screenX, screenY),
        };
        current.Add(new PointF(x, y));
        strokes.Add(current);
        StrokeCount = strokes.Count;
        MarkSegment(InkMask, x, y, x, y, current.Width);
    }

    /// <summary>Adds a point to the current stroke. Returns false if the mouse barely moved.</summary>
    bool ExtendStroke(float x, float y, out PointF previous)
    {
        previous = current.Points[current.Points.Count - 1];
        float localY = y - current.OffsetY;
        float dx = x - previous.X, dy = localY - previous.Y;
        if (dx * dx + dy * dy < 1f) return false;

        current.Add(new PointF(x, localY));
        MarkSegment(InkMask, previous.X, previous.Y + current.OffsetY, x, y, current.Width);
        return true;
    }

    /// <summary>Removes every stroke the eraser touches at (x, y). Returns true if any was removed.</summary>
    bool EraseAt(float x, float y)
    {
        float radius = 10f * UiScale;
        bool removedAny = false;

        for (int i = strokes.Count - 1; i >= 0; i--)
        {
            Stroke s = strokes[i];
            float reach = radius + s.Width / 2, localY = y - s.OffsetY;
            if (x < s.MinX - reach || x > s.MaxX + reach || localY < s.MinY - reach || localY > s.MaxY + reach) continue;

            bool hit = false;
            for (int k = 0; k < s.Points.Count && !hit; k++)
            {
                PointF a = s.Points[k], b = s.Points[Math.Min(k + 1, s.Points.Count - 1)];
                hit = DistanceToSegment(x, localY, a, b) <= reach;
            }
            if (hit) { strokes.RemoveAt(i); removedAny = true; }
        }

        if (removedAny)
        {
            StrokeCount = strokes.Count;
            RebuildInkMask();
        }
        return removedAny;
    }

    static float DistanceToSegment(float px, float py, PointF a, PointF b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y, lengthSquared = dx * dx + dy * dy, t = 0;
        if (lengthSquared > 0)
        {
            t = ((px - a.X) * dx + (py - a.Y) * dy) / lengthSquared;
            t = Math.Max(0, Math.Min(1, t));
        }
        float cx = a.X + t * dx - px, cy = a.Y + t * dy - py;
        return (float)Math.Sqrt(cx * cx + cy * cy);
    }

    // =========================================================================
    // Rendering
    // =========================================================================

    /// <summary>
    /// Call after stroke positions changed (scrolling, relocation, undo...).
    /// Order matters: mask first, pixels second - see <see cref="InkMask"/>.
    /// </summary>
    public void RedrawEverything()
    {
        RebuildInkMask();
        Render();
        overlay.Present();
    }

    void Render()
    {
        overlay.Graphics.Clear(Color.Transparent);
        foreach (Stroke s in strokes)
            if (s.IsOnScreen(Grid.Height)) DrawStroke(overlay.Graphics, s);
    }

    static Pen MakePen(Stroke s) => new Pen(s.Color, s.Width)
    {
        StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round,
    };

    void DrawStroke(Graphics g, Stroke s)
    {
        GraphicsState saved = g.Save();

        // Ink that has scrolled with a page must not spill outside that page's
        // visible area (for example onto the browser's toolbar).
        if (s.HasScrolled && Pane.TryGetBounds(s.Pane, Grid, out Rectangle paneBounds))
            g.SetClip(paneBounds);

        g.TranslateTransform(0, s.OffsetY);
        if (s.Points.Count == 1)
        {
            using (var brush = new SolidBrush(s.Color))
                g.FillEllipse(brush, s.Points[0].X - s.Width / 2, s.Points[0].Y - s.Width / 2, s.Width, s.Width);
        }
        else
        {
            using (Pen pen = MakePen(s)) g.DrawLines(pen, s.Points.ToArray());
        }
        g.Restore(saved);
    }

    // =========================================================================
    // Ink mask
    // =========================================================================

    void RebuildInkMask()
    {
        var mask = new byte[Grid.Cells];
        foreach (Stroke s in strokes)
        {
            if (!s.IsOnScreen(Grid.Height)) continue;
            for (int k = 0; k < s.Points.Count; k++)
            {
                PointF a = s.Points[k], b = s.Points[Math.Min(k + 1, s.Points.Count - 1)];
                MarkSegment(mask, a.X, a.Y + s.OffsetY, b.X, b.Y + s.OffsetY, s.Width);
            }
        }
        InkMask = mask;
    }

    /// <summary>Marks every cell a line segment (with its thickness plus a small margin) may touch.</summary>
    void MarkSegment(byte[] mask, float x0, float y0, float x1, float y1, float width)
    {
        float half = width / 2 + 3;
        int band0 = Grid.ClampBand((int)Math.Floor((Math.Min(x0, x1) - half) / Grid.BandWidth));
        int band1 = Grid.ClampBand((int)Math.Floor((Math.Max(x0, x1) + half) / Grid.BandWidth));
        int row0 = Math.Max(0, (int)Math.Floor(Math.Min(y0, y1) - half));
        int row1 = Math.Min(Grid.Height - 1, (int)Math.Ceiling(Math.Max(y0, y1) + half));

        for (int band = band0; band <= band1; band++)
            for (int row = row0; row <= row1; row++)
                mask[Grid.Cell(band, row)] = 1;
    }
}

/// <summary>The colors and sizes offered by the toolbar.</summary>
internal static class Palette
{
    public static readonly Color[] Colors =
    {
        Color.FromArgb(229, 57, 53),    // red
        Color.FromArgb(30, 136, 229),   // blue
        Color.FromArgb(67, 160, 71),    // green
        Color.FromArgb(253, 216, 53),   // yellow
        Color.FromArgb(20, 20, 20),     // black
        Color.White,
    };

    public static readonly string[] ColorNames = { "Red", "Blue", "Green", "Yellow", "Black", "White" };

    public static readonly float[] Sizes = { 2.5f, 4f, 7f };
    public static readonly string[] SizeNames = { "Thin", "Medium", "Thick" };

    public const int HighlighterAlpha = 95;
}
