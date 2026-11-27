using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SteamDirect
{
    internal sealed class ToggleSwitch : CheckBox
    {
        private readonly Motion.Value position;
        internal ToggleSwitch()
        {
            position = new Motion.Value(this, Checked ? 1F : 0F, InvalidateTrack);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnCheckedChanged(EventArgs e)
        {
            base.OnCheckedChanged(e);
            if (position != null) position.SpringTo(Checked ? 1F : 0F, 200);
        }
        protected override void Dispose(bool disposing) { if (disposing && position != null) position.Dispose(); base.Dispose(disposing); }
        private RectangleF TrackBounds()
        {
            float scale = UiStyle.Dpi(this) / 96F;
            float width = 36 * scale, height = 20 * scale;
            return new RectangleF(Math.Max(0, Width - width - 4 * scale), (Height - height) / 2, width, height);
        }

        private void InvalidateTrack()
        {
            Invalidate(Rectangle.Inflate(Rectangle.Ceiling(TrackBounds()), 2, 2));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (var surface = new SolidBrush(BackColor)) e.Graphics.FillRectangle(surface, e.ClipRectangle);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = UiStyle.Dpi(this) / 96f;
            var bounds = TrackBounds();
            float width = bounds.Width, height = bounds.Height, top = bounds.Y, left = bounds.X;
            float travel = Math.Max(0F, Math.Min(1F, position.Current));
            Color track = Motion.Blend(AppTheme.Edge, AppTheme.Text(AppTheme.Accent), Enabled ? travel : 0);
            using (var brush = new SolidBrush(track))
            using (var path = new GraphicsPath())
            {
                path.AddArc(left, top, height, height, 90, 180);
                path.AddArc(left + width - height, top, height, height, 270, 180);
                path.CloseFigure();
                e.Graphics.FillPath(brush, path);
            }
            float thumb = 14 * scale;
            using (var brush = new SolidBrush(Motion.Blend(AppTheme.Text(AppTheme.Muted), AppTheme.AccentForeground, travel)))
                e.Graphics.FillEllipse(brush, left + (3 + (36 - 14 - 6) * travel) * scale, top + 3 * scale, thumb, thumb);
            var textBounds = Rectangle.FromLTRB(0, 0, Math.Max(0, (int)(left - 12 * scale)), Height);
            if (e.ClipRectangle.IntersectsWith(textBounds))
                TextRenderer.DrawText(e.Graphics, Text, Font, textBounds,
                Enabled ? ForeColor : AppTheme.Text(AppTheme.Muted),
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -2, -2), ForeColor, BackColor);
        }
    }
}
