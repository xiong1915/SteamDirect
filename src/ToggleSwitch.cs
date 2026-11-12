using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SteamDirect
{
    internal sealed class ToggleSwitch : CheckBox
    {
        internal ToggleSwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = DeviceDpi / 96f;
            float width = 36 * scale, height = 20 * scale;
            float top = (Height - height) / 2;
            Color track = Checked && Enabled ? AppTheme.Text(AppTheme.Accent) : AppTheme.Edge;
            using (var brush = new SolidBrush(track))
            using (var path = new GraphicsPath())
            {
                path.AddArc(0, top, height, height, 90, 180);
                path.AddArc(width - height, top, height, height, 270, 180);
                path.CloseFigure();
                e.Graphics.FillPath(brush, path);
            }
            float thumb = 14 * scale;
            using (var brush = new SolidBrush(Checked ? AppTheme.Background(Color.White) : AppTheme.Text(AppTheme.Muted)))
                e.Graphics.FillEllipse(brush, Checked ? width - thumb - 3 * scale : 3 * scale, top + 3 * scale, thumb, thumb);
            var textBounds = Rectangle.FromLTRB((int)(width + 12 * scale), 0, Width, Height);
            TextRenderer.DrawText(e.Graphics, Text, Font, textBounds,
                Enabled ? ForeColor : AppTheme.Text(AppTheme.Muted),
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -2, -2), ForeColor, BackColor);
        }
    }
}
