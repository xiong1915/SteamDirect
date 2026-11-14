using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SteamDirect
{
    internal class RoundedButton : Button
    {
        private bool hovering, pressed;
        private readonly Motion.Value interaction;
        private readonly Motion.Value selection;
        private bool selected;
        internal bool SelectedNavigation
        {
            get { return selected; }
            set { if (selected == value) return; selected = value; UpdateMotion(); }
        }
        internal bool HorizontalIndicator;
        internal UiIcon Icon = UiIcon.None;
        internal float CornerRadius = UiStyle.ControlRadius;

        internal RoundedButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
            interaction = new Motion.Value(this, 0F);
            selection = new Motion.Value(this, 0F);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        private void UpdateMotion()
        {
            if (interaction == null || selection == null) return;
            interaction.To(pressed ? 1F : hovering ? .55F : 0F, pressed ? 90 : 180);
            selection.To(selected ? 1F : 0F, 210);
        }
        protected override void Dispose(bool disposing) { if (disposing) { interaction.Dispose(); selection.Dispose(); } base.Dispose(disposing); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); if (!Enabled) hovering = pressed = false; UpdateMotion(); }
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
            Color hover = FlatAppearance.MouseOverBackColor.A == 0 ? BackColor : FlatAppearance.MouseOverBackColor;
            Color down = FlatAppearance.MouseDownBackColor.A == 0 ? hover : FlatAppearance.MouseDownBackColor;
            float amount = interaction.Current;
            Color fill = !Enabled ? AppTheme.Background(AppTheme.Page) :
                amount <= .55F ? Motion.Blend(BackColor, hover, amount / .55F) : Motion.Blend(hover, down, (amount - .55F) / .45F);
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
            if (selection.Current > 0 && Width > 8 && Height > 8)
            {
                using (var marker = new SolidBrush(AppTheme.Background(AppTheme.Accent)))
                {
                    if (HorizontalIndicator)
                        e.Graphics.FillRectangle(marker, (int)(Width * .18F), Height - Math.Max(2, (int)(2 * scale)), Math.Max(1, (int)(Width * .64F * selection.Current)), Math.Max(2, (int)(2 * scale)));
                    else
                        e.Graphics.FillRectangle(marker, 1, (int)(Height * .22F), Math.Max(2, (int)(3 * scale)), Math.Max(1, (int)(Height * .56F * selection.Current)));
                }
            }
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -8, -6), ForeColor, fill);
        }
    }
}
