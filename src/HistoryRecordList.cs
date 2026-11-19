using System;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
namespace SteamDirect {
 // Own a native image list so high-DPI text rows are not capped at ImageList's 256px limit.
 internal sealed class HistoryRecordList : ListView {
  internal HistoryRecordList(){DoubleBuffered=true;}
  private IntPtr images;private int rowHeight=120;
  private ConditionalWeakTable<ListViewItem, int[]> heights = new ConditionalWeakTable<ListViewItem, int[]>();
  private int measuredWidth, fittedWidth, fittedCount = -1, fittedDpi;
  private Font measuredFont, fittedFont;
  private Font recordTitleFont;
  internal Font RecordTitleFont
  {
      get { return recordTitleFont ?? (recordTitleFont = new Font(Font, FontStyle.Bold)); }
  }

  protected override void OnFontChanged(EventArgs e)
  {
      ReleaseTitleFont();
      base.OnFontChanged(e);
  }

  protected override void Dispose(bool disposing)
  {
      if (disposing) ReleaseTitleFont();
      base.Dispose(disposing);
  }

  private void ReleaseTitleFont()
  {
      if (recordTitleFont == null) return;
      recordTitleFont.Dispose();
      recordTitleFont = null;
  }

  internal int[] MeasureRow(ListViewItem item, int width, Func<ListViewItem, int, int[]> measure)
  {
      if (measuredWidth != width || !ReferenceEquals(measuredFont, Font))
      {
          measuredWidth = width;
          measuredFont = Font;
          heights = new ConditionalWeakTable<ListViewItem, int[]>();
      }
      int[] result;
      if (!heights.TryGetValue(item, out result))
      {
          result = measure(item, width);
          heights.Add(item, result);
      }
      return result;
  }

  internal bool RowLayoutChanged(int width, bool force = false)
  {
      int dpi = UiStyle.Dpi(this);
      if (!force && width == fittedWidth && fittedCount == Items.Count && fittedDpi == dpi && ReferenceEquals(fittedFont, Font))
          return false;
      fittedWidth = width;
      fittedCount = Items.Count;
      fittedDpi = dpi;
      fittedFont = Font;
      return true;
  }
  private int hoveredIndex = -1; internal int HoveredIndex { get { return hoveredIndex; } private set { hoveredIndex = value; } }
  protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);var item=GetItemAt(e.X,e.Y);int next=item==null?-1:item.Index;if(next==HoveredIndex)return;int previous=HoveredIndex;HoveredIndex=next;if(previous>=0&&previous<Items.Count)Invalidate(Items[previous].Bounds);if(next>=0&&next<Items.Count)Invalidate(Items[next].Bounds);}
  protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);int previous=HoveredIndex;HoveredIndex=-1;if(previous>=0&&previous<Items.Count)Invalidate(Items[previous].Bounds);}
  [DllImport("comctl32.dll")]private static extern IntPtr ImageList_Create(int width,int height,uint flags,int initial,int grow);
  [DllImport("comctl32.dll")]private static extern bool ImageList_Destroy(IntPtr list);
  [DllImport("user32.dll")]private static extern IntPtr SendMessage(IntPtr window,int message,IntPtr parameter,IntPtr value);
  protected override CreateParams CreateParams {get{var p=base.CreateParams;p.Style|=0x40;return p;}}
  internal void SetRowHeight(int height){height=Math.Max(1,height);if(rowHeight==height&&images!=IntPtr.Zero)return;rowHeight=height;ApplyHeight();}
  private void ApplyHeight(){if(!IsHandleCreated)return;var next=ImageList_Create(1,rowHeight,0x20,0,1);if(next==IntPtr.Zero)throw new InvalidOperationException("无法创建历史记录行布局。");SendMessage(Handle,0x1003,new IntPtr(1),next);var old=images;images=next;if(old!=IntPtr.Zero)ImageList_Destroy(old);}
  private void RoundEdges(){if(Width<2||Height<2)return;var old=Region;using(var path=UiStyle.RoundRect(new System.Drawing.RectangleF(0,0,Width,Height),UiStyle.ControlRadius*UiStyle.Dpi(this)/96F))Region=new System.Drawing.Region(path);if(old!=null)old.Dispose();}
  protected override void OnSizeChanged(EventArgs e){base.OnSizeChanged(e);RoundEdges();}
  protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);ApplyHeight();RoundEdges();}
  protected override void OnHandleDestroyed(EventArgs e){base.OnHandleDestroyed(e);if(images!=IntPtr.Zero){ImageList_Destroy(images);images=IntPtr.Zero;}}
 }
}
