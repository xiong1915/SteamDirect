using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
namespace SteamDirect {
 // Own a native image list so high-DPI text rows are not capped at ImageList's 256px limit.
 internal sealed class HistoryRecordList : ListView {
  private IntPtr images;private int rowHeight=120;
  [DllImport("comctl32.dll")]private static extern IntPtr ImageList_Create(int width,int height,uint flags,int initial,int grow);
  [DllImport("comctl32.dll")]private static extern bool ImageList_Destroy(IntPtr list);
  [DllImport("user32.dll")]private static extern IntPtr SendMessage(IntPtr window,int message,IntPtr parameter,IntPtr value);
  protected override CreateParams CreateParams {get{var p=base.CreateParams;p.Style|=0x40;return p;}}
  internal void SetRowHeight(int height){height=Math.Max(1,height);if(rowHeight==height&&images!=IntPtr.Zero)return;rowHeight=height;ApplyHeight();}
  private void ApplyHeight(){if(!IsHandleCreated)return;var next=ImageList_Create(1,rowHeight,0x20,0,1);if(next==IntPtr.Zero)throw new InvalidOperationException("无法创建历史记录行布局。");SendMessage(Handle,0x1003,new IntPtr(1),next);var old=images;images=next;if(old!=IntPtr.Zero)ImageList_Destroy(old);}
  protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);ApplyHeight();}
  protected override void OnHandleDestroyed(EventArgs e){base.OnHandleDestroyed(e);if(images!=IntPtr.Zero){ImageList_Destroy(images);images=IntPtr.Zero;}}
 }
}