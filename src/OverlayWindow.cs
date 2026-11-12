using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace StickyInk;

/// <summary>
/// The surface the ink is drawn on: a borderless, always-on-top window covering
/// the whole virtual screen, with per-pixel transparency.
///
/// It is fully click-through (WS_EX_TRANSPARENT), so it never receives mouse
/// input - drawing input comes from InputHook instead.
///
/// Usage: draw into <see cref="Graphics"/>, then call <see cref="Present"/>.
/// </summary>
internal sealed class OverlayWindow : NativeWindow, IDisposable
{
    readonly Grid grid;
    IntPtr memoryDc, dib, previousBitmap;
    Bitmap bitmap;

    /// <summary>Draws straight into the window's pixel buffer (premultiplied ARGB).</summary>
    public Graphics Graphics { get; private set; }

    public OverlayWindow(Grid grid)
    {
        this.grid = grid;

        CreateHandle(new CreateParams
        {
            Caption = "StickyInk overlay",
            Style = Native.WS_POPUP,
            ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW
                      | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST,
            X = grid.Left, Y = grid.Top, Width = grid.Width, Height = grid.Height,
        });

        // The pixel buffer is a GDI DIB section; a GDI+ Bitmap wraps the same memory,
        // so GDI+ drawing lands directly in what UpdateLayeredWindow displays (no copy).
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        memoryDc = Native.CreateCompatibleDC(screenDc);
        var header = Native.BITMAPINFOHEADER.TopDown32(grid.Width, grid.Height);
        dib = Native.CreateDIBSection(screenDc, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        previousBitmap = Native.SelectObject(memoryDc, dib);
        Native.ReleaseDC(IntPtr.Zero, screenDc);

        bitmap = new Bitmap(grid.Width, grid.Height, grid.Width * 4, PixelFormat.Format32bppPArgb, bits);
        Graphics = Graphics.FromImage(bitmap);
        Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Graphics.Clear(Color.Transparent);

        Present();
        Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
    }

    /// <summary>Pushes the current contents of the pixel buffer to the screen.</summary>
    public void Present()
    {
        Graphics.Flush();
        var destination = new Native.POINT(grid.Left, grid.Top);
        var source = new Native.POINT(0, 0);
        var size = new Native.SIZE { cx = grid.Width, cy = grid.Height };
        var blend = new Native.BLENDFUNCTION { SourceConstantAlpha = 255, AlphaFormat = Native.AC_SRC_ALPHA };
        Native.UpdateLayeredWindow(Handle, IntPtr.Zero, ref destination, ref size, memoryDc, ref source, 0, ref blend, Native.ULW_ALPHA);
    }

    public void Dispose()
    {
        Graphics?.Dispose();
        Graphics = null;
        bitmap?.Dispose();
        bitmap = null;
        if (memoryDc != IntPtr.Zero)
        {
            Native.SelectObject(memoryDc, previousBitmap);
            Native.DeleteObject(dib);
            Native.DeleteDC(memoryDc);
            memoryDc = IntPtr.Zero;
        }
        DestroyHandle();
    }
}
