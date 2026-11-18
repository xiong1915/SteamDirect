using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
namespace SteamDirect
{
    internal static class RenderAcceptance
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int kind);
        private static bool OwnForeground() { uint process; GetWindowThreadProcessId(GetForegroundWindow(), out process); return process == (uint)Process.GetCurrentProcess().Id; }
        private static void Pump(int milliseconds) { var watch = Stopwatch.StartNew(); while (watch.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Need(!OwnForeground(),"Test process activated a foreground window"); Thread.Sleep(5); } }
        private static void Await(Task task) { var watch = Stopwatch.StartNew(); while (!task.IsCompleted && watch.ElapsedMilliseconds < 10000) Pump(5); if (!task.IsCompleted) throw new TimeoutException("Fixture asynchronous operation timed out"); task.GetAwaiter().GetResult(); }
        private static object Field(object target, string name) { return target.GetType().GetField(name, Private).GetValue(target); }
        private static Task Refresh(HistoryWindow form) { var clock=Stopwatch.StartNew(); while ((bool)Field(form,"loadingRecords") && clock.ElapsedMilliseconds<10000) Pump(5); return (Task)typeof(HistoryWindow).GetMethod("RefreshAsync", Private).Invoke(form, new object[] { (int?)null }); }
        private static void Capture(Form form, string path) { form.PerformLayout(); using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(path); } }
        private static void Collect(Control root, Dictionary<Control, Font> fonts) { fonts.Add(root, root.Font); foreach (Control child in root.Controls) Collect(child, fonts); }
        private static void Need(bool value, string message) { if (!value) throw new Exception(message); }
        [STAThread] private static int Main(string[] args)
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException); Application.ThreadException += (sender,eventArgs) => { Console.Error.WriteLine(eventArgs.Exception.ToString()); Environment.Exit(1); }; WindowEnvironment.OffscreenTests = true;
            var initializeUi=typeof(HighDpi).GetMethod("InitializeUi",BindingFlags.Static|BindingFlags.NonPublic);
            if(initializeUi!=null)initializeUi.Invoke(null,null);else{HighDpi.EnablePerMonitorContext();Application.EnableVisualStyles();}
            Motion.Reduced = true;
            bool baseline = args.Length > 1 && args[1] == "baseline";
            string output = Path.GetFullPath(args[0]), temporary = Path.Combine(Path.GetTempPath(), "SteamDirect-render-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(output); Directory.CreateDirectory(temporary); LogStore.DirectoryPath = Path.Combine(temporary, "Logs");
            IntPtr foreground = GetForegroundWindow();
            var awareness=typeof(HighDpi).GetProperty("Awareness",BindingFlags.Static|BindingFlags.NonPublic); Console.WriteLine("Runtime awareness="+(awareness==null?"baseline/not-instrumented":awareness.GetValue(null,null)));
            try {
                using (var monitor = new HistoryMonitor(Path.Combine(temporary, "fixture.sqlite3"), () => new List<SteamGameProcess>())) {
                    var store = monitor.Store; store.Initialize();
                    long stamp = new DateTimeOffset(DateTime.Today.AddHours(12)).ToUnixTimeMilliseconds();
                    var sessions = new List<HistorySession>(); var samples = new List<HistorySample>(); var incidents = new List<HistoryEvent>();
                    for (int i = 0; i < 16; i++) {
                        var session = new HistorySession { Id = "fixture-" + i, Game = i == 0 ? "Counter-Strike 2 · 合成对局" : "验收游戏 " + i, Mode = i % 2 == 0 ? "直连" : "代理", Target = "192.0.2.1", Start = stamp - i * 600000, End = stamp - i * 600000 + 300000 };
                        sessions.Add(session);
                        for (int n = 0; n < 60; n++) samples.Add(new HistorySample { SessionId = session.Id, At = session.Start + n * 5000, Mode = session.Mode, Gateway = "192.0.2.254", GatewayMs = 2, ExternalMs = n >= 28 && n <= 30 ? -1 : 20 + n % 9, Dns = 1, AdapterUp = true });
                        if (i == 0) incidents.Add(new HistoryEvent { Id = "issue", SessionId = session.Id, Kind = "外部探测异常", Start = session.Start + 140000, End = session.Start + 160000 });
                    }
                    sessions.Add(new HistorySession { Id = "yesterday", Game = "昨日记录", Mode = "直连", Target = "192.0.2.1", Start = stamp - 86400000, End = stamp - 86000000 });
                    store.WriteBatch(sessions, samples, incidents);
                    if (args.Length > 1 && args[1] == "desktop") { MainMatrix(monitor, output); return 0; }
                    int[,] matrix = { {1920,1080,96}, {1920,1080,120}, {2560,1440,96}, {2560,1440,144}, {3840,2160,144}, {3840,2160,168}, {3840,2160,192} };
                    foreach (string theme in new[] { "dark", "light", "red" }) {
                        AppTheme.SetTheme(theme != "light", theme == "red" ? "#FF0000" : "#6750A4");
                        for (int m = 0; m < matrix.GetLength(0); m++) {
                            if (baseline && m != 1 && m != 5) continue;
                            using (var form = new HistoryWindow(store)) {
                                Console.WriteLine("BEGIN fixture "+theme+" matrix="+m);
                                form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-24000,-24000); form.Show(); Await(Refresh(form));
                                Console.WriteLine("READY data "+theme+" matrix="+m);
                                int native = form.DeviceDpi, desired = matrix[m,2]; WindowEnvironment.LayoutDpi = desired;
                                // Geometric/font simulation only. Does not claim a physical DPI or display switch.
                                var originals = new Dictionary<Control,Font>(); Collect(form, originals); var owned = new List<Font>();
                                try {
                                    form.AutoScaleMode = AutoScaleMode.None; form.MinimumSize = Size.Empty;
                                    float factor = desired / (float)native; form.Scale(new SizeF(factor,factor));
                                    foreach (var item in originals) { var font = new Font(item.Value.FontFamily, item.Value.Size * factor, item.Value.Style); owned.Add(font); item.Key.Font = font; }
                                    form.ClientSize = new Size(matrix[m,0] - 80, matrix[m,1] - 100); form.PerformLayout(); Await(Refresh(form)); Pump(20);
                                    var records = (ListView)Field(form,"records"); Need(records.Items.Count > 0, "History load failed");
                                    Capture(form, Path.Combine(output, theme + "-" + matrix[m,0] + "x" + matrix[m,1] + "-" + desired + "dpi-simulated.png"));
                                    if (m == 1 && !baseline) {
                                        Console.WriteLine("BEGIN keyword"); var filter=(TextBox)Field(form,"gameFilter"); filter.Text="不存在的游戏"; Await(Refresh(form)); Need(records.Items.Count==0,"Keyword filter failed"); filter.Clear();
                                        Console.WriteLine("BEGIN mode"); var mode=(ComboBox)Field(form,"modeFilter"); mode.SelectedIndex=2; Await(Refresh(form)); foreach(ListViewItem item in records.Items) Need(((HistorySession)item.Tag).Mode=="代理","Mode filter failed"); mode.SelectedIndex=0;
                                        Console.WriteLine("BEGIN scope"); var scope=(ComboBox)Field(form,"recordScope"); scope.SelectedIndex=1; Await(Refresh(form)); filter.Text="昨日记录"; Await(Refresh(form)); Need(records.Items.Count==1,"History scope failed"); filter.Clear(); scope.SelectedIndex=0; Await(Refresh(form));
                                        Console.WriteLine("BEGIN tabs"); foreach(Control tab in Descendants(form)) if(tab is HistorySections) { var sections=(HistorySections)tab; for(int n=0;n<sections.PageCount;n++){ sections.SelectPage(n); Capture(form,Path.Combine(output,theme+"-tab-"+n+".png")); } }
                                        Console.WriteLine("BEGIN export"); var export=typeof(HistoryWindow).GetMethod("WriteCsv",Private); export.Invoke(form,new object[]{Path.Combine(output,theme+"-fixture.csv"),sessions[0],CancellationToken.None}); Need(File.ReadAllText(Path.Combine(output,theme+"-fixture.csv")).Contains("incident"),"CSV export failed");
                                    }
                                    Console.WriteLine("PASS layout simulation " + theme+" "+matrix[m,0]+"x"+matrix[m,1]+" requested_dpi="+desired+" native_dpi="+native+" client="+form.ClientSize);
                                } finally { form.Dispose(); WindowEnvironment.LayoutDpi = 0; foreach(var font in owned) font.Dispose(); }
                            }
                        }
                    }
                    if (baseline) { Console.WriteLine("Baseline matched-size synthetic captures only; no business or performance pass implied."); return 0; }
                    MainMatrix(monitor,output);
                    using(var main=new MainWindow(false,monitor)) {
                        main.ShowInTaskbar=false;main.StartPosition=FormStartPosition.Manual;main.Location=new Point(-24000,-24000);main.Show();Pump(20);
                        int before; using(var process=Process.GetCurrentProcess()) before=GetGuiResources(process.Handle,0);
                        foreach(string page in new[]{"概览","游戏记录","网络诊断","设置"}) {
                            typeof(MainWindow).GetMethod("NavigateTo",Private).Invoke(main,new object[]{page});Pump(50);
                            var diagnostic=Field(main,"windowDiagnostics"); diagnostic.GetType().GetField("Enabled",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(diagnostic,true);
                            IntPtr handle=main.Handle; var watch=Stopwatch.StartNew();
                            for(int n=0;n<20;n++){main.Hide();main.ShowMainWindow();Need(main.Handle==handle,"Unexpected handle reconstruction on restore");diagnostic.GetType().GetMethod("Complete",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(diagnostic,null);}
                            Console.WriteLine("PASS offscreen restore " +page+" 20cycles synchronous_request_mean_ms="+(watch.Elapsed.TotalMilliseconds/20).ToString("F3"));
                            Console.WriteLine(diagnostic.GetType().GetProperty("LastResult",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(diagnostic,null));
                        }
                        main.Hide();Pump(20);Need(Motion.ActiveCount==0,"Hidden animations still active");GC.Collect();GC.WaitForPendingFinalizers();
                        using(var process=Process.GetCurrentProcess()) Console.WriteLine("GDI before/after restore pages="+before+"/"+GetGuiResources(process.Handle,0)+" working_set_MB="+(process.WorkingSet64/1048576.0).ToString("F1"));
                        Need(monitor.PingCount==0&&monitor.DnsCount==0,"Fixture accessed network");
                    }
                }
                Need(!OwnForeground(),"Test process owns foreground window");
                if (GetForegroundWindow()!=foreground) Console.WriteLine("User foreground window changed during test; no fixture foreground activation observed.");
                Console.WriteLine("PASS offscreen captures / isolated SQLite / keyword / mode / range / CSV / four tabs / 80 restore cycles. Physical DPI, tray shell clicks and full compositor latency: BLOCKED (no isolated GUI session)."); return 0;
            } catch(Exception ex) {Console.Error.WriteLine(ex);return 1;}
            finally {Motion.IsGaming=null;try{Directory.Delete(temporary,true);}catch(IOException){}}
        }
        private static IEnumerable<Control> Descendants(Control root){foreach(Control child in root.Controls){yield return child;foreach(Control nested in Descendants(child))yield return nested;}}
        private static void MainMatrix(HistoryMonitor monitor,string output)
        {
            int[,] sizes={{1280,800,96},{1920,1080,96},{1920,1080,120},{1920,1080,144},{1920,1080,168},{1920,1080,192},{3840,2160,168},{3840,2160,192}};
            for (int index = 0; index < sizes.GetLength(0); index++) {
                using(var main=new MainWindow(false,monitor,new UserPreferences{NightMode=true,ThemeColor="#3269C7",NetworkRecording=false})){
                    main.ShowInTaskbar=false;main.FormBorderStyle=FormBorderStyle.None;main.StartPosition=FormStartPosition.Manual;main.Location=new Point(-24000,-24000);main.Show();
                    var navigate=typeof(MainWindow).GetMethod("NavigateTo",Private);var manager=Field(main,"pages");
                    foreach(string page in new[]{"网络诊断","游戏记录","设置","概览"}){navigate.Invoke(main,new object[]{page});Pump(40);}
                    var original=new Dictionary<Control,Font>();Collect(main,original);var owned=new List<Font>();
                    try{
                        int dpi=sizes[index,2];WindowEnvironment.LayoutDpi=dpi;float factor=dpi/(float)main.DeviceDpi;
                        main.AutoScaleMode=AutoScaleMode.None;main.MinimumSize=Size.Empty;foreach(Control control in Descendants(main)){var form=control as Form;if(form!=null)form.AutoScaleMode=AutoScaleMode.None;}
                        main.Scale(new SizeF(factor,factor));foreach(var entry in original){var font=new Font(entry.Value.FontFamily,entry.Value.Size*factor,entry.Value.Style);owned.Add(font);entry.Key.Font=font;}
                        OffscreenGeometry.Resize(main,new Size(sizes[index,0]-80,sizes[index,1]-100));main.PerformLayout();
                        Need(main.ClientSize==new Size(sizes[index,0]-80,sizes[index,1]-100),"Requested desktop simulation geometry was constrained");
                        foreach(string page in new[]{"概览","网络诊断","游戏记录","设置"}){
                            navigate.Invoke(main,new object[]{page});Pump(30);
                            foreach (var control in Descendants(main))
                            {
                                var button = control as RoundedButton;
                                if (button != null && button.Visible && !string.IsNullOrWhiteSpace(button.Text))
                                {
                                    CheckCaption(button, button.Text, button.CaptionBounds, page, dpi);
                                }
                                var combo = control as DarkComboBox;
                                if (combo != null && combo.Visible)
                                    foreach (var option in combo.Items)
                                        CheckCaption(combo, combo.GetItemText(option), combo.CaptionBounds, page, dpi);
                            }
                            Capture(main,Path.Combine(output,"desktop-"+sizes[index,0]+"-"+dpi+"-"+page+".png"));
                            var active=(Form)manager.GetType().GetProperty("ActivePage",Private).GetValue(manager,null);
                            if(active!=null)Need(active.Width<=main.ClientSize.Width&&active.Height>0,"Desktop page exceeds window");
                            if(page=="概览"){var run=(Button)Field(main,"runButton");var log=(TextBox)Field(main,"logBox");Need(run.Height>=UiStyle.Scale(run,32),"Main action collapsed");Need(log.Height>=UiStyle.Scale(log,60),"Overview log collapsed");}
                            if (page == "网络诊断")
                            {
                                var conclusion = (TextBox)Field(active, "conclusion");
                                var details = (TextBox)Field(active, "details");
                                Need(conclusion.Height >= UiStyle.Scale(conclusion, 40), "Diagnostic conclusion collapsed");
                                Need(details.Height >= UiStyle.Scale(details, 32), "Diagnostic details collapsed");
                            }
                            if (page == "游戏记录")
                            {
                                var records = (ListView)Field(active, "records");
                                var report = (RichTextBox)Field(active, "overviewText");
                                Need(records.Height >= UiStyle.Scale(records, 100), "History list collapsed");
                                Need(report.Height >= UiStyle.Scale(report, 80), "History report collapsed");
                                if (sizes[index,0] == 1920 && dpi >= 168)
                                    CaptureHistoryTabs(main, (HistoryWindow)active, output, dpi);
                            }
                        }
                        Console.WriteLine("PASS desktop simulation "+sizes[index,0]+"x"+sizes[index,1]+" dpi="+dpi+" four pages / recording off");
                    }finally{main.Dispose();WindowEnvironment.LayoutDpi=0;foreach(var font in owned)font.Dispose();}
                }
            }
        }
        private static void CaptureHistoryTabs(MainWindow main, HistoryWindow history, string output, int dpi)
        {
            Await(Refresh(history));
            var targets = new[] { (Control)Field(history, "networkChart"), (Control)Field(history, "incidentSummary"),
                (Control)Field(history, "comparisonText") };
            Control section = targets[2];
            while (!(section is HistorySections)) section = section.Parent;
            var buttons = (FlowLayoutPanel)Field(section, "navigation");
            for (int index = 1; index <= targets.Length; index++)
            {
                ((Button)buttons.Controls[index]).PerformClick();
                Pump(25);
                var target = targets[index - 1];
                var visible = target.RectangleToScreen(target.ClientRectangle);
                for (Control parent = target.Parent; parent != null; parent = parent.Parent)
                    visible.Intersect(parent.RectangleToScreen(parent.ClientRectangle));
                Need(visible.Height >= UiStyle.Scale(main, 32) && visible.Width > 0, "History tab content was clipped: " + index + " dpi=" + dpi);
                Capture(main, Path.Combine(output, "history-click-" + dpi + "-" + index + ".png"));
            }
        }
        private static void CheckCaption(Control control, string text, Rectangle bounds, string page, int dpi)
        {
            Size caption = TextRenderer.MeasureText(text, control.Font, Size.Empty, RoundedButton.CaptionFlags);
            Need(caption.Width <= bounds.Width && caption.Height <= bounds.Height,
                page + " clipped caption: " + text + " " + caption + " available=" + bounds.Size + " dpi=" + dpi);
        }
    }
}
