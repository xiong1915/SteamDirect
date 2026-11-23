using System;
using System.Drawing;
using System.Windows.Forms;

namespace SteamDirect
{
    // The shell commits its layout once. Only this hit-test-transparent strip changes during the transition.
    internal sealed class SidebarTransition : Control
    {
        private readonly Motion.Value edge;
        private Bitmap expanded;
        private int collapsedWidth, expandedWidth;
        private bool collapsing;
        private Rectangle lastClip;

        internal SidebarTransition()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
            TabStop = false;
            Visible = false;
            edge = new Motion.Value(this, 0F, UpdateClip);
        }

        internal float CurrentWidth
        {
            get { return Visible ? EdgePixels : 0F; }
        }
        private float EdgePixels { get { return collapsedWidth + (expandedWidth - collapsedWidth) * edge.Current; } }

        internal void CaptureExpanded(Control sidebar)
        {
            if (expanded != null) expanded.Dispose();
            expanded = new Bitmap(sidebar.Width, sidebar.Height);
            sidebar.DrawToBitmap(expanded, sidebar.ClientRectangle);
        }

        internal void Animate(Rectangle strip, int compactWidth, bool collapse)
        {
            if (strip.Width <= compactWidth || strip.Height <= 0 || (collapse && expanded == null))
            {
                Finish();
                return;
            }
            collapsing = collapse;
            expandedWidth = strip.Width;
            collapsedWidth = compactWidth;
            Bounds = strip;
            BringToFront();
            if (!Visible) edge.Set(collapse ? 1F : 0F);
            ApplyClip(ClipForCurrentWidth());
            Visible = true;
            edge.SpringToFromFrame(collapse ? 0F : 1F, UiStyle.PageMotion);
        }

        internal void Finish()
        {
            if (Visible) Hide();
            if (Region != null)
            {
                var region = Region;
                Region = null;
                region.Dispose();
            }
            lastClip = Rectangle.Empty;
            if (expanded != null) { expanded.Dispose(); expanded = null; }
        }

        private void UpdateClip()
        {
            if (!Visible) return;
            float target = collapsing ? 0F : 1F;
            if (edge.Current == target) { Finish(); return; }
            ApplyClip(ClipForCurrentWidth());
        }

        private Rectangle ClipForCurrentWidth()
        {
            int width = Math.Max(collapsedWidth, Math.Min(expandedWidth, (int)Math.Round(EdgePixels)));
            return collapsing ? Rectangle.FromLTRB(collapsedWidth, 0, width, Height) :
                Rectangle.FromLTRB(width, 0, expandedWidth, Height);
        }

        private void ApplyClip(Rectangle clip)
        {
            if (clip == lastClip && Region != null) return;
            var old = Region;
            Region = new Region(clip);
            lastClip = clip;
            if (old != null) old.Dispose();
            if (Visible) Invalidate(clip);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (collapsing && expanded != null)
            {
                var dirty = Rectangle.Intersect(e.ClipRectangle, new Rectangle(Point.Empty, expanded.Size));
                if (!dirty.IsEmpty) e.Graphics.DrawImage(expanded, dirty, dirty, GraphicsUnit.Pixel);
            }
            else
                using (var fill = new SolidBrush(AppTheme.Background(AppTheme.Page)))
                    e.Graphics.FillRectangle(fill, e.ClipRectangle);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x84) { message.Result = new IntPtr(-1); return; }
            base.WndProc(ref message);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                edge.Dispose();
                Finish();
            }
            base.Dispose(disposing);
        }
    }
}
