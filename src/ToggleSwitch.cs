using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SteamDirect
{
    internal sealed class ToggleSwitch : CheckBox
    {
        private readonly Timer motion = new Timer();
        private float position;
        internal ToggleSwitch()
        {
            motion.Interval = 16;
            position = Checked ? 1F : 0F;
            motion.Tick += (sender, args) =>
            {
                float target = Checked ? 1F : 0F;
                position += (target - position) * .32F;
                if (Math.Abs(target - position) < .02F) { position = target; motion.Stop(); }
                Invalidate();
            };
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnCheckedChanged(EventArgs e)
        {
            base.OnCheckedChanged(e);
            if (IsHandleCreated && Visible && Enabled) motion.Start();
            else { motion.Stop(); position = Checked ? 1F : 0F; Invalidate(); }
        }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (!Visible) motion.Stop(); }
        protected override void Dispose(bool disposing) { if (disposing) motion.Dispose(); base.Dispose(disposing); }
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
                e.Graphics.FillEllipse(brush, (3 + (36 - 14 - 6) * position) * scale, top + 3 * scale, thumb, thumb);
            var textBounds = Rectangle.FromLTRB((int)(width + 12 * scale), 0, Width, Height);
            TextRenderer.DrawText(e.Graphics, Text, Font, textBounds,
                Enabled ? ForeColor : AppTheme.Text(AppTheme.Muted),
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -2, -2), ForeColor, BackColor);
        }
    }
}
