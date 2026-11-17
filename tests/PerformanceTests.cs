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
                using(var text=new GlassRichTextBox{Text="network history",ReadOnly=true}){
                    text.CreateControl();text.Select(2,4);Need(text.SelectedText=="twor","Glass report broke native selection");
                }
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
                        OffscreenGeometry.Resize(form,new Size(3760,2060));Need(form.ClientSize==new Size(3760,2060),"4K fixture was constrained to the physical desktop");
                    }
                }
                return count;
            }finally{Directory.Delete(folder,true);}
        }
    }
}
