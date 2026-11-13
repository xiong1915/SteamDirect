using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
namespace SteamDirect {
 internal sealed class HistoryNetworkChart : Control {
  private IList<HistorySample> samples = Array.Empty<HistorySample>();
  private IList<HistoryEvent> events = Array.Empty<HistoryEvent>();
  private long start,end; private string target;
  private bool invalidData;
  private Bitmap imageCache;
  private readonly Motion.Value reveal;
  private bool cachedDark;
  private int cachedColor, cachedDpi;
  internal HistoryNetworkChart(){reveal=new Motion.Value(this,1F);DoubleBuffered=true;ResizeRedraw=true;Dock=DockStyle.Fill;BackColor=AppTheme.Surface;Font=UiStyle.Caption;AccessibleName="本局网络波动图";}
  internal void SetData(HistorySession session,IList<HistorySample> values,IList<HistoryEvent> issues){
   samples=values==null?(IList<HistorySample>)Array.Empty<HistorySample>():new List<HistorySample>(values);events=issues==null?(IList<HistoryEvent>)Array.Empty<HistoryEvent>():new List<HistoryEvent>(issues);
   start=session==null?0:session.Start;end=session==null?0:session.End;target=session==null?null:session.Target;
   if(samples.Count>0&&samples[0]!=null&&samples[samples.Count-1]!=null){if(start<=0)start=samples[0].At;if(end<=start)end=samples[samples.Count-1].At;}
   invalidData=(session!=null&&session.End>0&&session.End<session.Start)||start<0||start>253402300799999L||end<0||end>253402300799999L;
   foreach(var sample in samples)if(sample==null||sample.At<0||sample.At>253402300799999L)invalidData=true;
   foreach(var issue in events)if(issue==null||issue.Start<0||issue.Start>253402300799999L||issue.End<0||issue.End>253402300799999L)invalidData=true;
   ClearCache();reveal.Set(samples.Count==0?1F:0F);reveal.To(1F,240);
   AccessibleDescription="本地网关与外部目标 ICMP 延迟；超时不等于游戏实际丢包。";Invalidate();
  }
  protected override void OnFontChanged(EventArgs e){base.OnFontChanged(e);ClearCache();Height=Math.Max(Parent == null ? 0 : Parent.ClientSize.Height,(UiStyle.TextHeight(this)+UiStyle.Scale(this,8))*6+UiStyle.Scale(this,80));}
  protected override void OnPaint(PaintEventArgs e){
   base.OnPaint(e);
   if(Width<=0||Height<=0)return;
   if(imageCache==null||imageCache.Size!=ClientSize||cachedDark!=AppTheme.IsDark||cachedColor!=AppTheme.ThemeColor.ToArgb()||cachedDpi!=DeviceDpi){
    ClearCache();
    var image=new Bitmap(Width,Height);
    try{
     image.SetResolution(DeviceDpi,DeviceDpi);
     using(var graphics=Graphics.FromImage(image))Render(graphics);
     imageCache=image;cachedDark=AppTheme.IsDark;cachedColor=AppTheme.ThemeColor.ToArgb();cachedDpi=DeviceDpi;
    }catch{image.Dispose();throw;}
   }
   if(reveal.Current>=.999F)e.Graphics.DrawImage(imageCache,ClientRectangle,0,0,imageCache.Width,imageCache.Height,GraphicsUnit.Pixel);
   else {
    e.Graphics.Clear(AppTheme.Background(AppTheme.Surface));
    using(var attributes=new ImageAttributes()) {
     var matrix=new ColorMatrix();matrix.Matrix33=reveal.Current;attributes.SetColorMatrix(matrix);
     e.Graphics.DrawImage(imageCache,ClientRectangle,0,0,imageCache.Width,imageCache.Height,GraphicsUnit.Pixel,attributes);
    }
   }
  }
  protected override void OnVisibleChanged(EventArgs e){base.OnVisibleChanged(e);if(!Visible)ClearCache();}
  protected override void OnSizeChanged(EventArgs e){base.OnSizeChanged(e);ClearCache();}
  private void ClearCache(){if(imageCache!=null){imageCache.Dispose();imageCache=null;}}
  protected override void Dispose(bool disposing){if(disposing)reveal.Dispose();if(disposing&&imageCache!=null){imageCache.Dispose();imageCache=null;}base.Dispose(disposing);}
  private void Render(Graphics g){
   g.Clear(AppTheme.Background(AppTheme.Surface));g.SmoothingMode=SmoothingMode.AntiAlias;
   int line=UiStyle.TextHeight(this)+UiStyle.Scale(this,8);int pad=UiStyle.Scale(this,12);
   DrawText(g,"网关延迟 / 外部延迟（毫秒）",new Rectangle(pad,4,Width-pad*2,line),AppTheme.Ink);
   DrawText(g,"网关 ━",new Rectangle(pad,line+4,UiStyle.Scale(this,100),line),AppTheme.Success);
   DrawText(g,"外部 ┄",new Rectangle(pad+UiStyle.Scale(this,108),line+4,UiStyle.Scale(this,100),line),AppTheme.Accent);
   DrawText(g,"超时 |",new Rectangle(pad+UiStyle.Scale(this,216),line+4,UiStyle.Scale(this,100),line),AppTheme.Error);
   DrawText(g,"红色背景：已记录的异常时段",new Rectangle(pad,line*2+4,Width-pad*2,line),AppTheme.Muted);
   if(invalidData){DrawText(g,"记录包含无效时间，无法绘图。请导出原始记录核对。",new Rectangle(pad,line*3,Width-pad*2,line*2),AppTheme.Error);return;}
   if(samples.Count==0){DrawText(g,"本局没有可绘制的探测数据",new Rectangle(pad,line*3,Width-pad*2,line*2),AppTheme.Muted);return;}
   var plot=new Rectangle(UiStyle.Scale(this,48),line*3+8,Math.Max(1,Width-UiStyle.Scale(this,64)),Math.Max(1,Height-line*6-24));
   if(plot.Width<40||plot.Height<30)return;
   long finish=Math.Max(start+1,end);int max=10;
   foreach(var sample in samples){max=Math.Max(max,sample.GatewayMs);max=Math.Max(max,sample.ExternalMs);}
   double ceiling=Math.Ceiling(max/10.0)*10;
   Func<long,float> x=at=>plot.Left+(float)(Math.Max(0,Math.Min(finish-start,at-start))*(double)plot.Width/(finish-start));
   Func<int,float> y=ms=>plot.Bottom-(float)(Math.Max(0,ms)*(double)plot.Height/ceiling);
   using(var shade=new SolidBrush(Color.FromArgb(24,AppTheme.Text(AppTheme.Error))))
    foreach(var issue in events){long until=issue.End>0?issue.End:finish;if(until<start||issue.Start>finish)continue;float left=x(issue.Start);g.FillRectangle(shade,left,plot.Top,Math.Max(2,x(until)-left),plot.Height);}
   using(var pen=new Pen(AppTheme.Edge))for(int i=0;i<=4;i++){float yy=plot.Top+plot.Height*i/4F;g.DrawLine(pen,plot.Left,yy,plot.Right,yy);DrawText(g,(ceiling*(4-i)/4).ToString("0", System.Globalization.CultureInfo.InvariantCulture),new Rectangle(0,(int)yy-UiStyle.TextHeight(this)/2,UiStyle.Scale(this,44),line),AppTheme.Muted);}
   DrawSeries(g,plot,x,y,true);DrawSeries(g,plot,x,y,false);
   for(int i=0;i<3;i++){long at=start+(finish-start)*i/2;int xx=(int)(plot.Left+plot.Width*i/2F);var text=FormatTime(at);DrawText(g,text,new Rectangle(xx-(i==2?UiStyle.Scale(this,76):i==1?UiStyle.Scale(this,38):0),plot.Bottom+6,UiStyle.Scale(this,80),line),AppTheme.Muted);}
   DrawText(g,"外部目标："+(target??"未知")+" · 红色短线为探测超时",new Rectangle(pad,Height-line*2-4,Width-pad*2,line),AppTheme.Muted);
   DrawText(g,"ICMP 探测不等于游戏实际延迟或丢包；无数据处不连线。",new Rectangle(pad,Height-line-4,Width-pad*2,line),AppTheme.Muted);
  }
  private void DrawSeries(Graphics g,Rectangle plot,Func<long,float>x,Func<int,float>y,bool gateway){
   PointF? previous=null;long previousAt=0;string previousGateway=null,previousMode=null;
   using(var pen=new Pen(AppTheme.Text(gateway?AppTheme.Success:AppTheme.Accent),1.7F){DashStyle=gateway?DashStyle.Solid:DashStyle.Dash})
   using(var missed=new Pen(AppTheme.Text(AppTheme.Error),1.5F))
   foreach(var sample in samples){int ms=gateway?sample.GatewayMs:sample.ExternalMs;
    if(ms<0){previous=null;if(ms==-1)g.DrawLine(missed,x(sample.At),plot.Bottom-5,x(sample.At),plot.Bottom);continue;}
    var point=new PointF(x(sample.At),y(ms));
    if(previous.HasValue&&sample.At>previousAt&&sample.At-previousAt<=65000&&previousGateway==sample.Gateway&&previousMode==sample.Mode)g.DrawLine(pen,previous.Value,point);
    else g.DrawEllipse(pen,point.X-1.5F,point.Y-1.5F,3,3);
    previous=point;previousAt=sample.At;previousGateway=sample.Gateway;previousMode=sample.Mode;
   }
  }
  private static string FormatTime(long milliseconds){
   if(milliseconds<0||milliseconds>253402300799999L)return "时间无效";
   return new DateTimeOffset(1970,1,1,0,0,0,TimeSpan.Zero).AddMilliseconds(milliseconds).ToLocalTime().ToString("HH:mm:ss");
  }
  private void DrawText(Graphics g,string text,Rectangle box,Color color){TextRenderer.DrawText(g,text,Font,box,AppTheme.Text(color),TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix);}
 }
}
