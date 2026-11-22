using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SteamDirect
{
    internal class RoundedButton : Button
    {
        private bool hovering, pointerDown, spaceDown;
        private readonly Motion.Value interaction;
        private readonly Motion.Value selection;
        private bool selected;
        internal bool SelectedNavigation
        {
            get { return selected; }
            set
            {
                if (selected == value) return;
                selected = value;
                if (selection != null) selection.To(selected ? 1F : 0F, UiStyle.StatusMotion);
                else Invalidate();
            }
        }
        internal bool HorizontalIndicator;
        internal UiIcon Icon = UiIcon.None;
        internal float CornerRadius = UiStyle.ControlRadius;
        internal int CaptionInset = UiStyle.Space12;
        internal bool TextOnly;
        internal bool IconOnly;
        internal float IconOpacity = 1F;
        internal float CaptionOpacity = 1F;
        internal const TextFormatFlags CaptionFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

        internal Rectangle CaptionBounds
        {
            get
            {
                int right = UiStyle.Scale(this, CaptionInset);
                int left = UiStyle.Scale(this, Icon == UiIcon.None ? CaptionInset : UiStyle.Space40);
                return new Rectangle(left, 2, Math.Max(0, Width - left - right), Math.Max(0, Height - 4));
            }
        }

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
            if (interaction == null) return;
            if (Enabled && (spaceDown || pointerDown && hovering))
                interaction.Set(1F);
            else
                interaction.To(Enabled && hovering ? .55F : 0F, UiStyle.ButtonMotion);
        }

        protected override void Dispose(bool disposing) { if (disposing) { interaction.Dispose(); selection.Dispose(); } base.Dispose(disposing); }
        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            if (!Enabled) hovering = pointerDown = spaceDown = false;
            UpdateMotion();
        }
        protected override void OnMouseEnter(EventArgs e) { hovering = true; UpdateMotion(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovering = false; UpdateMotion(); base.OnMouseLeave(e); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool inside = ClientRectangle.Contains(e.Location);
            if (inside != hovering) { hovering = inside; UpdateMotion(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                hovering = ClientRectangle.Contains(e.Location);
                pointerDown = true;
                UpdateMotion();
            }
            base.OnMouseDown(e);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                hovering = ClientRectangle.Contains(e.Location);
                pointerDown = false;
                UpdateMotion();
            }
            base.OnMouseUp(e);
        }
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture && pointerDown) { pointerDown = false; UpdateMotion(); }
        }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space && !spaceDown) { spaceDown = true; UpdateMotion(); } base.OnKeyDown(e); }
        protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { spaceDown = false; UpdateMotion(); } base.OnKeyUp(e); }
        protected override void OnLostFocus(EventArgs e) { pointerDown = spaceDown = false; UpdateMotion(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color background = UiStyle.ParentSurface(this);
            e.Graphics.Clear(background);
            if (Width < 2 || Height < 2) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = UiStyle.Dpi(this) / 96F;
            float radius = Math.Min(CornerRadius * scale, (Math.Min(Width, Height) - 1) / 2f);
            var box = new RectangleF(.5f, .5f, Width - 1, Height - 1);
            Color hover = FlatAppearance.MouseOverBackColor.A == 0 ? BackColor : FlatAppearance.MouseOverBackColor;
            Color down = FlatAppearance.MouseDownBackColor.A == 0 ? hover : FlatAppearance.MouseDownBackColor;
            float amount = interaction.Current;
            Color fill = !Enabled ? AppTheme.Background(AppTheme.Page) :
                amount <= .55F ? Motion.Blend(BackColor, hover, amount / .55F) : Motion.Blend(hover, down, (amount - .55F) / .45F);
            bool navigation=Icon==UiIcon.Home||Icon==UiIcon.Network||Icon==UiIcon.History||Icon==UiIcon.Settings;
            if ((navigation || HorizontalIndicator) && Enabled)
                fill = Motion.Blend(Motion.Blend(background, AppTheme.Background(AppTheme.Pill), selection.Current), AppTheme.Hover, .6F * amount);
            if (TextOnly && Enabled && amount > 0)
                fill = Motion.Blend(background, AppTheme.Hover, Math.Min(1F, amount / .55F));
            if (!TextOnly || Enabled && amount > 0)
            {
                using (var path = UiStyle.RoundRect(box, radius))
                using (var brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
            }
            var bounds = CaptionBounds;
            Color textColor = !Enabled ? AppTheme.Text(AppTheme.Disabled) :
                navigation || HorizontalIndicator
                    ? Motion.Blend(AppTheme.Text(AppTheme.Muted), AppTheme.Text(AppTheme.Accent), selection.Current)
                    : ForeColor;
            var alignment = TextOnly ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter;
            if (Icon != UiIcon.None)
            {
                float size = UiStyle.Space20 * scale;
                float left = IconOnly ? (Width - size) / 2 : UiStyle.Space12 * scale;
                UiStyle.DrawIcon(e.Graphics, Icon, new RectangleF(left, (Height - size) / 2, size, size), Motion.Blend(fill, textColor, IconOpacity));
                alignment = TextFormatFlags.Left;
            }
            if (!IconOnly)
                TextRenderer.DrawText(e.Graphics, Text, Font, bounds, Motion.Blend(fill, textColor, CaptionOpacity),
                    alignment | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | CaptionFlags);
            if (HorizontalIndicator && selection.Current > 0 && Width > 8 && Height > 8)
            {
                using (var marker = new SolidBrush(AppTheme.Background(AppTheme.Accent)))
                {
                    int markerWidth = Math.Max(1, (int)(Width * .64F * selection.Current));
                    int markerHeight = Math.Max(2, (int)(2 * scale));
                    e.Graphics.FillRectangle(marker, (Width - markerWidth) / 2, Height - markerHeight, markerWidth, markerHeight);
                }
            }
            if (Focused && ShowFocusCues)
                using (var outline = UiStyle.RoundRect(RectangleF.Inflate(box, -1, -1), radius))
                using (var focus = new Pen(AppTheme.Focus, Math.Max(1F, scale)))
                    e.Graphics.DrawPath(focus, outline);
        }
    }
}
