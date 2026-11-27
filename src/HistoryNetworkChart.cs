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
  private string emptyMessage = "从左侧选择一条游戏记录。";
  private bool invalidData;
  private Bitmap imageCache;
  private readonly Motion.Value reveal;
  private readonly ImageAttributes revealAttributes = new ImageAttributes();
  private readonly ColorMatrix revealColors = new ColorMatrix();
  private readonly ToolTip dataTip = new ToolTip { ShowAlways = false };
  private readonly List<HistorySample> tipSamples = new List<HistorySample>();
  private long tipAt = long.MinValue;
  private bool cachedDark;
  private bool fontChangedDuringDrag;
  private int cachedColor, cachedDpi;
  internal HistoryNetworkChart(){reveal=new Motion.Value(this,1F);DoubleBuffered=true;ResizeRedraw=true;Dock=DockStyle.Fill;BackColor=AppTheme.Surface;Font=UiStyle.Caption;AccessibleName="本局网络波动图";}
  internal void SetEmpty(string message)
  {
   SetData(null, null, null);
   emptyMessage = message;
   AccessibleDescription = message;
  }
  internal void SetData(HistorySession session,IList<HistorySample> values,IList<HistoryEvent> issues){
   emptyMessage = "本局没有可绘制的探测样本。";
   samples=values==null?(IList<HistorySample>)Array.Empty<HistorySample>():new List<HistorySample>(values);events=issues==null?(IList<HistoryEvent>)Array.Empty<HistoryEvent>():new List<HistoryEvent>(issues);
   start=session==null?0:session.Start;end=session==null?0:session.End;target=session==null?null:session.Target;
   if(samples.Count>0&&samples[0]!=null&&samples[samples.Count-1]!=null){if(start<=0)start=samples[0].At;if(end<=start)end=samples[samples.Count-1].At;}
   invalidData=(session!=null&&session.End>0&&session.End<session.Start)||start<0||start>253402300799999L||end<0||end>253402300799999L;
   foreach(var sample in samples)if(sample==null||sample.At<0||sample.At>253402300799999L)invalidData=true;
   foreach(var issue in events)if(issue==null||issue.Start<0||issue.Start>253402300799999L||issue.End<0||issue.End>253402300799999L)invalidData=true;
   tipSamples.Clear(); if(!invalidData){tipSamples.AddRange(samples);tipSamples.Sort((a,b)=>a.At.CompareTo(b.At));}tipAt=long.MinValue;dataTip.SetToolTip(this,null);
   ClearCache();
   if (samples.Count > 0 && Visible && IsHandleCreated && Motion.Allowed) { reveal.Set(.65F); reveal.To(1F, UiStyle.StatusMotion); }
   else reveal.Set(1F);
   AccessibleDescription=samples.Count==0?emptyMessage:"已加载 "+samples.Count+" 个采样点。超时不等于游戏实际丢包。";Invalidate();
  }
  private bool IsLiveResizing
  {
   get { var window = TopLevelControl as DesktopForm; return window != null && window.IsLiveResizing; }
  }
  protected override void OnFontChanged(EventArgs e)
  {
   base.OnFontChanged(e);
   if (IsLiveResizing) fontChangedDuringDrag = true;
   else ClearCache();
  }
  protected override void OnPaint(PaintEventArgs e){
   base.OnPaint(e);
   if(Width<=0||Height<=0)return;
   int dpi = UiStyle.Dpi(this);
   bool sameTheme = cachedDark == AppTheme.IsDark && cachedColor == AppTheme.ThemeColor.ToArgb();
   if (imageCache != null && sameTheme && (imageCache.Size != ClientSize || cachedDpi != dpi || fontChangedDuringDrag) && IsLiveResizing)
   {
    e.Graphics.InterpolationMode = InterpolationMode.Low;
    e.Graphics.DrawImage(imageCache, ClientRectangle, 0, 0, imageCache.Width, imageCache.Height, GraphicsUnit.Pixel);
    return;
   }
   if(imageCache==null||imageCache.Size!=ClientSize||!sameTheme||cachedDpi!=dpi||fontChangedDuringDrag){
    ClearCache();
    var image=new Bitmap(Width,Height);
    try{
     image.SetResolution(dpi,dpi);
     using(var graphics=Graphics.FromImage(image))Render(graphics);
     imageCache=image;cachedDark=AppTheme.IsDark;cachedColor=AppTheme.ThemeColor.ToArgb();cachedDpi=dpi;
     fontChangedDuringDrag = false;
    }catch{image.Dispose();throw;}
   }
   if (reveal.Current >= 1F)
   {
    var dirty = Rectangle.Intersect(ClientRectangle, e.ClipRectangle);
    if (!dirty.IsEmpty) e.Graphics.DrawImage(imageCache, dirty, dirty, GraphicsUnit.Pixel);
   }
   else
   {
    revealColors.Matrix33 = reveal.Current;
    revealAttributes.SetColorMatrix(revealColors);
    e.Graphics.DrawImage(imageCache, ClientRectangle, 0, 0, imageCache.Width, imageCache.Height, GraphicsUnit.Pixel, revealAttributes);
   }
  }
  // Visibility alone does not invalidate data, size, theme or DPI. Keep the bounded cache for tray/tab restore.
  private void ClearCache(){if(imageCache!=null){imageCache.Dispose();imageCache=null;}}
  protected override void Dispose(bool disposing){if(disposing){dataTip.Dispose();revealAttributes.Dispose();ClearCache();}base.Dispose(disposing);}
  protected override void OnMouseMove(MouseEventArgs e){
   base.OnMouseMove(e);if(tipSamples.Count==0||Width<=UiStyle.Scale(this,64))return;
   double fraction=Math.Max(0,Math.Min(1,(e.X-UiStyle.Scale(this,48))/(double)Math.Max(1,Width-UiStyle.Scale(this,64))));
   long at=start+(long)((Math.Max(start+1,end)-start)*fraction);int low=0,high=tipSamples.Count-1;
   while(low<high){int mid=low+(high-low)/2;if(tipSamples[mid].At<at)low=mid+1;else high=mid;}
   if(low>0&&Math.Abs(tipSamples[low-1].At-at)<Math.Abs(tipSamples[low].At-at))low--;
   var sample=tipSamples[low];if(tipAt==sample.At)return;tipAt=sample.At;
   dataTip.SetToolTip(this,FormatTime(sample.At)+" · "+sample.Mode+Environment.NewLine+"网关 ICMP："+ProbeText(sample.GatewayMs)+Environment.NewLine+"外部 ICMP："+ProbeText(sample.ExternalMs)+Environment.NewLine+"探测数据，不代表游戏服务器实际延迟。");
  }
  private static string ProbeText(int value){return value>=0?value+" ms":value==-1?"超时":"未检测";}
  private void Render(Graphics g){
   g.Clear(AppTheme.Background(AppTheme.Surface));g.SmoothingMode=SmoothingMode.AntiAlias;
   int line=UiStyle.TextHeight(this)+UiStyle.Scale(this,8);int pad=UiStyle.Scale(this,12);
   DrawText(g,"网关延迟 / 外部延迟（毫秒）",new Rectangle(pad,4,Width-pad*2,line),AppTheme.Ink);
   if(invalidData){DrawText(g,"记录包含无效时间，无法绘图。请导出原始记录核对。",new Rectangle(pad,line+8,Width-pad*2,line*3),AppTheme.Error);return;}
   if(samples.Count==0){
    TextRenderer.DrawText(g,emptyMessage,Font,new Rectangle(pad,line+8,Math.Max(1,Width-pad*2),Math.Max(1,Height-line-12)),
      AppTheme.Text(AppTheme.Muted),TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    return;
   }
   int legendWidth=Math.Max(1,(Width-pad*2)/3);
   DrawText(g,"网关 ━",new Rectangle(pad,line+4,legendWidth,line),AppTheme.Success);
   DrawText(g,"外部 ┄",new Rectangle(pad+legendWidth,line+4,legendWidth,line),AppTheme.Accent);
   DrawText(g,"超时 |",new Rectangle(pad+legendWidth*2,line+4,legendWidth,line),AppTheme.Error);
   DrawText(g,"红色背景：已记录的异常时段",new Rectangle(pad,line*2+4,Width-pad*2,line),AppTheme.Muted);
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
