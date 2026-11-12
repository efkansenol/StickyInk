using System;
using System.Windows.Forms;

namespace StickyInk;

/// <summary>
/// Keeps the mouse cursor a steady crosshair while a drawing tool is active.
///
/// It is an invisible (1/255 opacity) window covering the whole screen. While it
/// is shown, mouse moves go to it instead of the app underneath, so that app can
/// no longer switch the cursor to an I-beam, a hand, etc.
///
/// The window never moves: an earlier version followed the cursor, and every
/// reposition let the app underneath see the mouse for an instant, which made
/// the cursor flicker. InputHook shows and hides it.
/// </summary>
internal sealed class CursorShield : NativeWindow
{
    public CursorShield(Grid grid)
    {
        CreateHandle(new CreateParams
        {
            Caption = "StickyInk cursor shield",
            Style = Native.WS_POPUP,
            ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST,
            X = grid.Left, Y = grid.Top, Width = grid.Width, Height = grid.Height,
        });
        Native.SetLayeredWindowAttributes(Handle, 0, 1, Native.LWA_ALPHA);

        // Keep it out of screen captures so it can never disturb the scroll tracker.
        Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    public void Show() =>
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);

    public void Hide() => Native.ShowWindow(Handle, Native.SW_HIDE);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_SETCURSOR)
        {
            Native.SetCursor(Cursors.Cross.Handle);
            m.Result = (IntPtr)1;
            return;
        }
        if (m.Msg == Native.WM_MOUSEACTIVATE)
        {
            m.Result = (IntPtr)Native.MA_NOACTIVATE;
            return;
        }
        base.WndProc(ref m);
    }
}
