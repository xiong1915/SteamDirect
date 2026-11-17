using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
namespace SteamDirect
{
    // Enlarge only the synthetic HWND's tracking limit, never the host display settings.
    internal sealed class OffscreenGeometry : NativeWindow
    {
        internal static void Resize(Form form,Size size)
        {
            if(!WindowEnvironment.OffscreenTests)throw new InvalidOperationException("Synthetic geometry is test-only");
            var observer=new OffscreenGeometry();observer.AssignHandle(form.Handle);
            try{
                form.MaximumSize=new Size(12000,12000);
                form.MinimumSize=new Size(size.Width+form.Width-form.ClientSize.Width,size.Height+form.Height-form.ClientSize.Height);
                form.ClientSize=size;
            }finally{observer.ReleaseHandle();}
        }
        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if(message.Msg==0x24&&message.LParam!=IntPtr.Zero){Marshal.WriteInt32(message.LParam,32,12000);Marshal.WriteInt32(message.LParam,36,12000);}
        }
    }
}
