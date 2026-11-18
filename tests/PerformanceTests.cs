using System;
using System.Collections.Generic;
using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
namespace SteamDirect
{
    internal static class PerformanceTests
    {
        private static int count;
        private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
        private static void Need(bool value,string message){count++;if(!value)throw new Exception(message);}
        private sealed class ResizeForm:DesktopForm{
            internal void BeginDrag(){OnResizeBegin(EventArgs.Empty);}
            internal void EndDrag(){OnResizeEnd(EventArgs.Empty);}
        }
        internal static int Run()
        {
            count=0;string folder=Path.Combine(Path.GetTempPath(),"SteamDirect-perf-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
            try{
                string settings=Path.Combine(folder,"settings.ini");
                UserPreferences.Save(settings,true,30,true,"#649BFF",folder,true,false,false);
                Need(!UserPreferences.Load(settings).NetworkRecording,"Disabled recorder setting not persisted");
                File.WriteAllText(settings,"NetworkRecording=broken");Need(UserPreferences.Load(settings).NetworkRecording,"Older settings must preserve recording behavior");
                using(var view=new SettingsWindow(true,false,30,networkRecording:false)){
                    Need(!view.RecordingEnabled,"Recording switch did not reflect disabled state");
                    view.SetSaving(true);Need(!view.Enabled,"Settings editable during asynchronous recorder transition");
                    view.SetSaving(false);Need(view.Enabled,"Settings did not recover after save");
                }
                Need(UiStyle.CardRadius==UiStyle.ControlRadius&&UiStyle.ControlRadius==UiStyle.NavigationRadius,"Inconsistent component radii");
                using (var list = new HistoryRecordList())
                {
                    var item = list.Items.Add("Synthetic");
                    int measurements = 0;
                    Func<ListViewItem, int, int[]> measure = (row, width) => { measurements++; return new[] { width }; };
                    Need(list.RowLayoutChanged(300), "Initial row layout must run");
                    list.MeasureRow(item, 300, measure);
                    list.Height += 40;
                    Need(!list.RowLayoutChanged(300), "Height-only resize repeated history row layout");
                    list.MeasureRow(item, 300, measure);
                    Need(measurements == 1, "Repaint remeasured the same history row");
                    Need(list.RowLayoutChanged(250) && list.MeasureRow(item, 250, measure)[0] == 250 && measurements == 2,
                        "Width change did not reflow history text");
                }
                Need(SteamGameDiscovery.IsCounterStrike("CS2.EXE")&&SteamGameDiscovery.IsCounterStrike("csgo.exe")&&!SteamGameDiscovery.IsCounterStrike("cs2-helper.exe"),"Counter-Strike guard must not rely only on Steam's registry flag");
                int scans=0;string path=Path.Combine(folder,"history.sqlite3");
                using(var disabled=new HistoryMonitor(path,()=>{Interlocked.Increment(ref scans);return new List<SteamGameProcess>();})){
                    Need(scans==0&&!File.Exists(path),"Disabled startup created worker or database");disabled.Dispose();
                    using(var enabled=disabled.CreateRestart()){
                        Need(ReferenceEquals(enabled.Store,disabled.Store),"Restart replaced history cache revision owner");
                        enabled.Start();var clock=Stopwatch.StartNew();while(Volatile.Read(ref scans)==0&&clock.ElapsedMilliseconds<5000)Thread.Sleep(2);
                        Need(scans>0,"Re-enabled recorder did not start");enabled.Dispose();enabled.Completion.GetAwaiter().GetResult();
                        int stopped=scans;Thread.Sleep(80);Need(scans==stopped&&enabled.PingCount==0&&enabled.DnsCount==0,"Stopped recorder still scanned or probed");
                        using(var resumed=enabled.CreateRestart()){
                            long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            typeof(HistoryMonitor).GetMethod("UpdateGames",Private).Invoke(resumed,new object[]{new List<SteamGameProcess>{new SteamGameProcess{Key="fixture",Name="fixture",StartedAt=now-60000}},now});
                            var pending=(IDictionary)typeof(HistoryMonitor).GetField("pendingSessions",Private).GetValue(resumed);
                            foreach(HistorySession session in pending.Values)Need(session.Start>=now-1000,"Disabled gap incorrectly counted as recorded game time");
                        }
                    }
                }
                using(var form=new ResizeForm{StartPosition=FormStartPosition.Manual,Location=new Point(-24000,-24000),ShowInTaskbar=false,ClientSize=new Size(1000,700)})
                using(var host=new PageHost())using(var overview=new Panel()){
                    form.Controls.Add(host);host.Controls.Add(overview);form.Show();Application.DoEvents();
                    using(var manager=new PageManager(host,overview)){
                        var a=new DesktopForm();var b=new DesktopForm();manager.Navigate("a",()=>a);manager.Navigate("b",()=>b);
                        int hiddenLayouts=0;a.Layout+=(s,e)=>hiddenLayouts++;Rectangle old=a.Bounds;
                        for(int i=0;i<50;i++){host.Size=new Size(700+i,450+i);host.PerformLayout();}
                        Need(a.Bounds==old&&hiddenLayouts==0,"Resizing laid out hidden cached page");
                        Need(b.Bounds==host.DisplayRectangle,"Active page failed to follow host bounds");
                        int layouts=0;form.Layout+=(s,e)=>layouts++;form.BeginDrag();
                        for(int i=0;i<50;i++)form.ClientSize=new Size(900+i,620+i);
                        int beforeEnd=layouts;form.EndDrag();
                        Console.WriteLine("RESIZE burst=50 layouts_during="+beforeEnd+" total="+layouts);
                        Need(beforeEnd<10,"Live resize did not coalesce layout work");
                        Need(host.Bounds==form.ClientRectangle,"Resize end did not flush final layout");
                        var timer=(System.Windows.Forms.Timer)typeof(DesktopForm).GetField("resizeTimer",Private).GetValue(form);Need(!timer.Enabled,"Resize timer remained active after drag");
                        OffscreenGeometry.Resize(form,new Size(3760,2060));Application.DoEvents();Need(form.ClientSize==new Size(3760,2060),"4K fixture was constrained to the physical desktop: "+form.ClientSize);
                    }
                }
                using (var form = new ResizeForm { StartPosition = FormStartPosition.Manual, Location = new Point(-24000, -24000), ShowInTaskbar = false })
                using (var chart = new HistoryNetworkChart { Dock = DockStyle.None, Size = new Size(400, 320) })
                using (var image = new Bitmap(560, 420))
                {
                    form.Controls.Add(chart);
                    form.Show();
                    var samples = new List<HistorySample>();
                    for (int index = 0; index < 1800; index++)
                        samples.Add(new HistorySample { At = 1000 + index * 5000L, ExternalMs = 30 + index % 15, GatewayMs = 2, Mode = "直连" });
                    chart.SetData(new HistorySession { Start = 1000, End = 9001000 }, samples, null);
                    chart.DrawToBitmap(image, chart.ClientRectangle);
                    var cache = typeof(HistoryNetworkChart).GetField("imageCache", Private);
                    var clock = Stopwatch.StartNew();
                    int rerasterized = 0;
                    for (int index = 1; index <= 12; index++)
                    {
                        var previous = cache.GetValue(chart);
                        chart.Size = new Size(400 + index * 8, 320 + index * 3);
                        chart.DrawToBitmap(image, chart.ClientRectangle);
                        if (!ReferenceEquals(previous, cache.GetValue(chart))) rerasterized++;
                    }
                    double fullPaint = clock.Elapsed.TotalMilliseconds;
                    chart.Size = new Size(400, 320);
                    chart.DrawToBitmap(image, chart.ClientRectangle);
                    var initial = cache.GetValue(chart);
                    form.BeginDrag();
                    clock.Restart();
                    for (int index = 1; index <= 12; index++)
                    {
                        chart.Size = new Size(400 + index * 8, 320 + index * 3);
                        chart.DrawToBitmap(image, chart.ClientRectangle);
                    }
                    Console.WriteLine("CHART resize_frames=12 normal_rebuilds=" + rerasterized +
                        " normal_ms=" + fullPaint.ToString("F1") + " live_ms=" + clock.Elapsed.TotalMilliseconds.ToString("F1"));
                    Need(initial != null && ReferenceEquals(initial, cache.GetValue(chart)), "Live resize rerasterized the history chart");
                    form.EndDrag();
                    chart.DrawToBitmap(image, chart.ClientRectangle);
                    var resized = (Bitmap)cache.GetValue(chart);
                    Need(!ReferenceEquals(initial, resized) && resized.Size == chart.ClientSize, "Chart did not restore pixel-accurate rendering at the final size");
                }
                return count;
            }finally{Directory.Delete(folder,true);}
        }
    }
}
