using System;
using System.Drawing;

namespace StickyInk;

/// <summary>
/// A "pane" is the window that sits under a stroke - in practice the scrollable
/// area the user drew on (for a browser, the page area without the toolbar).
/// Knowing the pane lets us ignore everything outside it when measuring scroll,
/// and clip ink so it never spills onto the window's own chrome.
/// </summary>
internal static class Pane
{
    /// <summary>
    /// The deepest window at a screen point. Our own overlay is click-through, so
    /// WindowFromPoint skips it. Windows of our own process are ignored except in
    /// self-test mode, where the test page lives in this process.
    /// </summary>
    public static IntPtr FromScreenPoint(int screenX, int screenY)
    {
        try
        {
            IntPtr hwnd = Native.WindowFromPoint(new Native.POINT(screenX, screenY));
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == Native.GetCurrentProcessId() && !Program.SelfTestMode) return IntPtr.Zero;
            return hwnd;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>The pane's client area (no title bar, no borders) in overlay coordinates.</summary>
    public static bool TryGetBounds(IntPtr pane, Grid grid, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        var origin = new Native.POINT(0, 0);
        if (pane == IntPtr.Zero || !Native.IsWindow(pane)) return false;
        if (!Native.GetClientRect(pane, out Native.RECT client) || !Native.ClientToScreen(pane, ref origin)) return false;
        bounds = new Rectangle(origin.x - grid.Left, origin.y - grid.Top, client.right, client.bottom);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    /// <summary>
    /// The pane as a range of cells. Only bands that lie FULLY inside the pane are
    /// included: an edge band also contains pixels of whatever is next to the window,
    /// which never scrolls and would pollute the measurement.
    /// </summary>
    public static bool TryGetCells(IntPtr pane, Grid grid, out CellRange cells)
    {
        cells = CellRange.FullScreen(grid);
        if (!TryGetBounds(pane, grid, out Rectangle b)) return false;

        int band0 = Math.Max(0, (b.Left + Grid.BandWidth - 1) / Grid.BandWidth);
        int band1 = Math.Min(grid.Bands - 1, b.Right / Grid.BandWidth - 1);
        if (band0 > band1)
        {
            // Pane narrower than two bands: fall back to the bands it touches.
            band0 = grid.ClampBand(b.Left / Grid.BandWidth);
            band1 = grid.ClampBand((b.Right - 1) / Grid.BandWidth);
        }
        int row0 = Math.Max(0, b.Top), row1 = Math.Min(grid.Height - 1, b.Bottom - 1);
        if (band0 > band1 || row0 > row1) return false;

        cells = new CellRange(band0, band1, row0, row1);
        return true;
    }
}
