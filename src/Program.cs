using System;
using System.Threading;
using System.Windows.Forms;

namespace StickyInk;

internal static class Program
{
    /// <summary>True when started with --selftest. See <see cref="SelfTest"/>.</summary>
    public static bool SelfTestMode { get; private set; }

    [STAThread]
    static void Main(string[] args)
    {
        SelfTestMode = args.Length > 0 && args[0] == "--selftest";

        // Only one copy at a time: two overlays and two mouse hooks would fight each other.
        using var singleInstance = new Mutex(true, "StickyInk_SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance) return;

        // Must happen before any window exists. From here on, every coordinate in the app
        // is a physical pixel - there is no DPI virtualization to think about.
        bool perMonitor = false;
        try { perMonitor = Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
        catch (EntryPointNotFoundException) { /* older Windows 10 */ }
        if (!perMonitor) Native.SetProcessDPIAware();

        Application.EnableVisualStyles();
        Application.Run(new ToolbarForm());
    }
}
