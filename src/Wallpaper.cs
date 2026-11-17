using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
namespace SteamDirect
{
    // Two bounded textures; no live blur or full-window frame cache.
    internal static class Wallpaper
    {
        private sealed class Texture : IDisposable
        {
            internal IntPtr Dc, Bitmap, Previous; internal Size Size;
            internal Texture(System.Drawing.Bitmap bitmap){Size=bitmap.Size;Bitmap=bitmap.GetHbitmap();Dc=CreateCompatibleDC(IntPtr.Zero);if(Dc!=IntPtr.Zero)Previous=SelectObject(Dc,Bitmap);}
            public void Dispose(){if(Dc!=IntPtr.Zero){if(Previous!=IntPtr.Zero)SelectObject(Dc,Previous);DeleteDC(Dc);Dc=IntPtr.Zero;}if(Bitmap!=IntPtr.Zero){DeleteObject(Bitmap);Bitmap=IntPtr.Zero;}}
        }
        private static Texture scene,glass; private static bool dark;
        private static Bitmap glassPixels;
        [StructLayout(LayoutKind.Sequential)]private struct PointNative{internal int X,Y;}
        [DllImport("gdi32.dll")]private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")]private static extern IntPtr SelectObject(IntPtr dc,IntPtr value);
        [DllImport("gdi32.dll")]internal static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll")]private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")]private static extern int SetStretchBltMode(IntPtr dc,int mode);
        [DllImport("gdi32.dll")]private static extern bool SetBrushOrgEx(IntPtr dc,int x,int y,out PointNative previous);
        [DllImport("gdi32.dll")]private static extern bool StretchBlt(IntPtr dc,int x,int y,int w,int h,IntPtr source,int sx,int sy,int sw,int sh,uint operation);
        [DllImport("gdi32.dll")]private static extern IntPtr CreatePatternBrush(IntPtr bitmap);
        static Wallpaper(){AppDomain.CurrentDomain.ProcessExit+=(sender,args)=>Release();}
        private static void Release(){if(scene!=null)scene.Dispose();if(glass!=null)glass.Dispose();if(glassPixels!=null)glassPixels.Dispose();scene=glass=null;glassPixels=null;}
        private static bool Prepare()
        {
            if(scene!=null&&dark==AppTheme.IsDark)return scene.Dc!=IntPtr.Zero&&glass!=null&&glass.Dc!=IntPtr.Zero;Release();
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("SteamDirect.Wallpaper")){
                if(stream==null)return false;
                using(var original=Image.FromStream(stream))
                using(var sharp=new Bitmap(1280,Math.Max(1,original.Height*1280/original.Width)))
                using(var soft=new Bitmap(320,Math.Max(1,original.Height*320/original.Width))){
                    using(var g=Graphics.FromImage(sharp)){g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.DrawImage(original,new Rectangle(Point.Empty,sharp.Size));using(var shade=new SolidBrush(AppTheme.IsDark?Color.FromArgb(55,8,15,27):Color.FromArgb(95,247,249,252)))g.FillRectangle(shade,0,0,sharp.Width,sharp.Height);}
                    using(var tiny=new Bitmap(160,Math.Max(1,soft.Height/2))){
                        using(var g=Graphics.FromImage(tiny)){g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.DrawImage(sharp,new Rectangle(Point.Empty,tiny.Size));}
                        using(var g=Graphics.FromImage(soft)){g.InterpolationMode=InterpolationMode.HighQualityBilinear;g.DrawImage(tiny,new Rectangle(Point.Empty,soft.Size));using(var shade=new SolidBrush(AppTheme.IsDark?Color.FromArgb(125,12,20,34):Color.FromArgb(205,250,252,255)))g.FillRectangle(shade,0,0,soft.Width,soft.Height);}
                    }
                    scene=new Texture(sharp);glass=new Texture(soft);glassPixels=new Bitmap(soft);dark=AppTheme.IsDark;
                }
            }
            return scene.Dc!=IntPtr.Zero&&glass.Dc!=IntPtr.Zero;
        }
        internal static Rectangle Placement(Control control,out Size rootSize)
        {
            Control root=control;var offset=Point.Empty;while(root.Parent!=null){offset.Offset(root.Left,root.Top);root=root.Parent;}rootSize=root.ClientSize;return new Rectangle(offset,control.ClientSize);
        }
        internal static bool Paint(Control control,Graphics graphics,bool force=false,bool material=false)
        {
            if(!force&&control.BackColor.ToArgb()!=AppTheme.Background(AppTheme.Page).ToArgb())return false;
            Size root;var bounds=Placement(control,out root);if(root.Width<=0||root.Height<=0||!Prepare())return false;
            var texture=material?glass:scene;float scale=Math.Max(root.Width/(float)texture.Size.Width,root.Height/(float)texture.Size.Height);
            float x=(root.Width-texture.Size.Width*scale)/2-bounds.X,y=(root.Height-texture.Size.Height*scale)/2-bounds.Y;
            var clip=RectangleF.Intersect(new RectangleF(0,0,control.Width,control.Height),graphics.VisibleClipBounds);
            clip=RectangleF.Intersect(clip,new RectangleF(-bounds.X,-bounds.Y,root.Width,root.Height));if(clip.Width<=0||clip.Height<=0)return true;
            var target=Rectangle.Ceiling(clip);IntPtr dc=graphics.GetHdc();
            try{PointNative origin;int previous=SetStretchBltMode(dc,3);SetBrushOrgEx(dc,0,0,out origin);StretchBlt(dc,target.X,target.Y,target.Width,target.Height,texture.Dc,(int)((clip.X-x)/scale),(int)((clip.Y-y)/scale),Math.Max(1,(int)(clip.Width/scale)),Math.Max(1,(int)(clip.Height/scale)),0x00CC0020);PointNative ignored;SetBrushOrgEx(dc,origin.X,origin.Y,out ignored);SetStretchBltMode(dc,previous);}finally{graphics.ReleaseHdc(dc);}return true;
        }
        internal static bool PaintGlass(Control control,Graphics graphics){return Paint(control,graphics,true,true);}
        internal static Brush MaterialBrush(Control control)
        {
            if(!Prepare())return new SolidBrush(AppTheme.Background(AppTheme.Surface));
            Size root;var bounds=Placement(control,out root);float scale=Math.Max(root.Width/(float)glassPixels.Width,root.Height/(float)glassPixels.Height);
            var brush=new TextureBrush(glassPixels,WrapMode.Clamp);
            using(var transform=new Matrix(scale,0,0,scale,(root.Width-glassPixels.Width*scale)/2-bounds.X,(root.Height-glassPixels.Height*scale)/2-bounds.Y))brush.Transform=transform;
            return brush;
        }
        internal static IntPtr GlassBrush(Control control)
        {
            if(control.Width<=0||control.Height<=0||(long)control.Width*control.Height>2000000)return IntPtr.Zero;
            using(var bitmap=new Bitmap(control.Width,control.Height)){
                using(var g=Graphics.FromImage(bitmap)){g.Clear(AppTheme.Background(AppTheme.Surface));PaintGlass(control,g);}
                IntPtr handle=bitmap.GetHbitmap();try{return CreatePatternBrush(handle);}finally{DeleteObject(handle);}
            }
        }
    }
}
