using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SteamDirect
{
    internal class RoundedButton : Button
    {
        private bool hovering, pressed;
        private float interaction;
        private readonly Timer transition = new Timer();
        private bool selected;
        internal bool SelectedNavigation
        {
            get { return selected; }
            set { if (selected == value) return; selected = value; UpdateMotion(); }
        }
        internal UiIcon Icon = UiIcon.None;
        internal float CornerRadius = UiStyle.ControlRadius;

        internal RoundedButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
            transition.Interval = 16;
            transition.Tick += (sender, args) =>
            {
                float target = (hovering || pressed || selected) ? 1F : 0F;
                interaction += (target - interaction) * .32F;
                if (Math.Abs(target - interaction) < .02F) { interaction = target; transition.Stop(); }
                Invalidate();
            };
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        private void UpdateMotion()
        {
            if (!IsHandleCreated || !Visible || !Enabled) { transition.Stop(); interaction = (hovering || selected) ? 1F : 0F; Invalidate(); return; }
            transition.Start();
            Invalidate();
        }
        protected override void Dispose(bool disposing) { if (disposing) transition.Dispose(); base.Dispose(disposing); }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (!Visible) transition.Stop(); }
        protected override void OnMouseEnter(EventArgs e) { hovering = true; UpdateMotion(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovering = pressed = false; UpdateMotion(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = e.Button == MouseButtons.Left; UpdateMotion(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; UpdateMotion(); base.OnMouseUp(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { pressed = true; UpdateMotion(); } base.OnKeyDown(e); }
        protected override void OnKeyUp(KeyEventArgs e) { pressed = false; UpdateMotion(); base.OnKeyUp(e); }
        protected override void OnLostFocus(EventArgs e) { pressed = false; UpdateMotion(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? AppTheme.Background(AppTheme.Page) : Parent.BackColor);
            if (Width < 2 || Height < 2) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = DeviceDpi / 96F;
            float radius = Math.Min(CornerRadius * scale, (Math.Min(Width, Height) - 1) / 2f);
            var box = new RectangleF(.5f, .5f, Width - 1, Height - 1);
            Color fill = !Enabled ? AppTheme.Background(AppTheme.Page) :
                pressed ? FlatAppearance.MouseDownBackColor :
                hovering ? FlatAppearance.MouseOverBackColor : BackColor;
            if (fill.A == 0) fill = BackColor;
            if (interaction > 0 && !pressed && Enabled)
            {
                Color hover = FlatAppearance.MouseOverBackColor;
                if (hover.A == 0) hover = BackColor;
                fill = Color.FromArgb((int)(fill.R + (hover.R - fill.R) * interaction),
                    (int)(fill.G + (hover.G - fill.G) * interaction),
                    (int)(fill.B + (hover.B - fill.B) * interaction));
            }
            using (var path = UiStyle.RoundRect(box, radius))
            using (var brush = new SolidBrush(fill))
            {
                e.Graphics.FillPath(brush, path);
            }
            var bounds = Rectangle.Inflate(ClientRectangle, -(int)(UiStyle.Space12 * scale), -2);
            Color textColor = Enabled ? ForeColor : AppTheme.Text(AppTheme.Disabled);
            var alignment = TextFormatFlags.HorizontalCenter;
            if (Icon != UiIcon.None)
            {
                float size = UiStyle.Space20 * scale;
                UiStyle.DrawIcon(e.Graphics, Icon, new RectangleF(UiStyle.Space16 * scale, (Height - size) / 2, size, size), textColor);
                bounds.X = (int)((UiStyle.Space16 + UiStyle.Space20 + UiStyle.Space12) * scale);
                bounds.Width = Math.Max(1, Width - bounds.X - (int)(UiStyle.Space12 * scale));
                alignment = TextFormatFlags.Left;
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, bounds, textColor,
                alignment | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            if (selected && Width > 8 && Height > 8)
            {
                using (var marker = new SolidBrush(AppTheme.Background(AppTheme.Accent)))
                    e.Graphics.FillRectangle(marker, 1, (int)(Height * .22F), Math.Max(2, (int)(3 * scale)), (int)(Height * .56F));
            }
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -8, -6), ForeColor, fill);
        }
    }
}
