using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SteamDirect
{
    // Opt-in only. Real monitor, normal message loop, isolated data and non-activating HWND.
    // Paint dispatch, UI queue latency and changes observed in a desktop pixel are distinct clocks.
    internal static class DesktopPerformance
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flag);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr order, int x, int y, int width, int height, uint flags);
        [DllImport("dwmapi.dll")] private static extern int DwmFlush();
        [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int kind);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateDC(string driver, string device, string output, IntPtr initialization);
        [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr dc, int index);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        private static readonly MethodInfo Navigate = typeof(MainWindow).GetMethod("NavigateTo", Private);
        private static double Ms(long ticks) { return ticks * 1000.0 / Stopwatch.Frequency; }
        private static string Distribution(List<double> values)
        {
            if (values.Count == 0) return "n=0";
            var sorted = values.OrderBy(x => x).ToArray();
            return string.Format("n={0} p50={1:F2} p95={2:F2} max={3:F2}", sorted.Length,
                sorted[(sorted.Length - 1) / 2], sorted[(int)Math.Ceiling(sorted.Length * .95) - 1], sorted[sorted.Length - 1]);
        }
        internal static void Run(string directory)
        {
            using (var loop = new ApplicationContext())
            using (var dispatcher = new Control())
            {
                var handle = dispatcher.Handle;
                Exception error = null;
                dispatcher.BeginInvoke((Action)(async () =>
                {
                    try
                    {
                        foreach (var screen in Screen.AllScreens.OrderByDescending(x => x.Primary))
                            await Measure(screen, directory);
                    }
                    catch (Exception ex) { error = ex; }
                    finally { loop.ExitThread(); }
                }));
                Application.Run(loop);
                if (error != null) throw new InvalidOperationException("Interactive benchmark failed", error);
            }
        }
        private sealed class PaintClock : NativeWindow, IDisposable
        {
            internal readonly List<double> Gaps = new List<double>();
            private long last;
            internal PaintClock(Control control) { AssignHandle(control.Handle); }
            internal void NewTransition() { last = 0; }
            protected override void WndProc(ref Message message)
            {
                base.WndProc(ref message);
                if (message.Msg != 15) return;
                long now = Stopwatch.GetTimestamp();
                if (last != 0) Gaps.Add(Ms(now - last));
                last = now;
            }
            public void Dispose() { ReleaseHandle(); }
        }
        private static async Task Measure(Screen screen, string directory)
        {
            using (var monitor = new HistoryMonitor(Path.Combine(directory, "fixture.sqlite3"), () => new List<SteamGameProcess>()))
            using (var main = new MainWindow(true, monitor, new UserPreferences {
                NightMode = true, TrayEnabled = true, NetworkRecording = false, ReduceMotion = false }))
            using (var process = Process.GetCurrentProcess())
            {
                main.ShowInTaskbar = false;
                main.StartPosition = FormStartPosition.Manual;
                main.Location = new Point(screen.WorkingArea.Left + 8, screen.WorkingArea.Top + 8);
                main.Show(); // background startup
                main.MinimumSize = Size.Empty;
                var area = screen.WorkingArea;
                int dpi = main.DeviceDpi;
                main.Size = new Size(Math.Min(area.Width - 16, 1160 * dpi / 96), Math.Min(area.Height - 16, 700 * dpi / 96));
                await Task.Delay(300);
                var cpuStart = process.TotalProcessorTime;
                var elapsed = Stopwatch.StartNew();
                var timer = Stopwatch.StartNew();
                main.ShowMainWindow();
                double first = timer.Elapsed.TotalMilliseconds;
                var warm = new List<double>();
                var revisit = new List<double>();
                await Task.Delay(150);
                var active = typeof(MainWindow).GetField("navigation", Private);
                var buttons = (Dictionary<string, Button>)active.GetValue(main);
                Button button = buttons["设置"];
                IntPtr hwnd = main.Handle;
                if (Screen.FromHandle(hwnd).DeviceName != screen.DeviceName || GetForegroundWindow() == hwnd)
                    throw new InvalidOperationException("Window monitor/focus changed");
                var dc = CreateDC("DISPLAY", screen.DeviceName, null, IntPtr.Zero);
                int hz;
                try { hz = GetDeviceCaps(dc, 116); } finally { if (dc != IntPtr.Zero) DeleteDC(dc); }
                Console.WriteLine("DESKTOP " + screen.DeviceName + " resolution=" + screen.Bounds.Size + " dpi=" + main.DeviceDpi + " hz=" + hz);
                var dispatch = new List<double>();
                using (var stop = new CancellationTokenSource())
                {
                    var replies = Task.Run(async () =>
                    {
                        while (!stop.IsCancellationRequested)
                        {
                            long posted = Stopwatch.GetTimestamp();
                            var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                            main.BeginInvoke((Action)(() => { dispatch.Add(Ms(Stopwatch.GetTimestamp() - posted)); ack.SetResult(true); }));
                            await ack.Task.ConfigureAwait(false);
                            await Task.Delay(12).ConfigureAwait(false);
                        }
                    });
                    try
                    {
                        for (int i = 0; i < 6; i++)
                        {
                            main.Hide();
                            await Task.Delay(40);
                            timer.Restart(); main.ShowMainWindow(); warm.Add(timer.Elapsed.TotalMilliseconds);
                            await Task.Delay(60);
                        }
                        timer.Restart(); Navigate.Invoke(main, new object[] { "游戏记录" });
                        double history = timer.Elapsed.TotalMilliseconds;
                        await Task.Delay(250);
                        for (int i = 0; i < 6; i++)
                        {
                            Navigate.Invoke(main, new object[] { "概览" });
                            await Task.Delay(65);
                            timer.Restart(); Navigate.Invoke(main, new object[] { "游戏记录" }); revisit.Add(timer.Elapsed.TotalMilliseconds);
                            await Task.Delay(65);
                        }
                        Console.WriteLine("UI first_show_ms=" + first.ToString("F2") + " warm_show_ms " + Distribution(warm) +
                            " history_first_ms=" + history.ToString("F2") + " history_cached_ms " + Distribution(revisit));
                        var gaps = new List<double>();
                        int changes = 0;
                        // Raise only our non-activating window in normal z-order. Never move the user's pointer.
                        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x13);
                        await Task.Delay(75);
                        var point = button.PointToScreen(new Point(button.Width / 2, button.Height - 8));
                        if (GetAncestor(WindowFromPoint(point), 2) != hwnd)
                        {
                            main.Location = new Point(area.Right - main.Width - 8, area.Bottom - main.Height - 8);
                            await Task.Delay(100);
                            point = button.PointToScreen(new Point(button.Width / 2, button.Height - 8));
                        }
                        using (var paint = new PaintClock(button))
                        {
                            for (int i = 0; i < 8; i++)
                            {
                                paint.NewTransition();
                                var sampling = Task.Run(() => SamplePixel(hwnd, point, gaps));
                                typeof(RoundedButton).GetMethod(i % 2 == 0 ? "OnMouseEnter" : "OnMouseLeave", Private)
                                    .Invoke(button, new object[] { EventArgs.Empty });
                                changes += await sampling;
                                await Task.Delay(45);
                            }
                            Console.WriteLine("HOVER paint_dispatch_gap_ms " + Distribution(paint.Gaps) +
                                " observed_pixel_gap_ms " + Distribution(gaps) + " pixel_changes=" + changes);
                        }
                        if (changes == 0) Console.WriteLine("BLOCKED desktop pixel transitions not observed (occlusion or reduced motion).");
                    }
                    finally { stop.Cancel(); }
                    await replies;
                }
                process.Refresh();
                Console.WriteLine("LOAD ui_dispatch_delay_ms " + Distribution(dispatch) + " wall_ms=" + elapsed.ElapsedMilliseconds +
                    " process_cpu_ms=" + (process.TotalProcessorTime - cpuStart).TotalMilliseconds.ToString("F1") +
                    " working_set_bytes=" + process.WorkingSet64 + " gdi_objects=" + GetGuiResources(process.Handle, 0));
                if (GetForegroundWindow() == hwnd) throw new InvalidOperationException("Fixture took foreground");
            }
        }
        private static int SamplePixel(IntPtr hwnd, Point point, List<double> gaps)
        {
            var time = Stopwatch.StartNew();
            uint previous = uint.MaxValue;
            long lastChange = 0;
            int changes = 0;
            var dc = GetDC(IntPtr.Zero);
            try
            {
                while (time.ElapsedMilliseconds < 260)
                {
                    if (GetAncestor(WindowFromPoint(point), 2) != hwnd) { previous = uint.MaxValue; lastChange = 0; }
                    else
                    {
                        uint color = GetPixel(dc, point.X, point.Y);
                        long now = time.ElapsedTicks;
                        if (previous != uint.MaxValue && color != previous && color != uint.MaxValue)
                        {
                            if (lastChange != 0) gaps.Add(Ms(now - lastChange));
                            lastChange = now; changes++;
                        }
                        previous = color;
                    }
                    if (DwmFlush() < 0) Thread.Sleep(4);
                }
            }
            finally { ReleaseDC(IntPtr.Zero, dc); }
            return changes;
        }
    }
}
