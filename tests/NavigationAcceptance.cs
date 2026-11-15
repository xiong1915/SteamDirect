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
            string temp=Path.Combine(Path.GetTempPath(),"SteamDirect-navigation-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp); LogStore.DirectoryPath=Path.Combine(temp,"Logs");
            try {
                bool baseline=args.Length>1&&args[1]=="baseline",fourK=args.Length>2&&args[2]=="4k";
                using(var monitor=new HistoryMonitor(Path.Combine(temp,"history.sqlite3"),()=>new List<SteamGameProcess>())) {
                    monitor.Store.Initialize(); long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    monitor.Store.WriteBatch(new[]{new HistorySession{Id="nav",Game="Navigation fixture",Mode="直连",Target="192.0.2.1",Start=now-600000,End=now}},null,null);
                    using(var main=new MainWindow(false,monitor)) {
                        main.ShowInTaskbar=false;main.StartPosition=FormStartPosition.Manual;main.Location=new Point(-24000,-24000);main.MinimumSize=Size.Empty;main.ClientSize=fourK?new Size(3760,2060):new Size(1840,980);main.Show();Pump(20);
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
                            if(i==1&&!baseline){Console.WriteLine("DATA pipeline_elapsed_ms="+typeof(HistoryWindow).GetProperty("LastDataLoadMilliseconds",Private).GetValue(current,null)+" sqlite_worker_ms="+typeof(HistoryWindow).GetProperty("LastQueryMilliseconds",Private).GetValue(current,null)+" report_worker_ms="+typeof(HistoryWindow).GetProperty("LastAnalysisMilliseconds",Private).GetValue(current,null));CaptureCurrent(main,args[0],"history-loaded.png");Need(((Control)Field(current,"overviewText")).Height>=UiStyle.Scale(current,80),"Report body collapsed at constrained height");}
                            if(i==3&&!baseline){var log=(TextBox)Field(main,"logBox");Need(log.Height>=UiStyle.Scale(log,80),"Overview log collapsed at constrained height");var viewport=Field(Field(main,"pages"),"current") as ContentViewport;if(viewport!=null){viewport.ScrollTo(int.MaxValue);CaptureCurrent(main,args[0],"overview-log.png");viewport.ScrollTo(0);}}
                            if(i>=8&&(i-7)%25==0){GC.Collect();GC.WaitForPendingFinalizers();Console.WriteLine("CHECKPOINT switches="+(i-7)+" GDI="+GetGuiResources(Process.GetCurrentProcess().Handle,0)+" private_MB="+(Process.GetCurrentProcess().PrivateMemorySize64/1048576.0).ToString("F1"));}
                        }
                        foreach(var item in warm){item.Value.Sort();double sum=0;foreach(double value in item.Value)sum+=value;Console.WriteLine("WARM100 page="+item.Key+" n="+item.Value.Count+" mean_ms="+(sum/item.Value.Count).ToString("F3")+" p95_ms="+item.Value[(int)Math.Ceiling(item.Value.Count*0.95)-1].ToString("F3"));}
                        main.Hide();main.ShowMainWindow();nav.Invoke(main,new object[]{"游戏记录"});Pump(20);Need(baseline||ReferenceEquals(first["游戏记录"],Current(main)),"Restore navigation reconstructed history");
                        GC.Collect();GC.WaitForPendingFinalizers();int finalGdi=GetGuiResources(Process.GetCurrentProcess().Handle,0);Console.WriteLine("RESOURCE gdi_before="+gdiBefore+" warmed="+gdiWarm+" after="+finalGdi+" working_set_MB="+(Process.GetCurrentProcess().WorkingSet64/1048576.0).ToString("F1")+" private_warmed_MB="+(memoryWarm/1048576.0).ToString("F1")+" private_final_MB="+(Process.GetCurrentProcess().PrivateMemorySize64/1048576.0).ToString("F1"));
                        if(!baseline)Need(finalGdi<=gdiWarm+5,"GDI growth after warmed 100 switches");
                        Need(monitor.PingCount==0&&monitor.DnsCount==0,"Network probes unexpectedly started");
                    }
                }
                Console.WriteLine("PASS offscreen navigation harness; desktop compositor/full visible frame: BLOCKED");return 0;
            }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}finally{Motion.IsGaming=null;try{Directory.Delete(temp,true);}catch(IOException){}}
        }
        private static void CaptureCurrent(Form main,string directory,string name){using(var bitmap=new Bitmap(main.Width,main.Height)){main.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(directory,name));}}
    }
}
