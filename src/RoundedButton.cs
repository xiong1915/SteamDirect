using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SteamDirect
{
    internal class RoundedButton : Button
    {
        private bool hovering, pressed;

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
            float radius = Math.Min(20 * e.Graphics.DpiX / 96f, (Math.Min(Width, Height) - 1) / 2f);
            float diameter = radius * 2;
            var box = new RectangleF(.5f, .5f, Width - 1, Height - 1);
            Color fill = !Enabled ? AppTheme.Background(AppTheme.Page) :
                pressed ? FlatAppearance.MouseDownBackColor :
                hovering ? FlatAppearance.MouseOverBackColor : BackColor;
            if (fill.A == 0) fill = BackColor;
            using (var path = new GraphicsPath())
            using (var brush = new SolidBrush(fill))
            {
                path.AddArc(box.Left, box.Top, diameter, diameter, 180, 90);
                path.AddArc(box.Right - diameter, box.Top, diameter, diameter, 270, 90);
                path.AddArc(box.Right - diameter, box.Bottom - diameter, diameter, diameter, 0, 90);
                path.AddArc(box.Left, box.Bottom - diameter, diameter, diameter, 90, 90);
                path.CloseFigure();
                e.Graphics.FillPath(brush, path);
            }
            var bounds = Rectangle.Inflate(ClientRectangle, -10, -2);
            TextRenderer.DrawText(e.Graphics, Text, Font, bounds, Enabled ? ForeColor : AppTheme.Text(AppTheme.Muted),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                TextFormatFlags.SingleLine);
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -8, -6), ForeColor, fill);
        }
    }
}
