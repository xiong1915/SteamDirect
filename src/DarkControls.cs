using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SteamDirect
{
    // Retain the native combo's keyboard, IME, selection and accessibility implementation.
    internal sealed class DarkComboBox : ComboBox
    {
        [StructLayout(LayoutKind.Sequential)] private struct ComboInfo { internal int size; internal Rectangle item, button; internal uint state; internal IntPtr combo, edit, list; }
        [DllImport("user32.dll")] private static extern bool GetComboBoxInfo(IntPtr window, ref ComboInfo info);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr window, string app, string id);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        protected override void OnDropDown(EventArgs e)
        {
            var info = new ComboInfo { size = Marshal.SizeOf(typeof(ComboInfo)) };
            if (GetComboBoxInfo(Handle, ref info) && info.list != IntPtr.Zero) SetWindowTheme(info.list, AppTheme.IsDark ? "DarkMode_Explorer" : "Explorer", null);
            base.OnDropDown(e);
        }
        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if (DropDownStyle != ComboBoxStyle.DropDownList) return;
            if (message.Msg == 0xF) {
                IntPtr dc = GetDC(Handle); if (dc == IntPtr.Zero) return;
                try { using (var graphics = Graphics.FromHdc(dc)) DrawFace(graphics); } finally { ReleaseDC(Handle, dc); }
            } else if ((message.Msg == 0x318 || message.Msg == 0x317) && message.WParam != IntPtr.Zero) {
                using (var graphics = Graphics.FromHdc(message.WParam)) DrawFace(graphics);
            }
        }
        private void DrawFace(Graphics graphics)
        {
            var rect = ClientRectangle;
            graphics.Clear(AppTheme.Background(AppTheme.InputSurface));
            int arrow = UiStyle.Scale(this, 24), pad = UiStyle.Scale(this, 8);
            TextRenderer.DrawText(graphics, Text, Font, new Rectangle(pad, 0, Math.Max(1, Width - arrow - pad), Height),
                AppTheme.Text(Enabled ? AppTheme.Ink : AppTheme.Muted), TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            int x = rect.Right - arrow / 2, y = rect.Height / 2, d = UiStyle.Scale(this, 4);
            using (var pen = new Pen(AppTheme.Text(AppTheme.Muted), Math.Max(1, UiStyle.Dpi(this) / 96F)))
                graphics.DrawLines(pen, new[] { new Point(x - d, y - d / 2), new Point(x, y + d / 2), new Point(x + d, y - d / 2) });
            if (Focused && !DroppedDown) ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(rect, -3, -3), AppTheme.Text(AppTheme.Accent), AppTheme.Background(AppTheme.Surface));
        }
    }

    internal sealed class DarkScrollBar : Control
    {
        private int maximum, page = 1, value, dragY, dragValue;
        private bool hover, dragging;
        internal event Action<int> ScrollTo;
        internal int Value { get { return value; } }
        internal void SetRange(int max, int visible, int position)
        {
            max = Math.Max(0, max); visible = Math.Max(1, visible); position = Math.Max(0, Math.Min(max, position));
            if (maximum == max && page == visible && value == position) return;
            maximum = max; page = visible; value = position; Invalidate();
        }
        internal DarkScrollBar() { DoubleBuffered = true; TabStop = false; AccessibleRole = AccessibleRole.ScrollBar; AccessibleName = "垂直滚动"; }
        private Rectangle Thumb
        {
            get {
                int inset = UiStyle.Scale(this, 3), track = Math.Max(1, Height - inset * 2);
                int length = Math.Min(track, Math.Max(UiStyle.Scale(this, 28), (int)((long)track * page / Math.Max(1L, (long)maximum + page))));
                int y = inset + (int)((long)(track - length) * value / Math.Max(1, maximum));
                return new Rectangle(inset, y, Math.Max(1, Width - inset * 2), length);
            }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(AppTheme.Background(AppTheme.Surface));
            if (maximum == 0) return;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = UiStyle.RoundRect(Thumb, Width / 2F))
            using (var brush = new SolidBrush(dragging ? AppTheme.Text(AppTheme.Accent) : hover ? AppTheme.Text(AppTheme.Muted) : AppTheme.Edge)) e.Graphics.FillPath(brush, path);
        }
        private void MoveTo(int position) { if (ScrollTo != null) ScrollTo(Math.Max(0, Math.Min(maximum, position))); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); if (e.Button != MouseButtons.Left) return;
            if (Thumb.Contains(e.Location)) { dragging = true; dragY = e.Y; dragValue = value; Capture = true; Invalidate(); }
            else MoveTo(value + (e.Y < Thumb.Top ? -page : page));
        }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) MoveTo(dragValue + (int)((long)(e.Y - dragY) * maximum / Math.Max(1, Height - UiStyle.Scale(this, 6) - Thumb.Height))); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; Capture = false; Invalidate(); }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) { dragging = false; Invalidate(); } }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); MoveTo(value - Math.Sign(e.Delta) * Math.Max(1, SystemInformation.MouseWheelScrollLines)); }
    }

    // Replace native scroll chrome while retaining content, selection, keyboard and IME.
    internal sealed class NativeScrollFrame : UserControl
    {
        private readonly Panel viewport = new Panel { Margin = Padding.Empty };
        private readonly DarkScrollBar bar = new DarkScrollBar();
        private readonly Control content;
        private readonly Observer observer;
        private bool synchronizing;
        [DllImport("user32.dll")] private static extern bool ShowScrollBar(IntPtr window, int bar, bool show);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        internal NativeScrollFrame(Control content)
        {
            var text = content as TextBox; if (text != null) text.ScrollBars = ScrollBars.None;
            var rich = content as RichTextBox; if (rich != null) rich.ScrollBars = RichTextBoxScrollBars.None;
            this.content = content; Dock = DockStyle.Fill; Margin = Padding.Empty;
            Controls.Add(viewport); Controls.Add(bar); viewport.Controls.Add(content); content.Dock = DockStyle.None;
            bar.Width = UiStyle.Scale(this, 14); bar.ScrollTo += ScrollContent;
            observer = new Observer(this); content.HandleCreated += HandleCreatedContent; content.HandleDestroyed += HandleDestroyedContent;
            if (content.IsHandleCreated) observer.AssignHandle(content.Handle);
        }
        private void HandleCreatedContent(object sender, EventArgs e) { observer.AssignHandle(content.Handle); Synchronize(); }
        private void HandleDestroyedContent(object sender, EventArgs e) { observer.ReleaseHandle(); }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e); if (content == null) return;
            bar.Bounds = new Rectangle(Math.Max(0, Width - UiStyle.Scale(this, 14)), 0, UiStyle.Scale(this, 14), Height);
            viewport.Bounds = new Rectangle(0, 0, Math.Max(1, bar.Left), Height);
            // A separate scroll surface avoids native white chrome, including during offscreen capture.
            content.Bounds = new Rectangle(0, 0, Math.Max(1, viewport.ClientSize.Width), Math.Max(1, viewport.ClientSize.Height));
            Synchronize();
        }
        private void Synchronize()
        {
            if (synchronizing || content == null || content.IsDisposed || content.Disposing || !content.IsHandleCreated || IsDisposed || Disposing) return;
            synchronizing = true;
            try {
                var list = content as ListView;
                if (list != null) {
                    ShowScrollBar(content.Handle, 3, false);
                    // Handle-created/paint callbacks can run while the managed Items collection is mutating.
                    // Query the native view atomically instead of dereferencing transient ListViewItems.
                    int count = SendMessage(content.Handle, 0x1004, IntPtr.Zero, IntPtr.Zero).ToInt32();
                    int visible = Math.Max(1, SendMessage(content.Handle, 0x1028, IntPtr.Zero, IntPtr.Zero).ToInt32());
                    int position = SendMessage(content.Handle, 0x1027, IntPtr.Zero, IntPtr.Zero).ToInt32();
                    bar.SetRange(Math.Max(0, count - visible), visible, position);
                } else {
                    int lines = SendMessage(content.Handle, 0x00BA, IntPtr.Zero, IntPtr.Zero).ToInt32();
                    int first = SendMessage(content.Handle, 0x00CE, IntPtr.Zero, IntPtr.Zero).ToInt32();
                    int visible = Math.Max(1, content.ClientSize.Height / Math.Max(1, UiStyle.TextHeight(content)));
                    bar.SetRange(Math.Max(first, lines - visible), visible, first);
                }
            } finally { synchronizing = false; }
        }
        private void ScrollContent(int position)
        {
            if (!content.IsHandleCreated) return;
            int difference = position - bar.Value;
            var list = content as ListView;
            if (list != null) {
                int row = list.Items.Count == 0 ? 1 : list.Items[0].Bounds.Height;
                SendMessage(content.Handle, 0x1014, IntPtr.Zero, new IntPtr(difference * Math.Max(1, row)));
            } else SendMessage(content.Handle, 0x00B6, IntPtr.Zero, new IntPtr(difference)); // EM_LINESCROLL
            Synchronize();
        }
        protected override void Dispose(bool disposing) { if (disposing) { observer.ReleaseHandle(); content.HandleCreated -= HandleCreatedContent; content.HandleDestroyed -= HandleDestroyedContent; } base.Dispose(disposing); }
        private sealed class Observer : NativeWindow
        {
            private readonly NativeScrollFrame owner;
            internal Observer(NativeScrollFrame owner) { this.owner = owner; }
            protected override void WndProc(ref Message message) { base.WndProc(ref message); if (message.Msg == 0xF || message.Msg == 0x115 || message.Msg == 0x20A || message.Msg == 5 || message.Msg == 0x100 || message.Msg == 0xC || message.Msg == 0x1014) owner.Synchronize(); }
        }
    }
}
