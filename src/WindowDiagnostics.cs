using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SteamDirect
{
    // Opt-in, one bounded observation per restore. No idle sampling or per-frame disk writes.
    internal sealed class WindowDiagnostics : IDisposable
    {
        private readonly Form window;
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 50 };
        private static int writerBusy;
        private readonly Stopwatch clock = new Stopwatch();
        private int layouts, paints, created, destroyed, gdiBefore;
        private long memoryBefore, firstPaint, shown, paintsFlushed, lastTick, maximumGap;
        internal bool Enabled;
        internal string LastResult { get; private set; }
        [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int flags);
        internal WindowDiagnostics(Form window)
        {
            this.window = window;
            window.Layout += Layout; window.Paint += Paint;
            window.HandleCreated += Created; window.HandleDestroyed += Destroyed;
            timer.Tick += Tick;
        }
        internal void Begin()
        {
            if (!Enabled || clock.IsRunning || (Motion.IsGaming != null && Motion.IsGaming())) return;
            layouts = paints = created = destroyed = 0; shown = firstPaint = paintsFlushed = -1; maximumGap = lastTick = 0;
            using (var process = Process.GetCurrentProcess()) { memoryBefore = process.WorkingSet64; gdiBefore = GetGuiResources(process.Handle, 0); }
            clock.Restart(); timer.Start();
        }
        internal void Displayed() { if (clock.IsRunning) shown = clock.ElapsedMilliseconds; }
        internal void PaintsFlushed() { if (clock.IsRunning) paintsFlushed = clock.ElapsedMilliseconds; }
        private void Layout(object sender, LayoutEventArgs e) { if (clock.IsRunning) layouts++; }
        private void Paint(object sender, PaintEventArgs e) { if (clock.IsRunning) { paints++; if (firstPaint < 0) firstPaint = clock.ElapsedMilliseconds; } }
        private void Created(object sender, EventArgs e) { if (clock.IsRunning) created++; }
        private void Destroyed(object sender, EventArgs e) { if (clock.IsRunning) destroyed++; }
        private void Tick(object sender, EventArgs e)
        {
            long now = clock.ElapsedMilliseconds; maximumGap = Math.Max(maximumGap, Math.Max(0, now - lastTick - timer.Interval)); lastTick = now;
            if (!window.Visible || now >= 1000 || (Motion.IsGaming != null && Motion.IsGaming())) Complete();
        }
        internal void Complete()
        {
            if (!clock.IsRunning) return;
            timer.Stop(); clock.Stop();
            using (var process = Process.GetCurrentProcess())
            {
                var screen = Screen.FromControl(window);
                LastResult = DateTime.Now.ToString("s") + " restore_request=0ms shown=" + shown + "ms parent_first_paint=" + firstPaint +
                    "ms child_paint_flush=" + paintsFlushed + "ms observation=" + clock.ElapsedMilliseconds + "ms UI_queue_delay_max=" + maximumGap + "ms layouts=" + layouts + " paints=" + paints +
                    " HWND_create/destroy=" + created + "/" + destroyed + " GDI=" + gdiBefore + "->" + GetGuiResources(process.Handle, 0) +
                    " working_set_delta=" + (process.WorkingSet64 - memoryBefore) + " screen=" + screen.DeviceName + " bounds=" + screen.Bounds +
                    " dpi=" + window.DeviceDpi + " motion_active=" + Motion.ActiveCount;
            }
            // Parent Paint and queue delay are proxies; not a claim of child composition or exact UI blocking.
            Record(LastResult);
        }
        internal static void Record(string result)
        {
            if (WindowEnvironment.OffscreenTests) return;
            string directory = LogStore.DirectoryPath;
            if (Interlocked.CompareExchange(ref writerBusy, 1, 0) != 0) return;
            Task.Run(() => { try { Write(directory, result); } finally { Interlocked.Exchange(ref writerBusy, 0); } });
        }
        private static void Write(string directory, string result)
        {
            try {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "SteamDirect-window-diagnostics.log");
                if (File.Exists(path) && new FileInfo(path).Length > 262144) { string old = path + ".1"; if (File.Exists(old)) File.Delete(old); File.Move(path, old); }
                File.AppendAllText(path, result + Environment.NewLine);
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        public void Dispose()
        {
            timer.Stop(); timer.Dispose(); clock.Stop();
            window.Layout -= Layout; window.Paint -= Paint; window.HandleCreated -= Created; window.HandleDestroyed -= Destroyed;
        }
    }
}
