using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace StickyInk;

/// <summary>
/// A system-wide low-level mouse hook, running on its own thread.
///
/// While a drawing tool is active it swallows left-button presses and turns them
/// (plus the following moves and the release) into strokes. Everything else -
/// mouse wheel, right click, clicks on the toolbar - is passed on untouched, which
/// is why you can keep scrolling the page while drawing.
///
/// The hook callback runs for EVERY mouse event on the system, so it must return
/// quickly: it only reads volatile state and queues work for the UI thread.
/// </summary>
internal sealed class InputHook : IDisposable
{
    readonly Grid grid;
    readonly InkController ink;
    Native.HookProc callback;   // kept in a field so the GC cannot collect the delegate
    IntPtr hook;
    CursorShield shield;

    // Only touched on the hook thread:
    bool strokeInProgress;
    bool shieldVisible;
    int letClicksThroughUntil;

    // Toolbar bounds in screen coordinates; clicks there must reach the toolbar.
    volatile int toolbarLeft, toolbarTop, toolbarRight, toolbarBottom;

    public InputHook(Grid grid, InkController ink)
    {
        this.grid = grid;
        this.ink = ink;
        var thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "StickyInk input hook" };
        thread.Start();
    }

    public void SetToolbarBounds(Rectangle screenBounds)
    {
        toolbarLeft = screenBounds.Left;
        toolbarTop = screenBounds.Top;
        toolbarRight = screenBounds.Right;
        toolbarBottom = screenBounds.Bottom;
    }

    void Run()
    {
        shield = new CursorShield(grid);
        callback = OnMouseEvent;
        hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, callback, Native.GetModuleHandle(null), 0);
        Application.Run();   // a low-level hook needs a message loop on its thread
    }

    IntPtr OnMouseEvent(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int message = (int)wParam;
            int x = Marshal.ReadInt32(lParam, 0), y = Marshal.ReadInt32(lParam, 4);   // MSLLHOOKSTRUCT.pt
            bool overToolbar = x >= toolbarLeft && x < toolbarRight && y >= toolbarTop && y < toolbarBottom;
            bool isWheel = message == Native.WM_MOUSEWHEEL || message == Native.WM_MOUSEHWHEEL;
            bool isLeftOrMove = message == Native.WM_MOUSEMOVE || message == Native.WM_LBUTTONDOWN || message == Native.WM_LBUTTONUP;

            UpdateCursorShield(isWheel, isLeftOrMove, overToolbar);

            if (message == Native.WM_LBUTTONDOWN)
            {
                if (ink.DrawMode && !overToolbar)
                {
                    strokeInProgress = true;
                    ink.PostMouse(MouseKind.Down, x, y);
                    return (IntPtr)1;   // swallow: the app underneath never sees the click
                }
            }
            else if (message == Native.WM_MOUSEMOVE)
            {
                if (strokeInProgress) ink.PostMouse(MouseKind.Move, x, y);
            }
            else if (message == Native.WM_LBUTTONUP && strokeInProgress)
            {
                strokeInProgress = false;
                ink.PostMouse(MouseKind.Up, x, y);
                return (IntPtr)1;
            }
        }
        return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    /// <summary>
    /// The shield blocks ALL mouse input to the app underneath, so it has to step
    /// aside whenever an event should get through:
    ///  - mouse wheel: hidden for just this event, back on the next one;
    ///  - right / middle click: hidden for 300 ms so press and release both arrive;
    ///  - over the toolbar: hidden so the toolbar can be clicked.
    /// </summary>
    void UpdateCursorShield(bool isWheel, bool isLeftOrMove, bool overToolbar)
    {
        int now = Environment.TickCount;
        if (!isLeftOrMove && !isWheel) letClicksThroughUntil = now + 300;

        bool wanted = ink.DrawMode && !isWheel
                      && (strokeInProgress || (!overToolbar && now - letClicksThroughUntil >= 0));
        if (wanted == shieldVisible) return;

        if (wanted) shield.Show(); else shield.Hide();
        shieldVisible = wanted;
    }

    public void Dispose()
    {
        if (hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }
    }
}
