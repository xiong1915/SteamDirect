using System;
using System.Drawing;
using System.Windows.Forms;

namespace SteamDirect
{
    internal static class WindowEnvironment
    {
        // Set only by the separate acceptance executable, never by persisted user settings.
        internal static bool OffscreenTests { get; set; }
        internal static int LayoutDpi { get; set; }
        internal static Rectangle Fit(Rectangle bounds, Rectangle work)
        {
            int width = Math.Min(bounds.Width, work.Width), height = Math.Min(bounds.Height, work.Height);
            return new Rectangle(Math.Max(work.Left, Math.Min(bounds.Left, work.Right - width)),
                Math.Max(work.Top, Math.Min(bounds.Top, work.Bottom - height)), width, height);
        }
    }

    internal class DesktopForm : Form
    {
        protected override void OnLayout(LayoutEventArgs args)
        {
            var trace = NavigationTrace.Current;
            if (trace != null) trace.BeginLayout();
            try { base.OnLayout(args); } finally { if (trace != null) trace.EndLayout(); }
        }
        private bool displayPending, fitting, liveResize, resizePending, displayChangedDuringDrag;
        private string dragScreen;
        internal bool IsLiveResizing { get { return liveResize; } }

        protected override void OnVisibleChanged(EventArgs args)
        {
            base.OnVisibleChanged(args);
            if (!Visible && liveResize) FinishDrag(false);
        }
        protected override void OnResizeBegin(EventArgs args)
        {
            if (!liveResize)
            {
                dragScreen = WindowEnvironment.OffscreenTests ? null : Screen.FromHandle(Handle).DeviceName;
                liveResize = true;
                resizePending = displayChangedDuringDrag = false;
                SuspendLayout();
            }
            base.OnResizeBegin(args);
        }
        protected override void OnResizeEnd(EventArgs args)
        {
            if (liveResize) FinishDrag(true);
            base.OnResizeEnd(args);
        }

        private void FinishDrag(bool fitMonitor)
        {
            bool repaint = false;
            try
            {
                if (fitMonitor && !WindowEnvironment.OffscreenTests &&
                    (displayChangedDuringDrag || dragScreen != Screen.FromHandle(Handle).DeviceName))
                    CheckWorkArea();
                repaint = resizePending || displayChangedDuringDrag;
                if (resizePending && !IsDisposed) base.OnResize(EventArgs.Empty);
            }
            finally
            {
                liveResize = resizePending = displayChangedDuringDrag = false;
                dragScreen = null;
                ResumeLayout(repaint);
            }
            if (repaint && Visible) Invalidate(true);
        }
        private FormWindowState previousState;
        protected override bool ShowWithoutActivation { get { return WindowEnvironment.OffscreenTests; } }
        protected override CreateParams CreateParams
        {
            get { var value = base.CreateParams; if (WindowEnvironment.OffscreenTests) value.ExStyle |= 0x08000000; return value; }
        }
        internal void CheckWorkArea()
        {
            if (WindowEnvironment.OffscreenTests || !TopLevel || fitting) return;
            fitting = true;
            try {
            var work = Screen.FromControl(this).WorkingArea;
            // MinimumSize is an outer size, and must not force a window beyond a smaller work area.
            Size preferred = this is MainWindow
                ? new Size(UiStyle.Scale(this, UiStyle.MinimumWindowWidth), UiStyle.Scale(this, UiStyle.MinimumWindowHeight))
                : new Size(UiStyle.Scale(this, 880), UiStyle.Scale(this, 620));
            if (this is MainWindow && (work.Width < preferred.Width || work.Height < preferred.Height))
                preferred = new Size(UiStyle.Scale(this, UiStyle.CompactWindowWidth), UiStyle.Scale(this, UiStyle.CompactWindowHeight));
            var minimum = new Size(Math.Min(preferred.Width, work.Width), Math.Min(preferred.Height, work.Height));
            if (MinimumSize != minimum) MinimumSize = minimum;
            if (WindowState != FormWindowState.Normal) return;
            var fitted = WindowEnvironment.Fit(Bounds, work);
            if (Bounds != fitted) Bounds = fitted;
            } finally { fitting = false; }
        }
        protected override void WndProc(ref Message message)
        {
            // WinForms owns WM_DPICHANGED and its suggested rectangle. Never scale it a second time.
            int kind = message.Msg;
            base.WndProc(ref message);
            if (kind == 0x007E || kind == 0x02E0 || kind == 0x001A)
            {
                if (liveResize) displayChangedDuringDrag = true;
                else QueueDisplayCheck();
            }
        }
        private void QueueDisplayCheck()
        {
            if (!displayPending && IsHandleCreated && !IsDisposed)
            {
                displayPending = true;
                BeginInvoke((Action)(() => { displayPending = false; if (!IsDisposed && IsHandleCreated && !liveResize) CheckWorkArea(); }));
            }
        }
        protected override void OnResize(EventArgs e)
        {
            if (liveResize) { resizePending = true; return; }
            base.OnResize(e);
            var state = WindowState;
            if (state == FormWindowState.Normal && previousState != FormWindowState.Normal && !fitting) QueueDisplayCheck();
            previousState = state;
        }
        protected override void OnShown(EventArgs e) { base.OnShown(e); CheckWorkArea(); }
    }
}
