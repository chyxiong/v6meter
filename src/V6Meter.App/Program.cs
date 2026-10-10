using System.Diagnostics;

namespace V6Meter.App;

internal static class Program
{
    /// <param name="args">
    /// --show: open the main window on start.
    /// --after &lt;pid&gt;: wait for that process to exit first (used when restarting elevated).
    /// --render-ui &lt;png&gt;: debug aid, renders the main window to a PNG after a few seconds and exits.
    /// --autostart on|off --sid &lt;sid&gt;: elevated helper that registers or removes the logon task, then exits.
    /// --autostart status: exits with the autostart state (0 off, 1 on, 2 moved, 3 other copy).
    /// </param>
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--autostart"))
            return Autostart.RunHelper(args);

        int after = Array.IndexOf(args, "--after");
        if (after >= 0 && after + 1 < args.Length && int.TryParse(args[after + 1], out int pid))
        {
            try { using var prev = Process.GetProcessById(pid); prev.WaitForExit(10_000); }
            catch (ArgumentException) { } // already gone
        }

        using var mutex = new Mutex(initiallyOwned: true, @"Global\v6meter-single-instance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("v6meter 已经在运行了，请在任务栏通知区域找到它的图标。", "v6meter", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        ApplicationConfiguration.Initialize();
        // A tray monitor should survive a bug in a UI action: report it and keep running.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) =>
            MessageBox.Show($"操作出错，程序会继续运行：\n{e.Exception.Message}\n\n{e.Exception.GetType().Name}", "v6meter", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        int render = Array.IndexOf(args, "--render-ui");
        var app = new TrayApp(showWindow: args.Contains("--show"), renderPath: render >= 0 && render + 1 < args.Length ? args[render + 1] : null);
        Application.Run(app);
        GC.KeepAlive(mutex);
        return 0;
    }
}
