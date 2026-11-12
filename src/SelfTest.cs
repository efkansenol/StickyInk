using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace StickyInk;

/// <summary>
/// End-to-end test of the scroll-following, run with `StickyInk.exe --selftest`
/// (or simply `test.cmd`).
///
/// It opens its own window full of text, draws two strokes on it (one over text,
/// one in a blank area), scrolls the window in steps and checks that both strokes
/// moved by exactly the same number of pixels. It then deliberately knocks the
/// strokes out of place and checks that the anchors put them back.
///
/// The result is written to selftest.txt next to the exe: a line starting with
/// PASS or FAIL. No input is injected; the mouse and keyboard are not touched.
///
/// RUN THIS AFTER ANY CHANGE to ScrollTracker, StrokeFollower, AnchorRelocator,
/// InkController or Pane.
/// </summary>
internal sealed class SelfTest
{
    /// <summary>A window that paints numbered lines of text and can be "scrolled" by changing ScrollY.</summary>
    sealed class TestPage : Form
    {
        public int ScrollY;

        public TestPage()
        {
            Text = "StickyInk self-test";
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            Bounds = new Rectangle(120, 160, 820, 620);
            TopMost = true;
            BackColor = Color.White;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.White);
            using (var font = new Font("Segoe UI", 15, GraphicsUnit.Pixel))
            {
                for (int i = 0; i < 300; i++)
                {
                    int y = i * 24 - ScrollY;
                    if (y < -30 || y > ClientSize.Height) continue;
                    // The numbers make every line unique, like real text.
                    e.Graphics.DrawString($"Line {i} - sample text {i * 7919 % 1000} {i * 104729 % 977}", font, Brushes.Black, 12, y);
                }
            }
        }
    }

    const int ScrollDownSteps = 12, ScrollDownAmount = 37;
    const int ScrollUpSteps = 5, ScrollUpAmount = 50;
    const int InjectedDrift = 13;

    readonly ToolbarForm toolbar;
    readonly InkController ink;
    readonly Timer timer = new Timer { Interval = 150 };
    TestPage page;
    int step;

    public SelfTest(ToolbarForm toolbar, InkController ink)
    {
        this.toolbar = toolbar;
        this.ink = ink;
    }

    public void Start()
    {
        page = new TestPage { ScrollY = 600 };
        page.Show();
        timer.Tick += (_, _) => RunStep(++step);
        timer.Start();
    }

    void RunStep(int n)
    {
        if (n == 5)
        {
            ink.SetTool(Tool.Pen);
            DrawSquiggle(page.PointToScreen(new Point(150, 300)));   // over text
            DrawSquiggle(page.PointToScreen(new Point(650, 300)));   // in the blank right half
        }
        else if (n == 7) ink.SetTool(Tool.Pointer);
        else if (n >= 10 && n < 10 + ScrollDownSteps) Scroll(ScrollDownAmount);
        else if (n >= 22 && n < 22 + ScrollUpSteps) Scroll(-ScrollUpAmount);
        else if (n == 30)
        {
            // Simulate accumulated drift; the anchors must repair it after the next scroll.
            foreach (Stroke s in ink.Strokes) s.OffsetY += InjectedDrift;
        }
        else if (n == 32) Scroll(ScrollDownAmount);
        else if (n == 34) Scroll(-ScrollDownAmount);
        else if (n == 42) Finish();
    }

    void Scroll(int pixels)
    {
        page.ScrollY += pixels;
        page.Refresh();
    }

    void DrawSquiggle(Point center)
    {
        ink.PostMouse(MouseKind.Down, center.X - 40, center.Y);
        for (int i = 1; i <= 40; i++)
            ink.PostMouse(MouseKind.Move, center.X - 40 + i * 2, center.Y + (int)(15 * Math.Sin(i / 3.0)));
        ink.PostMouse(MouseKind.Up, center.X + 40, center.Y);
    }

    void Finish()
    {
        timer.Stop();

        // The page ended up this much further down than where the strokes were drawn,
        // so the strokes must have moved up by the same amount.
        int expected = -(ScrollDownSteps * ScrollDownAmount - ScrollUpSteps * ScrollUpAmount);
        bool pass = ink.Strokes.Count == 2;
        string details = $"expected offset {expected};";
        foreach (Stroke s in ink.Strokes)
        {
            if (s.OffsetY != expected) pass = false;
            details += $" stroke offset {s.OffsetY} (anchor cells: {s.Anchor?.Count ?? 0});";
        }

        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest.txt"), (pass ? "PASS " : "FAIL ") + details);
        page.Close();
        toolbar.Close();
    }
}
