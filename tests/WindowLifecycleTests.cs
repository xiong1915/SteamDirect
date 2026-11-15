using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
namespace SteamDirect
{
    internal static class WindowLifecycleTests
    {
        private static int count;
        private static void Check(bool value,string message){if(!value)throw new Exception(message);count++;}
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd,int message,IntPtr a,IntPtr b);
        internal static int Run()
        {
            WindowEnvironment.OffscreenTests=true; Motion.Reduced=true;
            Check(WindowEnvironment.Fit(new Rectangle(1800,100,1100,900),new Rectangle(0,0,1920,1040))==new Rectangle(820,100,1100,900),"Moved monitor bounds must fit work area");
            Check(WindowEnvironment.Fit(new Rectangle(-2000,-50,2500,1600),new Rectangle(-1920,0,1920,1040))==new Rectangle(-1920,0,1920,1040),"Negative origin and reduced work area");
            var detached = new ListViewItem("正在移除的记录") { Tag = new HistorySession() }; detached.SubItems.AddRange(new[]{"2026-11-15 12:00:00","游玩 25 小时 10 分 15 秒","直连"});
            var heights = (int[])typeof(HistoryWindow).GetMethod("RecordTextHeights",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{detached,200});
            Check(heights.Length==3&&heights[2]>0,"Detached record during refresh must not cause null reference");
            using(var form=new DesktopForm{StartPosition=FormStartPosition.Manual,Location=new Point(-24000,-24000),ShowInTaskbar=false,ClientSize=new Size(600,400)})
            using(var text=new RichTextBox{Multiline=true,Text=new string('x',5)})
            using(var frame=new NativeScrollFrame(text))
            {
                form.Controls.Add(frame);form.Show();Application.DoEvents();
                Check(UiStyle.Scale(text,96)==form.DeviceDpi,"Embedded manual dimensions must use root DPI");
                WindowEnvironment.LayoutDpi=192;Check(UiStyle.Scale(text,40)==80,"DPI parameter simulation");WindowEnvironment.LayoutDpi=0;
                text.Text=string.Join(Environment.NewLine,new string[200]).Replace(Environment.NewLine,"测试行"+Environment.NewLine);Application.DoEvents();
                typeof(NativeScrollFrame).GetMethod("ScrollContent",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(frame,new object[]{50});
                Check(SendMessage(text.Handle,0xCE,IntPtr.Zero,IntPtr.Zero).ToInt32()>0,"Custom scrollbar scrolls native RichEdit");
                Check(text.ScrollBars==RichTextBoxScrollBars.None,"Native white scrollbar must be disabled");
                text.SelectAll();Check(text.SelectedText.Contains("测试行"),"Native text selection retained");
                IntPtr handle=form.Handle;for(int n=0;n<20;n++){form.Hide();form.Show();Check(form.Handle==handle,"Visibility must reuse HWND");}
                using(var diagnostics=new WindowDiagnostics(form)){
                    diagnostics.Begin();diagnostics.Complete();Check(diagnostics.LastResult==null,"Disabled diagnostics performs no observation");
                    diagnostics.Enabled=true;diagnostics.Begin();form.Invalidate();using(var pixel=new Bitmap(1,1)) form.DrawToBitmap(pixel,new Rectangle(0,0,1,1));diagnostics.Displayed();diagnostics.Complete();Check(diagnostics.LastResult.Contains("layouts="),"Opt-in aggregate diagnostic output");
                }
            }
            WindowEnvironment.LayoutDpi=0;Motion.Reduced=false;return count;
        }
    }
}
