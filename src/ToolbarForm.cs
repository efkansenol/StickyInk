using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace StickyInk;

/// <summary>
/// The small floating toolbar - the only visible UI - and the application's
/// composition root: it creates all the other parts in <see cref="OnLoad"/> and
/// tears them down in <see cref="OnFormClosed"/>.
///
/// The toolbar is painted by hand (no child controls). To add a button:
///   1. add a value to <see cref="ButtonKind"/> if it is a new kind of action;
///   2. add it in <see cref="BuildButtons"/>;
///   3. draw it in <see cref="PaintButton"/>;
///   4. handle the click in <see cref="OnButtonClicked"/>.
/// </summary>
internal sealed class ToolbarForm : Form
{
    enum ButtonKind { Tool, Color, Size, Undo, Clear, Close }

    sealed class Button
    {
        public ButtonKind Kind;
        public RectangleF Bounds;
        public string Tooltip;
        public string Glyph;     // a character from the "Segoe MDL2 Assets" icon font, or null
        public Tool Tool;
        public Color Color;
        public float Size;
    }

    // Hotkey ids for RegisterHotKey.
    const int HotkeyToggle = 1, HotkeyClear = 2, HotkeyEscape = 3, HotkeyUndo = 4;

    static readonly Color Background = Color.FromArgb(38, 38, 42);
    static readonly Color Border = Color.FromArgb(90, 90, 98);
    static readonly Color HoverFill = Color.FromArgb(64, 64, 70);
    static readonly Color ActiveFill = Color.FromArgb(58, 120, 215);

    Grid grid;
    InkController ink;
    InputHook inputHook;
    ScrollTracker scrollTracker;

    float scale = 1f;             // DPI scale: 1.0 at 96 DPI
    readonly List<Button> buttons = new List<Button>();
    readonly ToolTip tooltip = new ToolTip();
    readonly Timer keepOnTopTimer = new Timer { Interval = 2000 };
    Font iconFont;
    Button hovered;
    bool dragging;
    Point dragOffset;

    public ToolbarForm()
    {
        Text = "StickyInk";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    // Never take keyboard focus away from the app the user is annotating.
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    // =========================================================================
    // Startup / shutdown
    // =========================================================================

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;
        iconFont = new Font("Segoe MDL2 Assets", 17 * scale, GraphicsUnit.Pixel);

        BuildButtons();
        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(workArea.Left + (workArea.Width - Width) / 2, workArea.Top + (int)(8 * scale));

        // Wire the parts together. See docs/ARCHITECTURE.md for how they cooperate.
        grid = new Grid();
        ink = new InkController(grid, this) { UiScale = scale };
        ink.StateChanged += Invalidate;
        ink.DrawModeChanged += OnDrawModeChanged;
        inputHook = new InputHook(grid, ink);
        inputHook.SetToolbarBounds(Bounds);
        var anchors = new AnchorRelocator(grid, ink);
        var follower = new StrokeFollower(grid, ink, anchors);
        scrollTracker = new ScrollTracker(grid, ink, follower, anchors, this);

        Native.RegisterHotKey(Handle, HotkeyToggle, Native.MOD_NOREPEAT, Native.VK_F9);
        Native.RegisterHotKey(Handle, HotkeyClear, Native.MOD_NOREPEAT | Native.MOD_SHIFT, Native.VK_F9);

        // Other always-on-top windows can end up above us; re-assert our place now and then.
        keepOnTopTimer.Tick += (_, _) => BringToTop();
        keepOnTopTimer.Start();
        BringToTop();

        if (Program.SelfTestMode) new SelfTest(this, ink).Start();
    }

