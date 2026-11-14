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
            position = new Motion.Value(this, Checked ? 1F : 0F);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnCheckedChanged(EventArgs e)
        {
            base.OnCheckedChanged(e);
            if (position != null) position.To(Checked ? 1F : 0F, 200);
        }
        protected override void Dispose(bool disposing) { if (disposing && position != null) position.Dispose(); base.Dispose(disposing); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = DeviceDpi / 96f;
            float width = 36 * scale, height = 20 * scale;
            float top = (Height - height) / 2;
            Color track = Motion.Blend(AppTheme.Edge, AppTheme.Text(AppTheme.Accent), Enabled ? position.Current : 0);
            using (var brush = new SolidBrush(track))
            using (var path = new GraphicsPath())
            {
                path.AddArc(0, top, height, height, 90, 180);
                path.AddArc(width - height, top, height, height, 270, 180);
                path.CloseFigure();
                e.Graphics.FillPath(brush, path);
            }
            float thumb = 14 * scale;
            using (var brush = new SolidBrush(Motion.Blend(AppTheme.Text(AppTheme.Muted), Color.White, position.Current)))
                e.Graphics.FillEllipse(brush, (3 + (36 - 14 - 6) * position.Current) * scale, top + 3 * scale, thumb, thumb);
            var textBounds = Rectangle.FromLTRB((int)(width + 12 * scale), 0, Width, Height);
            TextRenderer.DrawText(e.Graphics, Text, Font, textBounds,
                Enabled ? ForeColor : AppTheme.Text(AppTheme.Muted),
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -2, -2), ForeColor, BackColor);
        }
    }
}
