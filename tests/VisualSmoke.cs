using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
namespace SteamDirect
{
    // Explicit offline visual acceptance harness, separate from fast regression tests.
    internal static class VisualSmoke
    {
        [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int kind);
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static void Pump(int milliseconds)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(15); }
        }
        private static void Navigate(MainWindow main, string page)
        {
            typeof(MainWindow).GetMethod("NavigateTo", Private).Invoke(main, new object[] { page });
            Pump(180);
        }
        private static void Capture(Form form, string file)
        {
            form.PerformLayout();
            using (var image = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                image.Save(file);
            }
        }
        [STAThread] private static int Main(string[] arguments)
        {
            HighDpi.EnablePerMonitorContext();
            Application.EnableVisualStyles();
            string temporary = Path.Combine(Path.GetTempPath(), "SteamDirect-visual-" + Guid.NewGuid().ToString("N"));
            string output = Path.GetFullPath(arguments.Length == 0 ? "dist/visual-smoke" : arguments[0]);
            Directory.CreateDirectory(temporary); Directory.CreateDirectory(output);
            string originalLogs = LogStore.DirectoryPath;
            LogStore.DirectoryPath = Path.Combine(temporary, "Logs");
            try
            {
                using (var monitor = new HistoryMonitor(Path.Combine(temporary, "history.sqlite3"), () => new List<SteamGameProcess>()))
                {
                    monitor.Store.Initialize();
                    long stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 90000;
                    var session = new HistorySession { Id = "visual", Game = "合成验收对局 · Synthetic", Mode = "直连", Target = "192.0.2.1", Start = stamp, End = stamp + 80000 };
                    var samples = new List<HistorySample>();
                    for (int i = 0; i < 16; i++) samples.Add(new HistorySample { SessionId = session.Id, At = stamp + i * 5000, Mode = "直连", Gateway = "192.0.2.254", GatewayMs = 2, ExternalMs = i == 8 ? -1 : 20 + i, Dns = 1, AdapterUp = true });
                    monitor.Store.WriteBatch(new[] { session }, samples, new[] { new HistoryEvent { Id = "event", SessionId = session.Id, Kind = "外部探测异常", Start = stamp + 30000, End = stamp + 45000 } });
                    using (var main = new MainWindow(false, monitor))
                    {
                        main.StartPosition = FormStartPosition.Manual; main.Location = new Point(-20000, -20000); main.Show();
                        Pump(250);
                        Console.WriteLine("Native device DPI: " + main.DeviceDpi + "; client: " + main.ClientSize);
                        foreach (string mode in new[] { "dark", "light", "red" })
                        {
                            AppTheme.SetTheme(mode != "light", mode == "red" ? "#FF0000" : "#6750A4");
                            foreach (string page in new[] { "概览", "网络诊断", "游戏记录", "设置" })
                            {
                                Navigate(main, page); Pump(page == "游戏记录" ? 400 : 0);
                                Capture(main, Path.Combine(output, mode + "-" + page + ".png"));
                                if (page == "概览") { Size normal = main.Size; main.Size = main.MinimumSize; Capture(main, Path.Combine(output, mode + "-minimum.png")); main.Size = normal; }
                            }
                        }
                        using (var details = new HistoryDataWindow(monitor.Store, session, () => false))
                        {
                            details.StartPosition = FormStartPosition.Manual; details.Location = new Point(-20000, -20000); details.Show(); Pump(500);
                            Capture(details, Path.Combine(output, "details.png"));
                        }
                        AppTheme.SetTheme(true, "#6750A4");
                        Navigate(main, "概览");
                        int before = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
                        for (int i = 0; i < 12; i++) { Navigate(main, "设置"); Navigate(main, "游戏记录"); Navigate(main, "概览"); }
                        GC.Collect(); GC.WaitForPendingFinalizers(); Pump(250);
                        int after = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
                        var process = Process.GetCurrentProcess(); process.Refresh(); TimeSpan cpu = process.TotalProcessorTime; var watch = Stopwatch.StartNew();
                        double peak=0;TimeSpan previous=cpu;var sampleWatch=Stopwatch.StartNew();long last=0;
                        for(int sample=0;sample<15;sample++){Pump(1000);process.Refresh();long now=sampleWatch.ElapsedMilliseconds;peak=Math.Max(peak,(process.TotalProcessorTime-previous).TotalMilliseconds/Math.Max(1,now-last)*100);previous=process.TotalProcessorTime;last=now;}
                        process.Refresh();
                        Console.WriteLine("Idle sampling: 15 x 1s; peak core-percent="+peak.ToString("F2")+"; working-set MB="+(process.WorkingSet64/1048576.0).ToString("F1")+"; private MB="+(process.PrivateMemorySize64/1048576.0).ToString("F1"));
                        Console.WriteLine("Idle CPU core-percent: " + ((process.TotalProcessorTime - cpu).TotalMilliseconds / watch.ElapsedMilliseconds * 100).ToString("F2"));
                        Console.WriteLine("GDI after 12 settings/history/overview cycles: " + before + " -> " + after);
                        Console.WriteLine("Motion active at idle: " + Motion.ActiveCount + "; offline ping/DNS: " + monitor.PingCount + "/" + monitor.DnsCount);
                        if (after - before > 12 || Motion.ActiveCount != 0 || monitor.PingCount != 0 || monitor.DnsCount != 0) throw new Exception("Visual resource/network invariant failed.");
                        foreach (float scale in new[] { 1.75F, 2F })
                        {
                            using (var settings = new SettingsWindow(false, false, 7, true, "#6750A4", temporary, false))
                            {
                                settings.StartPosition = FormStartPosition.Manual; settings.Location = new Point(-20000, -20000); settings.Show();
                                settings.Scale(new SizeF(scale, scale)); Pump(150);
                                Capture(settings, Path.Combine(output, "settings-geometric-" + (int)(scale * 100) + ".png"));
                            }
                            using (var history = new HistoryWindow(monitor.Store))
                            {
                                history.StartPosition = FormStartPosition.Manual; history.Location = new Point(-20000, -20000); history.Show();
                                history.Scale(new SizeF(scale, scale)); Pump(500);
                                Capture(history, Path.Combine(output, "history-geometric-" + (int)(scale * 100) + ".png"));
                            }
                        }
                        Console.WriteLine("Visual captures: " + output);
                    }
                }
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally
            {
                LogStore.DirectoryPath = originalLogs; Motion.IsGaming = null; Pump(150);
                try { Directory.Delete(temporary, true); } catch (IOException) { }
            }
        }
    }
}