    void BringToTop()
    {
        const uint flags = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE;
        Native.SetWindowPos(ink.OverlayHandle, Native.HWND_TOPMOST, 0, 0, 0, 0, flags);
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, flags);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        keepOnTopTimer.Stop();
        for (int id = HotkeyToggle; id <= HotkeyUndo; id++) Native.UnregisterHotKey(Handle, id);
        scrollTracker?.Stop();
        inputHook?.Dispose();
        ink?.Dispose();
        base.OnFormClosed(e);
    }

    // =========================================================================
    // Hotkeys
    // =========================================================================

    /// <summary>Esc and Ctrl+Z are only claimed while drawing, so other apps keep them the rest of the time.</summary>
    void OnDrawModeChanged(bool drawing)
    {
        if (drawing)
        {
            Native.RegisterHotKey(Handle, HotkeyEscape, Native.MOD_NOREPEAT, Native.VK_ESCAPE);
            Native.RegisterHotKey(Handle, HotkeyUndo, Native.MOD_CONTROL, Native.VK_Z);
        }
        else
        {
            Native.UnregisterHotKey(Handle, HotkeyEscape);
            Native.UnregisterHotKey(Handle, HotkeyUndo);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY && ink != null)
        {
            switch ((int)m.WParam)
            {
                case HotkeyToggle: ink.ToggleDrawMode(); break;
                case HotkeyClear: ink.ClearAll(); break;
                case HotkeyEscape: ink.SetTool(Tool.Pointer); break;
                case HotkeyUndo: ink.Undo(); break;
            }
            return;
        }
        base.WndProc(ref m);
    }

    // =========================================================================
    // Buttons
    // =========================================================================

    void BuildButtons()
    {
        float x = 18 * scale;   // room for the drag grip on the left

        void Add(float width, Button button)
        {
            button.Bounds = new RectangleF(x, 4 * scale, width * scale, 36 * scale);
            buttons.Add(button);
            x += width * scale;
        }
        void Gap() => x += 8 * scale;

        Add(36, new Button { Kind = ButtonKind.Tool, Tool = Tool.Pointer, Tooltip = "Pointer - use the mouse normally (F9 / Esc)" });
        Add(36, new Button { Kind = ButtonKind.Tool, Tool = Tool.Pen, Glyph = "", Tooltip = "Pen (F9)" });
        Add(36, new Button { Kind = ButtonKind.Tool, Tool = Tool.Highlighter, Glyph = "", Tooltip = "Highlighter" });
        Add(36, new Button { Kind = ButtonKind.Tool, Tool = Tool.Eraser, Glyph = "", Tooltip = "Eraser - removes any stroke it touches" });
        Gap();
        for (int i = 0; i < Palette.Colors.Length; i++)
            Add(26, new Button { Kind = ButtonKind.Color, Color = Palette.Colors[i], Tooltip = Palette.ColorNames[i] });
        Gap();
        for (int i = 0; i < Palette.Sizes.Length; i++)
            Add(28, new Button { Kind = ButtonKind.Size, Size = Palette.Sizes[i], Tooltip = Palette.SizeNames[i] });
        Gap();
        Add(36, new Button { Kind = ButtonKind.Undo, Glyph = "", Tooltip = "Undo (Ctrl+Z while drawing)" });
        Add(36, new Button { Kind = ButtonKind.Clear, Glyph = "", Tooltip = "Clear everything (Shift+F9)" });
        Add(36, new Button { Kind = ButtonKind.Close, Glyph = "", Tooltip = "Quit StickyInk" });

        Size = new Size((int)Math.Ceiling(x + 4 * scale), (int)Math.Ceiling(44 * scale));
    }

    void OnButtonClicked(Button button)
    {
        switch (button.Kind)
        {
            case ButtonKind.Tool:
                ink.SetTool(button.Tool);
                break;
            case ButtonKind.Color:
                ink.Color = button.Color;
                // Picking a color means "I want to draw with it".
                if (ink.Tool == Tool.Pointer || ink.Tool == Tool.Eraser)
                    ink.SetTool(ink.LastDrawingTool == Tool.Eraser ? Tool.Pen : ink.LastDrawingTool);
                break;
            case ButtonKind.Size:
                ink.PenSize = button.Size;
                break;
            case ButtonKind.Undo:
                ink.Undo();
                break;
            case ButtonKind.Clear:
                ink.ClearAll();
                break;
            case ButtonKind.Close:
                Close();
                return;
        }
        Invalidate();
    }

    Button ButtonAt(Point p)
    {
        foreach (Button b in buttons)
            if (b.Bounds.Contains(p)) return b;
        return null;
    }

    // =========================================================================
    // Mouse
    // =========================================================================

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        Button button = ButtonAt(e.Location);
        if (button != null)
        {
            OnButtonClicked(button);
            return;
        }
        // Anywhere else on the toolbar: drag it around.
        dragging = true;
        dragOffset = e.Location;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (dragging)
        {
            Point cursor = Cursor.Position;
            Location = new Point(cursor.X - dragOffset.X, cursor.Y - dragOffset.Y);
            return;
        }
        Button button = ButtonAt(e.Location);
        if (button == hovered) return;
        hovered = button;
        tooltip.SetToolTip(this, button?.Tooltip ?? "Drag to move");
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        dragging = false;
        Capture = false;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (hovered == null) return;
        hovered = null;
        Invalidate();
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        inputHook?.SetToolbarBounds(Bounds);
    }

    // =========================================================================
    // Painting
    // =========================================================================

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Background);

        // Drag grip: three dots on the left edge.
        using (var grip = new SolidBrush(Color.FromArgb(120, 120, 128)))
            for (int i = 0; i < 3; i++)
                g.FillEllipse(grip, 7 * scale, (13 + i * 7) * scale, 3.5f * scale, 3.5f * scale);

        if (ink != null)
            foreach (Button button in buttons) PaintButton(g, button);

        using (var pen = new Pen(Border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    void PaintButton(Graphics g, Button button)
    {
        bool active = (button.Kind == ButtonKind.Tool && button.Tool == ink.Tool)
                      || (button.Kind == ButtonKind.Size && button.Size == ink.PenSize);
        if (active || button == hovered)
        {
            using (GraphicsPath path = RoundedRectangle(RectangleF.Inflate(button.Bounds, -2 * scale, -2 * scale), 6 * scale))
            using (var fill = new SolidBrush(active ? ActiveFill : HoverFill))
                g.FillPath(fill, path);
        }

        float centerX = button.Bounds.X + button.Bounds.Width / 2, centerY = button.Bounds.Y + button.Bounds.Height / 2;

        if (button.Kind == ButtonKind.Tool && button.Tool == Tool.Pointer)
        {
            // A classic mouse-arrow, drawn as a polygon.
            float ox = button.Bounds.X, oy = button.Bounds.Y;
            (float x, float y)[] outline = { (12, 8), (12, 27), (16.5f, 22.5f), (20, 30), (23, 28.5f), (19.5f, 21.5f), (26, 21.5f) };
            g.FillPolygon(Brushes.White, Array.ConvertAll(outline, p => new PointF(ox + p.x * scale, oy + p.y * scale)));
        }
        else if (button.Kind == ButtonKind.Color)
        {
            float d = 16 * scale;
            using (var fill = new SolidBrush(button.Color)) g.FillEllipse(fill, centerX - d / 2, centerY - d / 2, d, d);
            using (var rim = new Pen(Color.FromArgb(110, 110, 118), scale)) g.DrawEllipse(rim, centerX - d / 2, centerY - d / 2, d, d);
            if (button.Color.ToArgb() == ink.Color.ToArgb())
            {
                float ring = d + 6 * scale;
                using (var selected = new Pen(Color.White, 2 * scale))
                    g.DrawEllipse(selected, centerX - ring / 2, centerY - ring / 2, ring, ring);
            }
        }
        else if (button.Kind == ButtonKind.Size)
        {
            float d = (button.Size * 1.7f + 2) * scale;
            g.FillEllipse(Brushes.White, centerX - d / 2, centerY - d / 2, d, d);
        }
        else if (button.Glyph != null)
        {
            using (var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(button.Glyph, iconFont, Brushes.White, button.Bounds, centered);
        }
    }

    static GraphicsPath RoundedRectangle(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
