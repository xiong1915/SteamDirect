using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SteamDirect
{
    internal class RoundedButton : Button
    {
        private bool hovering, pressed;
        internal UiIcon Icon = UiIcon.None;
        internal float CornerRadius = UiStyle.ControlRadius;

        internal RoundedButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseEnter(EventArgs e) { hovering = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovering = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { pressed = true; Invalidate(); } base.OnKeyDown(e); }
        protected override void OnKeyUp(KeyEventArgs e) { pressed = false; Invalidate(); base.OnKeyUp(e); }
        protected override void OnLostFocus(EventArgs e) { pressed = false; Invalidate(); base.OnLostFocus(e); }

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
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -8, -6), ForeColor, fill);
        }
    }
}
