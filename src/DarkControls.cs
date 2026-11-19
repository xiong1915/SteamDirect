using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SteamDirect
{
    // Retain the native combo's keyboard, IME, selection and accessibility implementation.
    internal sealed class DarkComboBox : ComboBox
    {
        internal Rectangle CaptionBounds
        {
            get
            {
                int pad = UiStyle.Scale(this, 8);
                return new Rectangle(pad, 0, Math.Max(0, Width - UiStyle.Scale(this, 24) - pad), Height);
            }
        }
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
            if (Width < 2 || Height < 2) return;
            using (var background = new SolidBrush(UiStyle.ParentSurface(this)))
                graphics.FillRectangle(background, rect);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var outline = UiStyle.RoundRect(new RectangleF(.5F, .5F, Width - 1, Height - 1), UiStyle.ControlRadius * UiStyle.Dpi(this) / 96F))
            {
                using (var fill = new SolidBrush(BackColor)) graphics.FillPath(fill, outline);
                using (var edge = new Pen(Focused ? AppTheme.Focus : AppTheme.Edge))
                    graphics.DrawPath(edge, outline);
            }
            int arrow = UiStyle.Scale(this, 24);
            TextRenderer.DrawText(graphics, Text, Font, CaptionBounds,
                AppTheme.Text(Enabled ? AppTheme.Ink : AppTheme.Muted), TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            int x = rect.Right - arrow / 2, y = rect.Height / 2, d = UiStyle.Scale(this, 4);
            using (var pen = new Pen(AppTheme.Text(AppTheme.Muted), Math.Max(1, UiStyle.Dpi(this) / 96F)))
                graphics.DrawLines(pen, new[] { new Point(x - d, y - d / 2), new Point(x, y + d / 2), new Point(x + d, y - d / 2) });
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
            e.Graphics.Clear(UiStyle.ParentSurface(this));
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

    // Text uses themed scroll chrome; ListView exclusively owns its native scrolling.
    internal sealed class NativeScrollFrame : UserControl
    {
        private readonly Panel viewport;
        private readonly DarkScrollBar bar = new DarkScrollBar();
        private readonly Control content;
        private readonly Observer observer;
        private bool synchronizing;
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        internal NativeScrollFrame(Control content)
        {
            DoubleBuffered = true;
            var text = content as TextBox; if (text != null) text.ScrollBars = ScrollBars.None;
            var rich = content as RichTextBox; if (rich != null) rich.ScrollBars = RichTextBoxScrollBars.None;
            this.content = content; Dock = DockStyle.Fill; Margin = Padding.Empty;
            viewport = new Panel { Margin = Padding.Empty };
            if (content is ListView) bar.Visible = false;
            Controls.Add(viewport); Controls.Add(bar); viewport.Controls.Add(content); content.Dock = DockStyle.None;
            bar.Width = UiStyle.Scale(this, 14); bar.ScrollTo += ScrollContent;
            observer = new Observer(this); content.HandleCreated += HandleCreatedContent; content.HandleDestroyed += HandleDestroyedContent;
            if (content.IsHandleCreated) HandleCreatedContent(content, EventArgs.Empty);
        }
        private void HandleCreatedContent(object sender, EventArgs e)
        {
            if (content is ListView) { SendMessage(content.Handle, 0x1036, new IntPtr(0x410000), new IntPtr(0x10000)); return; }
            observer.AssignHandle(content.Handle); Synchronize();
        }
        private void HandleDestroyedContent(object sender, EventArgs e) { observer.ReleaseHandle(); }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e); if (content == null) return;
            bar.Bounds = new Rectangle(Math.Max(0, Width - UiStyle.Scale(this, 14)), 0, UiStyle.Scale(this, 14), Height);
            viewport.Bounds = new Rectangle(0, 0, Math.Max(1, content is ListView ? Width : bar.Left), Height);
            // ListView uses the full viewport; text reserves space for its custom scrollbar.
            content.Bounds = new Rectangle(0, 0, Math.Max(1, viewport.ClientSize.Width), Math.Max(1, viewport.ClientSize.Height));
            Synchronize();
        }
        private void Synchronize()
        {
            if (synchronizing || content == null || content is ListView || content.IsDisposed || content.Disposing || !content.IsHandleCreated || IsDisposed || Disposing) return;
            synchronizing = true;
            try {
                int lines = SendMessage(content.Handle, 0x00BA, IntPtr.Zero, IntPtr.Zero).ToInt32();
                int first = SendMessage(content.Handle, 0x00CE, IntPtr.Zero, IntPtr.Zero).ToInt32();
                int visible = Math.Max(1, content.ClientSize.Height / Math.Max(1, UiStyle.TextHeight(content)));
                bar.SetRange(Math.Max(first, lines - visible), visible, first);
            } finally { synchronizing = false; }
        }
        private void ScrollContent(int position)
        {
            if (!content.IsHandleCreated) return;
            int difference = position - bar.Value;
            SendMessage(content.Handle, 0x00B6, IntPtr.Zero, new IntPtr(difference)); // EM_LINESCROLL
            Synchronize();
        }
        protected override void Dispose(bool disposing) { if (disposing) { observer.ReleaseHandle(); content.HandleCreated -= HandleCreatedContent; content.HandleDestroyed -= HandleDestroyedContent; } base.Dispose(disposing); }
        private sealed class Observer : NativeWindow
        {
            private readonly NativeScrollFrame owner;
            internal Observer(NativeScrollFrame owner) { this.owner = owner; }
            protected override void WndProc(ref Message message) { base.WndProc(ref message); if (message.Msg == 0xF || message.Msg == 0x115 || message.Msg == 0x20A || message.Msg == 5 || message.Msg == 0x100 || message.Msg == 0xC) owner.Synchronize(); }
        }
    }
}
