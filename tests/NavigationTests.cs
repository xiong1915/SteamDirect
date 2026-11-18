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
    internal static class NavigationTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int assertions;
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
        private static void Need(bool value,string message){assertions++;if(!value)throw new Exception(message);}
        private static object Field(object obj,string name){return obj.GetType().GetField(name,Private).GetValue(obj);}
        private static void Wait(Func<bool> ready){var timer=Stopwatch.StartNew();while(true){Application.DoEvents();if(ready())return;if(timer.ElapsedMilliseconds>10000)throw new TimeoutException("Async navigation fixture");Thread.Sleep(1);}}
        private static Task Query(HistoryWindow form){return (Task)typeof(HistoryWindow).GetMethod("RefreshAsync",Private).Invoke(form,new object[]{0});}
        private sealed class TestPage : DesktopForm,IManagedPage
        {
            internal int Enters,Leaves;internal bool FailEnter;internal readonly TextBox Input=new TextBox{Dock=DockStyle.Fill};
            internal TestPage(){Controls.Add(Input);}
            public void EnterPage(){Enters++;if(FailEnter)throw new InvalidOperationException("Injected post-handle preparation failure");}public void LeavePage(){Leaves++;}
        }
        internal static int Run()
        {
            assertions=0;WindowEnvironment.OffscreenTests=true;
            using(var window=new DesktopForm{ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-24000,-24000),ClientSize=new Size(1000,700)})
            using(var host=new PageHost())using(var overview=new Panel{Dock=DockStyle.Fill})
            {
                window.Controls.Add(host);host.Controls.Add(overview);window.Show();
                using(var pages=new PageManager(host,overview))
                {
                    int constructions=0;TestPage first=null;
                    pages.Navigate("one",()=>{constructions++;return first=new TestPage();});first.Input.Text="preserved";IntPtr handle=first.Handle;
                    pages.Navigate("two",()=>new TestPage());Need(!first.IsDisposed&&!first.Visible,"Hidden page remains alive");
                    pages.Navigate("one",()=>{throw new Exception("Must reuse page");});
                    Need(first.Handle==handle&&first.Input.Text=="preserved"&&first.Enters==2,"Page identity HWND and state retained");
                    Need(first.Leaves==1&&constructions==1&&pages.CachedCount==2,"Lifecycle and lazy construction");
                    Need(!pages.Navigate("one",()=>null)&&first.Enters==2,"Repeated selected navigation is no-op");
                    for(int i=0;i<100;i++)pages.Navigate(i%2==0?"two":"one",()=>null);
                    Need(first.Handle==handle&&pages.CachedCount==2,"100 switches do not rebuild handles");
                    try{pages.Navigate("failure",()=>{throw new InvalidOperationException("injected");});}catch(InvalidOperationException){}
                    Need(first.Visible&&host.Visible,"Failed preparation retains old page and host visibility");
                    TestPage failure=new TestPage{FailEnter=true};try{pages.Navigate("late-failure",()=>failure);}catch(InvalidOperationException){}
                    Need(failure.IsDisposed&&first.Visible&&pages.CachedCount==2,"Post-handle failure disposes prepared page and preserves old page");
                    pages.Navigate("概览",()=>null);Need(overview.Visible&&!first.Visible,"Overview is reused");
                    using(new RedrawBatch(first)){first.Input.Text="hidden update";}Need(!IsWindowVisible(first.Handle),"Updating cached hidden page must not show its HWND");
                    try{using(new RedrawBatch(host)){throw new InvalidOperationException("injected drawing failure");}}catch(InvalidOperationException){}
                    Need(IsWindowVisible(host.Handle),"Drawing failure restores native host visibility");
                }
            }
            string temporary=Path.Combine(Path.GetTempPath(),"SteamDirect-nav-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temporary);
            try{
                var store=new HistoryStore(Path.Combine(temporary,"fixture.sqlite3"));store.Initialize();long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                store.WriteBatch(new[]{new HistorySession{Id="first",Game="Alpha",Mode="直连",Start=now-2000,End=now},new HistorySession{Id="second",Game="Beta",Mode="代理",Start=now-3000,End=now}},null,null);
                using(var history=new HistoryWindow(store)){history.StartPosition=FormStartPosition.Manual;history.Location=new Point(-24000,-24000);history.ShowInTaskbar=false;history.Show();Wait(()=>!(bool)Field(history,"loadingRecords"));
                    var records=(ListView)Field(history,"records");Need(records.Items.Count==2,"Initial asynchronous history load");
                    var report=(RichTextBox)Field(history,"overviewText");Control viewport=report.Parent.Parent.Parent.Parent;
                    history.MinimumSize=Size.Empty;history.ClientSize=new Size(1300,700);history.PerformLayout();
                    Need(viewport is ContentViewport&&report.Height>0,"Constrained report retains narrative area");
                    double loaded=history.LastDataLoadMilliseconds;history.LeavePage();history.EnterPage();Need(history.LastDataLoadMilliseconds==loaded&&!(bool)Field(history,"loadingRecords"),"Valid history reused without query");
                    var filter=(TextBox)Field(history,"gameFilter");filter.Text="Alpha";Task stale=Query(history);filter.Text="Beta";Task latest=Query(history);Wait(()=>stale.IsCompleted&&latest.IsCompleted);Need(records.Items.Count==1&&records.Items[0].Text=="Beta","Old query cannot overwrite newer filter");
                    filter.Text="Alpha";Task hidden=Query(history);history.LeavePage();Wait(()=>hidden.IsCompleted);Need(records.Items[0].Text=="Beta","Cancelled hidden query cannot replace visible data");
                    filter.Clear();history.EnterPage();Wait(()=>!(bool)Field(history,"loadingRecords"));Need(records.Items.Count==2,"Reenter after cancellation refreshes");
                    history.LeavePage();store.WriteBatch(new[]{new HistorySession{Id="third",Game="Gamma",Mode="直连",Start=now-4000,End=now}},null,null);history.EnterPage();Wait(()=>!(bool)Field(history,"loadingRecords"));Need(records.Items.Count==3,"Store revision invalidates cached history");
                    Task disposing=Query(history);history.Dispose();Wait(()=>disposing.IsCompleted);Need(disposing.Status==TaskStatus.RanToCompletion,"Disposal cancels query without updating dead controls");
                }
                using(var settings=new SettingsWindow(true,false,7)){
                    var preferences=new UserPreferences{ThemeColor="#FF0000",NightMode=true,TrayEnabled=false,RetentionDays=30,ReduceMotion=true,WindowDiagnostics=true,LogDirectory=temporary};settings.Synchronize=()=>settings.Reload(preferences,true);
                    settings.EnterPage();Need(settings.ThemeColor=="#FF0000"&&settings.NightMode&&!settings.TrayEnabled&&settings.RetentionDays==30&&settings.AutoStart&&settings.DiagnosticsEnabled,"Cached settings reload current configuration");
                }
                var olderStore = new HistoryStore(Path.Combine(temporary, "older.sqlite3"));
                olderStore.Initialize();
                long yesterday = new DateTimeOffset(DateTime.Today.AddDays(-1).AddHours(12)).ToUnixTimeMilliseconds();
                var last = new HistorySession { Id = "last", Game = "Fixture", Mode = "直连", Target = "192.0.2.1", Start = yesterday, End = yesterday + 60000 };
                var prior = new HistorySession { Id = "prior", Game = "Fixture", Mode = "代理", Target = last.Target, Start = yesterday - 120000, End = yesterday - 60000 };
                olderStore.WriteBatch(new[] { last, prior }, new[] {
                    new HistorySample { SessionId = last.Id, At = yesterday + 5000, Mode = "直连", ExternalMs = 25, GatewayMs = 2, AdapterUp = true },
                    new HistorySample { SessionId = prior.Id, At = prior.Start + 5000, Mode = "代理", ExternalMs = 40, GatewayMs = 3, AdapterUp = true }
                }, new[] { new HistoryEvent { Id = "incident", SessionId = last.Id, Start = yesterday + 5000, End = yesterday + 10000, Kind = "外部探测异常" } });
                using (var history = new HistoryWindow(olderStore) { StartPosition = FormStartPosition.Manual, Location = new Point(-24000, -24000), ShowInTaskbar = false })
                {
                    history.Show();
                    var rows = (ListView)Field(history, "records");
                    var scope = (ComboBox)Field(history, "recordScope");
                    Wait(() => rows.Items.Count == 2 && !(bool)Field(history, "loadingRecords"));
                    Need(scope.SelectedIndex == 1 && ((Label)Field(history, "status")).Text.Contains("已显示最近历史"), "Empty today opens existing history once");
                    Need(((RichTextBox)Field(history, "overviewText")).Text.Length > 0, "Overview loaded");
                    Need(((Control)Field(history, "networkChart")).AccessibleDescription.Contains("1 个采样点"), "Chart loaded");
                    Need(((ListView)Field(history, "incidents")).Items.Count == 1, "Timeline loaded");
                    string comparison = ((RichTextBox)Field(history, "comparisonText")).Text;
                    Need(comparison.Contains("直连 ·") && comparison.Contains("代理 ·") && comparison.Contains("样本不足"), "History shows both modes and insufficient-data status");
                    scope.SelectedIndex = 0;
                    Wait(() => rows.Items.Count == 0 && !(bool)Field(history, "loadingRecords"));
                    Need(scope.SelectedIndex == 0 && ((Label)Field(history, "incidentSummary")).Text.Contains("今天") &&
                        ((RichTextBox)Field(history, "comparisonText")).Text.Contains("今天"), "Explicit today selection remains in place and explains missing data");
                }
                int calls=0;using(var gate=new ManualResetEventSlim())using(var started=new ManualResetEventSlim())
                using(var network=new NetworkWindow(_=>{},(server,update,log,token)=>{Interlocked.Increment(ref calls);started.Set();gate.Wait(token);return new NetworkEvidence();})){
                    network.StartPosition=FormStartPosition.Manual;network.Location=new Point(-24000,-24000);network.ShowInTaskbar=false;network.Show();
                    var method=typeof(NetworkWindow).GetMethod("RunAsync",Private);Task run=(Task)method.Invoke(network,null);Wait(()=>started.IsSet);network.LeavePage();network.EnterPage();Task duplicate=(Task)method.Invoke(network,null);
                    Need(calls==1&&duplicate.IsCompleted&&!run.IsCompleted,"Navigation preserves one bounded diagnostic without duplication");
                    network.Dispose();Wait(()=>run.IsCompleted);Need(run.Status==TaskStatus.RanToCompletion,"Disposal cancels diagnostic safely");
                }
            }finally{Directory.Delete(temporary,true);}
            return assertions;
        }
    }
}
