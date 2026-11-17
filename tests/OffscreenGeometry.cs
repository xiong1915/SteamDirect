using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
namespace SteamDirect
{
    // Enlarge only the synthetic HWND's tracking limit, never the host display settings.
    internal sealed class OffscreenGeometry : NativeWindow
    {
        [DllImport("user32.dll",SetLastError=true)]private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
        internal static void Resize(Form form,Size size)
        {
            if(!WindowEnvironment.OffscreenTests)throw new InvalidOperationException("Synthetic geometry is test-only");
            var observer=new OffscreenGeometry();observer.AssignHandle(form.Handle);
            try{
                form.MaximumSize=new Size(12000,12000);
                int width=size.Width+form.Width-form.ClientSize.Width,height=size.Height+form.Height-form.ClientSize.Height;
                if(!SetWindowPos(form.Handle,IntPtr.Zero,0,0,width,height,0x416))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                form.PerformLayout();
            }finally{observer.ReleaseHandle();}
        }
        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if(message.Msg==0x24&&message.LParam!=IntPtr.Zero){Marshal.WriteInt32(message.LParam,32,12000);Marshal.WriteInt32(message.LParam,36,12000);}
        }
    }
}
