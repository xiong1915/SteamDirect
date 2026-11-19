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
    internal static class NavigationAcceptance
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int kind);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h,int m,IntPtr w,IntPtr l);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
        [DllImport("gdi32.dll",EntryPoint="GetObjectW")]private static extern int GetFontObject(IntPtr h,int size,IntPtr data);
        private static void FontMetrics(MainWindow main){var text=(TextBox)Field(main,"logBox");IntPtr data=Marshal.AllocHGlobal(92);try{Need(GetFontObject(SendMessage(text.Handle,0x31,IntPtr.Zero,IntPtr.Zero),92,data)==92,"Cannot inspect native log font");int height=Math.Abs(Marshal.ReadInt32(data));Console.WriteLine("LOGFONT managed_points="+text.Font.SizeInPoints+" native_height="+height+" root_dpi="+main.DeviceDpi+" text_dpi="+text.DeviceDpi+" root_font_points="+main.Font.SizeInPoints);Need(height>=Math.Round(text.Font.SizeInPoints*main.DeviceDpi/72.0)-1,"Native log font did not scale to window DPI");}finally{Marshal.FreeHGlobal(data);}}
        private static void Need(bool value, string message) { if (!value) throw new Exception(message); }
        private static void Pump(int ms) { var timer=Stopwatch.StartNew(); do { Application.DoEvents(); uint id; GetWindowThreadProcessId(GetForegroundWindow(),out id); Need(id!=(uint)Process.GetCurrentProcess().Id,"Fixture took foreground"); Thread.Sleep(2); } while(timer.ElapsedMilliseconds<ms); }
        private static object Field(object obj,string name) { return obj.GetType().GetField(name,Private).GetValue(obj); }
        private static Form Current(MainWindow main) { var field=typeof(MainWindow).GetField("pages",Private); if(field!=null){var manager=field.GetValue(main);return (Form)manager.GetType().GetProperty("ActivePage",Private).GetValue(manager,null);}return (Form)Field(main,"activePage"); }
        [STAThread] private static int Main(string[] args)
        {
            WindowEnvironment.OffscreenTests=true; Motion.Reduced=true;
            if(args.Length>=4&&args[3]=="production") {
                var initialize=typeof(HighDpi).GetMethod("InitializeUi",BindingFlags.Static|BindingFlags.NonPublic);
                if(initialize!=null)initialize.Invoke(null,null);else{Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);}
            }else{HighDpi.EnablePerMonitorContext();Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);}
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException+=(s,e)=>{Console.Error.WriteLine(e.Exception);Environment.Exit(1);};
            using (var context = new ApplicationContext())
            using (var dispatcher = new Control())
            {
                var handle = dispatcher.Handle;
                int exitCode = 1;
                dispatcher.BeginInvoke((Action)(() =>
                {
                    try { exitCode = Run(args); }
                    finally { context.ExitThread(); }
                }));
                Application.Run(context);
                return exitCode;
            }
        }

        private static int Run(string[] args)
        {
            string temp=Path.Combine(Path.GetTempPath(),"SteamDirect-navigation-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp); LogStore.DirectoryPath=Path.Combine(temp,"Logs");
            try {
                bool baseline=args.Length>1&&args[1]=="baseline",fourK=args.Length>2&&args[2]=="4k";
                using(var monitor=new HistoryMonitor(Path.Combine(temp,"history.sqlite3"),()=>new List<SteamGameProcess>())) {
                    monitor.Store.Initialize(); long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    monitor.Store.WriteBatch(new[]{new HistorySession{Id="nav",Game="Navigation fixture",Mode="直连",Target="192.0.2.1",Start=now-600000,End=now}},null,null);
                    using(var main=new MainWindow(false,monitor,new UserPreferences{NightMode=true,ThemeColor="#3269C7",NetworkRecording=false})) {
                        main.ShowInTaskbar=false;main.StartPosition=FormStartPosition.Manual;main.Location=new Point(-24000,-24000);main.MinimumSize=Size.Empty;
                        if(fourK)main.FormBorderStyle=FormBorderStyle.None; // Avoid host-monitor tracking limits in the synthetic 4K fixture.
                        main.Show();OffscreenGeometry.Resize(main,fourK?new Size(3760,2060):new Size(1840,980));Pump(20);
                        Need(main.ClientSize==(fourK?new Size(3760,2060):new Size(1840,980)),"Requested synthetic client geometry was constrained by host: "+main.ClientSize);
                        Console.WriteLine("ENV client="+main.ClientSize+" native_dpi="+main.DeviceDpi+" awareness="+HighDpi.Awareness+" reduced="+Motion.Reduced+" baseline="+baseline);
                        FontMetrics(main);
                        var nav=typeof(MainWindow).GetMethod("NavigateTo",Private);
                        string[] pages={"网络诊断","游戏记录","设置","概览"};
                        var first=new Dictionary<string,Form>();
                        var warm=new Dictionary<string,List<double>>();foreach(string p in pages)warm[p]=new List<double>();
                        int gdiBefore=GetGuiResources(Process.GetCurrentProcess().Handle,0);
                        int gdiWarm=0;long memoryWarm=0;
                        for(int i=0;i<108;i++) {
                            string page=pages[i%4];var clock=Stopwatch.StartNew();nav.Invoke(main,new object[]{page});double request=clock.Elapsed.TotalMilliseconds;
                            Form current=Current(main); bool reused=page=="概览"||first.ContainsKey(page)&&ReferenceEquals(first[page],current);
                            if(!first.ContainsKey(page))first[page]=current;
                            clock.Restart();using(var bitmap=new Bitmap(main.Width,main.Height)){main.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));if(i<4&&args.Length>0)bitmap.Save(Path.Combine(args[0],"navigation-"+i+".png"));}double render=clock.Elapsed.TotalMilliseconds;
                            if(i<8){Console.WriteLine("NAV page="+page+" visit="+(i/4+1)+" request_ms="+request.ToString("F3")+" draw_to_bitmap_ms="+render.ToString("F3")+" reused="+reused);if(!baseline){var manager=Field(main,"pages");Console.WriteLine(manager.GetType().GetProperty("LastMeasurement",Private).GetValue(manager,null));}}
                            if(i>=8)warm[page].Add(request);
                            if(i>=4&&!baseline)Need(reused,"Revisited page was reconstructed");
                            Pump(i<8?60:2);
                            if(i==7){GC.Collect();GC.WaitForPendingFinalizers();gdiWarm=GetGuiResources(Process.GetCurrentProcess().Handle,0);memoryWarm=Process.GetCurrentProcess().PrivateMemorySize64;}
                            if(i==1&&!baseline)
                            {
                                var load = Stopwatch.StartNew();
                                var history = (HistoryWindow)current;
                                while (history.LastDataLoadMilliseconds == 0 && load.ElapsedMilliseconds < 3000) Pump(20);
                                Need(history.LastDataLoadMilliseconds > 0, "History did not finish loading before the cached navigation benchmark");
                                Console.WriteLine("DATA pipeline_elapsed_ms="+history.LastDataLoadMilliseconds+" sqlite_worker_ms="+history.LastQueryMilliseconds+" report_worker_ms="+history.LastAnalysisMilliseconds);
                                CaptureCurrent(main,args[0],"history-loaded.png");
                                Need(((Control)Field(current,"overviewText")).Height>=UiStyle.Scale(current,80),"Report body collapsed at constrained height");
                            }
                            if(i==3&&!baseline)
                            {
                                var log = (TextBox)Field(main, "logBox");
                                var timeline = (Control)Field(main, "timeline");
                                var logToggle = (Button)Field(main, "logToggle");
                                var flowToggle = (Button)Field(main, "flowToggle");
                                Need(!log.Visible && !timeline.Visible, "Overview secondary sections must start collapsed");
                                flowToggle.PerformClick();
                                Need(timeline.Visible && timeline.Height >= UiStyle.Scale(timeline, 280), "Flow disclosure did not show its content");
                                flowToggle.PerformClick();
                                logToggle.PerformClick();
                                Need(log.Visible && log.Height >= UiStyle.Scale(log, 80) && log.TextLength > 0, "Log disclosure did not show its content");
                                CaptureCurrent(main,args[0],"overview-log.png");
                                logToggle.PerformClick();
                                Need(!log.Visible && !timeline.Visible, "Overview sections did not collapse");
                            }
                            if(i>=8&&(i-7)%25==0){GC.Collect();GC.WaitForPendingFinalizers();Console.WriteLine("CHECKPOINT switches="+(i-7)+" GDI="+GetGuiResources(Process.GetCurrentProcess().Handle,0)+" private_MB="+(Process.GetCurrentProcess().PrivateMemorySize64/1048576.0).ToString("F1"));}
                        }
                        foreach(var item in warm){item.Value.Sort();double sum=0;foreach(double value in item.Value)sum+=value;Console.WriteLine("WARM100 page="+item.Key+" n="+item.Value.Count+" mean_ms="+(sum/item.Value.Count).ToString("F3")+" p95_ms="+item.Value[(int)Math.Ceiling(item.Value.Count*0.95)-1].ToString("F3"));}
                        main.Hide();main.ShowMainWindow();nav.Invoke(main,new object[]{"游戏记录"});Pump(20);Need(baseline||ReferenceEquals(first["游戏记录"],Current(main)),"Restore navigation reconstructed history");
                        if (!baseline)
                        {
                            DragAcceptance(main);
                        }
                        GC.Collect();GC.WaitForPendingFinalizers();int finalGdi=GetGuiResources(Process.GetCurrentProcess().Handle,0);Console.WriteLine("RESOURCE gdi_before="+gdiBefore+" warmed="+gdiWarm+" after="+finalGdi+" working_set_MB="+(Process.GetCurrentProcess().WorkingSet64/1048576.0).ToString("F1")+" private_warmed_MB="+(memoryWarm/1048576.0).ToString("F1")+" private_final_MB="+(Process.GetCurrentProcess().PrivateMemorySize64/1048576.0).ToString("F1"));
                        if(!baseline)Need(finalGdi<=gdiWarm+5,"GDI growth after warmed 100 switches");
                        if (!baseline && Environment.GetEnvironmentVariable("STEAMDIRECT_TEST_CROSS_MONITOR") == "1")
                            MonitorDragAcceptance(main);
                        Need(monitor.PingCount==0&&monitor.DnsCount==0,"Network probes unexpectedly started");
                    }
                }
                Console.WriteLine("PASS offscreen navigation harness; desktop compositor/full visible frame: BLOCKED");return 0;
            }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}finally{Motion.IsGaming=null;try{Directory.Delete(temp,true);}catch(IOException){}}
        }

        private static void DragAcceptance(MainWindow main)
        {
            int layouts = 0;
            Control page = Current(main);
            var host = (PageHost)Field(main, "pageHost");
            Rectangle original = page.Bounds;
            Size size = main.ClientSize;
            var clock = Stopwatch.StartNew();
            LayoutEventHandler count = (sender, args) => layouts++;
            main.Layout += count;
            SendMessage(main.Handle, 0x0231, IntPtr.Zero, IntPtr.Zero); // WM_ENTERSIZEMOVE
            try
            {
                for (int step = 1; step <= 10; step++)
                {
                    OffscreenGeometry.Resize(main, new Size(size.Width - step * 6, size.Height - step * 5));
                    Pump(42);
                    Need(page.Bounds == original, "Active history page reflowed during corner drag");
                }
                Need(layouts == 0, "Full main-window layout ran during corner drag");
            }
            finally { SendMessage(main.Handle, 0x0232, IntPtr.Zero, IntPtr.Zero); } // WM_EXITSIZEMOVE
            Need(page.Bounds == host.PageBounds && page.Bounds != original, "Active page did not fit the released window");
            Console.WriteLine("CORNER drag_steps=10 layouts_during=0 layouts_on_release=" + layouts + " elapsed_ms=" + clock.ElapsedMilliseconds);
            layouts = 0;
            var origin = main.Location;
            SendMessage(main.Handle, 0x0231, IntPtr.Zero, IntPtr.Zero);
            try
            {
                for (int step = 1; step <= 5; step++) { main.Location = new Point(origin.X + step * 8, origin.Y); Pump(10); }
            }
            finally { SendMessage(main.Handle, 0x0232, IntPtr.Zero, IntPtr.Zero); main.Layout -= count; }
            Need(layouts == 0, "Move-only gesture triggered full layout");
            Console.WriteLine("MOVE drag_steps=5 layouts=0 same_dpi=true");
        }

        private static void MonitorDragAcceptance(MainWindow main)
        {
            var screens = Screen.AllScreens;
            if (screens.Length < 2) { Console.WriteLine("CROSS_MONITOR BLOCKED: only one screen"); return; }
            int cloak = 1, state;
            if (DwmSetWindowAttribute(main.Handle, 13, ref cloak, 4) != 0 ||
                DwmGetWindowAttribute(main.Handle, 14, out state, 4) != 0 || state == 0)
            {
                Console.WriteLine("CROSS_MONITOR BLOCKED: could not cloak the test window");
                return;
            }
            var origin = main.Location;
            int layouts = 0;
            LayoutEventHandler count = (sender, args) => layouts++;
            bool dragging = false;
            try
            {
                OffscreenGeometry.Resize(main, new Size(850, 620));
                main.Location = new Point(screens[0].WorkingArea.Left + 20, screens[0].WorkingArea.Top + 20);
                Pump(25);
                main.Layout += count;
                foreach (int destination in new[] { 1, 0 })
                {
                    layouts = 0;
                    uint before = GetDpiForWindow(main.Handle);
                    SendMessage(main.Handle, 0x0231, IntPtr.Zero, IntPtr.Zero);
                    dragging = true;
                    main.Location = new Point(screens[destination].WorkingArea.Left + 20, screens[destination].WorkingArea.Top + 20);
                    Pump(80);
                    uint after = GetDpiForWindow(main.Handle);
                    int during = layouts;
                    SendMessage(main.Handle, 0x0232, IntPtr.Zero, IntPtr.Zero);
                    dragging = false;
                    Pump(25);
                    var host = (PageHost)Field(main, "pageHost");
                    Need(during == 0 && Current(main).Bounds == host.PageBounds, "Cross-monitor drag must defer full layout and fit the active page on release");
                    Console.WriteLine("CROSS_MONITOR cloaked=true native_dpi=" + before + "->" + after +
                        " managed_dpi=" + main.DeviceDpi + " layouts_during=" + during + " layouts_on_release=" + (layouts - during));
                    if (before != after) Need(main.DeviceDpi == after, "WinForms did not adopt the destination monitor DPI");
                }
            }
            finally
            {
                if (dragging) SendMessage(main.Handle, 0x0232, IntPtr.Zero, IntPtr.Zero);
                main.Layout -= count;
                main.Location = origin;
            }
        }
        private static void CaptureCurrent(Form main,string directory,string name){using(var bitmap=new Bitmap(main.Width,main.Height)){main.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(directory,name));}}
    }
}
