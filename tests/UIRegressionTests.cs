using System;
using System.Drawing;
using System.Windows.Forms;
using System.Reflection;
using System.Collections.Generic;
namespace SteamDirect {
 internal static class UIRegressionTests {
  private static int checks;
  private static void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;}
  [STAThread] internal static int Run(){
   Motion.Reduced=true;
   using(var form=new Form()) using(var control=new Panel()) {
    form.Controls.Add(control); form.Show(); Application.DoEvents();
    using(var value=new Motion.Value(control,0F)) {
     value.To(1F,180); Check(value.Current==1F&&Motion.ActiveCount==0,"Reduced motion must settle without scheduler.");
     Motion.Reduced=false; Motion.IsGaming=()=>true; value.To(0F,180);
     Check(value.Current==0F&&Motion.ActiveCount==0,"Gaming must settle immediately.");
     Motion.IsGaming=()=>false; bool allowed=Motion.Allowed; value.To(1F,180); Check(Motion.ActiveCount==(allowed?1:0),"Visible animation respects system motion preference.");
     control.Hide(); Check(value.Current==1F&&Motion.ActiveCount==0,"Hidden owner completes and releases motion.");
     control.Show(); value.To(0F,180); value.To(.25F,180); Motion.Reduced=true;
     Check(value.Current==.25F && Motion.ActiveCount==0,"Retarget settles at latest semantic state.");
     Motion.Reduced=false; value.To(0F,180); control.Dispose(); Check(Motion.ActiveCount==0,"Disposed owner releases motion.");
    }
   }
   foreach(bool dark in new[]{false,true}) foreach(string color in new[]{"#6750A4","#FF0000","#FFFFFF","#000000"}) {
    AppTheme.SetTheme(dark,color);
    Check(AppTheme.Contrast(AppTheme.Text(AppTheme.Accent),AppTheme.Background(AppTheme.Surface))>=4.5,"Accent text contrast.");
    Check(AppTheme.Contrast(AppTheme.AccentForeground,AppTheme.Background(AppTheme.Accent))>=4.5,"Primary button contrast.");
   }
   Motion.Reduced=true;
   using(var chart=new HistoryNetworkChart()) using(var bitmap=new Bitmap(640,360)) {
    chart.Size=bitmap.Size;
    var data=new List<HistorySample> {
     new HistorySample{At=1000,ExternalMs=10,GatewayMs=1,Mode="直连",Gateway="192.0.2.1"},
     new HistorySample{At=2000,ExternalMs=int.MaxValue,GatewayMs=-1,Mode="代理",Gateway="192.0.2.1"}
    };
    chart.SetData(new HistorySession {Start=1000,End=3000},data,null);
    chart.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));
    var cache=typeof(HistoryNetworkChart).GetField("imageCache",BindingFlags.Instance|BindingFlags.NonPublic);
    var first=cache.GetValue(chart);
    chart.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));
    Check(first!=null&&ReferenceEquals(first,cache.GetValue(chart)),"Unchanged chart uses bitmap cache.");
    var pixels=(Bitmap)first;int markerX=pixels.Width-12,markerY=pixels.Height/2;pixels.SetPixel(markerX,markerY,Color.Magenta);pixels.SetResolution(chart.DeviceDpi*2,chart.DeviceDpi*2);chart.DrawToBitmap(bitmap,chart.ClientRectangle);
    Check(bitmap.GetPixel(markerX,markerY).ToArgb()==Color.Magenta.ToArgb(),"Cached chart uses explicit pixels even when bitmap DPI metadata differs.");
    chart.SetData(new HistorySession{Start=long.MaxValue,End=long.MaxValue}, data,null);
    chart.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));
    Check((bool)typeof(HistoryNetworkChart).GetField("invalidData",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(chart),"Invalid chart timestamps display an error.");
    chart.SetData(null,null,null); Check(cache.GetValue(chart)==null,"New chart data invalidates cache.");
    chart.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));
    chart.Dispose();Check(cache.GetValue(chart)==null,"Chart disposal releases bitmap cache.");
   }
   CheckSeriesGap("直连", "192.0.2.1", 2000, "Continuous samples must produce a visible trace.", true);
   CheckSeriesGap("代理", "192.0.2.1", 2000, "Mode changes must break trace.");
   CheckSeriesGap("直连", "192.0.2.2", 2000, "Gateway changes must break trace.");
   CheckSeriesGap("直连", "192.0.2.1", 1000, "Duplicate timestamps must not connect.");
   Check((string)typeof(HistoryWindow).GetMethod("UtcTime",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{long.MaxValue})=="invalid_timestamp:"+long.MaxValue,"CSV preserves invalid timestamp value without throwing.");
   Check((string)typeof(HistoryWindow).GetMethod("SpanText",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{long.MaxValue})=="时长无效","Corrupt duration must not overflow.");
   Motion.IsGaming=null; Motion.Reduced=false; return checks;
  }
  private static void CheckSeriesGap(string mode,string gateway,long at,string message,bool connected=false) {
   using(var chart=new HistoryNetworkChart()) using(var image=new Bitmap(100,100)) using(var graphics=Graphics.FromImage(image)) {
    chart.SetData(null,new List<HistorySample> {
     new HistorySample{At=1000,ExternalMs=20,Mode="直连",Gateway="192.0.2.1"},
     new HistorySample{At=at,ExternalMs=80,Mode=mode,Gateway=gateway}
    },null);
    graphics.Clear(Color.White);int index=0;
    Func<long,float> x=value=>index++==0?10:90;Func<int,float> y=value=>value;
    typeof(HistoryNetworkChart).GetMethod("DrawSeries",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(chart,new object[]{graphics,new Rectangle(0,0,100,100),x,y,false});
    int marks=0; for(int px=30;px<70;px++)for(int py=30;py<70;py++)if(image.GetPixel(px,py).ToArgb()!=Color.White.ToArgb())marks++;
    Check(connected ? marks>0 : marks==0,message);
   }
  }
  [STAThread] private static int Main(){try{Console.WriteLine("UI regression tests: "+Run()+" passed");return 0;}catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
 }
}
