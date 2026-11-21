using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
namespace SteamDirect
{
    internal static class ScrollRegressionTests
    {
        [DllImport("user32.dll")]private static extern IntPtr SendMessage(IntPtr h,int m,IntPtr w,IntPtr l);
        [DllImport("user32.dll",EntryPoint="GetWindowLongW")]private static extern int Style(IntPtr h,int i);
        private sealed class Probe:NativeWindow,IDisposable{
            internal int Styles,Paints;internal Probe(IntPtr h){AssignHandle(h);}
            protected override void WndProc(ref Message m){if(m.Msg==0x7D)Styles++;if(m.Msg==0xF)Paints++;base.WndProc(ref m);}
            public void Dispose(){ReleaseHandle();}
        }
        internal static int Run()
        {
            using(var form=new DesktopForm{ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-24000,-24000),ClientSize=new Size(420,600)})
            using(var list=new HistoryRecordList{View=View.Details,HeaderStyle=ColumnHeaderStyle.None})
            using(var frame=new NativeScrollFrame(list)){
                list.Columns.Add("记录",300);list.SetRowHeight(120);for(int i=0;i<12;i++)list.Items.Add("第 "+i+" 场对局");
                form.Controls.Add(frame);form.Show();Application.DoEvents();
                using(var probe=new Probe(list.Handle)){
                    for(int i=0;i<20;i++){SendMessage(list.Handle,0x1014,IntPtr.Zero,new IntPtr(i%2==0?120:-120));list.Invalidate();list.Update();}
                    Console.WriteLine("SCROLL repeated_paint_style_changes="+probe.Styles+" paints="+probe.Paints+" native_vscroll="+((Style(list.Handle,-16)&0x200000)!=0));
                    var bar=(Control)typeof(NativeScrollFrame).GetField("bar",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(frame);
                    if(bar.Visible)throw new Exception("ListView must not have a competing custom scrollbar");
                    if(probe.Styles!=0)throw new Exception("Paint must not mutate ListView scrollbar styles");
                    if((SendMessage(list.Handle,0x1037,IntPtr.Zero,IntPtr.Zero).ToInt32()&0x10000)==0)throw new Exception("Native ListView double buffering disabled");
                    SendMessage(list.Handle,0x1014,IntPtr.Zero,new IntPtr(600));
                    if(SendMessage(list.Handle,0x1027,IntPtr.Zero,IntPtr.Zero).ToInt32()==0)throw new Exception("Native list scrolling must remain available");
                    return 4;
                }
            }
        }
    }
}
