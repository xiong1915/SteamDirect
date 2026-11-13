using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SteamDirect
{
    internal enum UiIcon { None, Home, Network, History, Settings, External }

    internal static class UiStyle
    {
        internal const int Space4 = 4, Space8 = 8, Space12 = 12, Space16 = 16, Space20 = 20,
            Space24 = 24, Space32 = 32, Space40 = 40, Space48 = 48;
        internal const int HeaderHeight = 96, ButtonHeight = 40, CompactHeight = 32, Gap = Space16, SmallGap = Space8;
        internal const float CardRadius = 14, ControlRadius = 10, NavigationRadius = 10;
        internal const int MotionTick = 40, SpinnerStep = 9; // Continuous motion is reserved for active operations; controls animate only on interaction.
        private const string Family = "Microsoft YaHei UI";
        internal static readonly Font Body = new Font(Family, 10F);
        internal static readonly Font BodyStrong = new Font(Family, 10F, FontStyle.Bold);
        internal static readonly Font Caption = new Font(Family, 9F);
        internal static readonly Font Button = BodyStrong;
        internal static readonly Font PageTitle = new Font(Family, 22F);
        internal static readonly Font SectionTitle = new Font(Family, 11F, FontStyle.Bold);
        internal static readonly Font HeroTitle = new Font(Family, 17F, FontStyle.Bold);
        internal static readonly Font DiagramCaption = new Font(Family, 12F, FontStyle.Regular, GraphicsUnit.Pixel);
        internal static readonly Font DiagramTitle = new Font(Family, 13.5F, FontStyle.Bold, GraphicsUnit.Pixel);
        internal static Padding PagePadding { get { return new Padding(Space24); } }
        internal static Padding CardPadding { get { return new Padding(Space20); } }
        internal static Padding DensePadding { get { return new Padding(Space16); } }

        internal static int Scale(Control control, int value) { return (int)Math.Round(value * control.DeviceDpi / 96F); }
        internal static int TextHeight(Control control) { return (int)Math.Ceiling(control.Font.GetHeight(control.DeviceDpi)); }
        internal static TableLayoutPanel Grid(int columns, int rows)
        {
            return new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = columns, RowCount = rows, Margin = Padding.Empty };
        }

        internal static TableLayoutPanel Header(string title, string description)
        {
            var header = Grid(1, 2);
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, Space32 + Space24));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            header.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, Margin = Padding.Empty,
                Font = PageTitle, ForeColor = AppTheme.Ink, AutoEllipsis = true }, 0, 0);
            header.Controls.Add(new Label { Text = description, Dock = DockStyle.Fill, Margin = Padding.Empty,
                Font = Body, ForeColor = AppTheme.Muted, AutoEllipsis = true }, 0, 1);
            return header;
        }

        internal static RoundedPanel Card(bool compact = false)
        {
            return new RoundedPanel { Dock = DockStyle.Fill, BackColor = AppTheme.Surface,
                Padding = compact ? DensePadding : CardPadding, Margin = Padding.Empty };
        }

        internal static Control Entry(Control input, string accessibleName)
        {
            return new EntryFrame(input, accessibleName);
        }

        internal static void ReadOnlyText(TextBox text, string accessibleName)
        {
            text.AccessibleName = accessibleName;
            text.Dock = DockStyle.Fill;
            text.Font = Body;
            text.ReadOnly = text.Multiline = true;
            text.ScrollBars = ScrollBars.Vertical;
            text.BorderStyle = BorderStyle.None;
            text.BackColor = AppTheme.Surface;
            text.ForeColor = AppTheme.Ink;
        }

        internal static void SetState(Label title, Label detail, string heading, string message, bool error = false)
        {
            title.Text = heading;
            detail.Text = message;
            AppTheme.SetForeground(title, error ? AppTheme.Error : AppTheme.Ink);
            AppTheme.SetForeground(detail, AppTheme.Muted);
        }

        internal static void ConfigureChoice(ComboBox choice)
        {
            choice.FlatStyle = FlatStyle.Flat;
            choice.DrawMode = DrawMode.OwnerDrawFixed;
            choice.Font = Body;
            choice.DrawItem += (sender, args) =>
            {
                bool selected = (args.State & DrawItemState.Selected) != 0;
                using (var brush = new SolidBrush(AppTheme.Background(selected ? AppTheme.Pill : AppTheme.Surface)))
                    args.Graphics.FillRectangle(brush, args.Bounds);
                string text = args.Index < 0 ? choice.Text : choice.GetItemText(choice.Items[args.Index]);
                TextRenderer.DrawText(args.Graphics, text, choice.Font, Rectangle.Inflate(args.Bounds, -Space4, 0),
                    AppTheme.Text(AppTheme.Ink), TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                if ((args.State & DrawItemState.Focus) != 0) args.DrawFocusRectangle();
            };
            choice.FontChanged += (sender, args) => choice.ItemHeight = TextHeight(choice) + Scale(choice, Space8);
            choice.ItemHeight = TextHeight(choice) + Scale(choice, Space8);
        }

        internal static GraphicsPath RoundRect(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            if (diameter <= 0) return path;
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        internal static void DrawIcon(Graphics graphics, UiIcon icon, RectangleF bounds, Color color)
        {
            if (icon == UiIcon.None) return;
            var state = graphics.Save();
            graphics.TranslateTransform(bounds.X, bounds.Y);
            graphics.ScaleTransform(bounds.Width / 24F, bounds.Height / 24F);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(color, 1.7F) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                switch (icon)
                {
                    case UiIcon.Home:
                        graphics.DrawLines(pen, new[] { new PointF(3, 11), new PointF(12, 3), new PointF(21, 11) });
                        graphics.DrawLines(pen, new[] { new PointF(6, 10), new PointF(6, 21), new PointF(18, 21), new PointF(18, 10) });
                        break;
                    case UiIcon.Network:
                        graphics.DrawEllipse(pen, 3, 3, 18, 18);
                        graphics.DrawEllipse(pen, 8, 3, 8, 18);
                        graphics.DrawLine(pen, 3, 12, 21, 12);
                        break;
                    case UiIcon.History:
                        graphics.DrawArc(pen, 4, 4, 16, 16, -130, 310);
                        graphics.DrawLines(pen, new[] { new PointF(2, 4), new PointF(2, 9), new PointF(7, 9) });
                        graphics.DrawLines(pen, new[] { new PointF(12, 7), new PointF(12, 12), new PointF(16, 14) });
                        break;
                    case UiIcon.Settings:
                        for (int i = 0; i < 3; i++)
                        {
                            float y = 6 + i * 6, x = i == 1 ? 15 : 9;
                            graphics.DrawLine(pen, 3, y, x - 3, y);
                            graphics.DrawLine(pen, x + 3, y, 21, y);
                            graphics.DrawEllipse(pen, x - 3, y - 3, 6, 6);
                        }
                        break;
                    case UiIcon.External:
                        graphics.DrawLines(pen, new[] { new PointF(10, 4), new PointF(4, 4), new PointF(4, 20), new PointF(20, 20), new PointF(20, 14) });
                        graphics.DrawLines(pen, new[] { new PointF(14, 3), new PointF(21, 3), new PointF(21, 10) });
                        graphics.DrawLine(pen, 11, 13, 21, 3);
                        break;
                }
            }
            graphics.Restore(state);
        }
    }

    internal sealed class EntryFrame : RoundedPanel
    {
        private readonly Control input;
        internal EntryFrame(Control input, string accessibleName)
        {
            this.input = input;
            Dock = DockStyle.Fill;
            Padding = new Padding(UiStyle.Space12, UiStyle.Space8, UiStyle.Space12, UiStyle.Space8);
            Margin = Padding.Empty;
            BackColor = AppTheme.Surface;
            CornerRadius = UiStyle.ControlRadius;
            input.AccessibleName = accessibleName;
            input.Font = UiStyle.Body;
            input.BackColor = AppTheme.Surface;
            input.ForeColor = AppTheme.Ink;
            input.Dock = DockStyle.None;
            input.Margin = Padding.Empty;
            input.FontChanged += (sender, args) => PerformLayout();
            input.SizeChanged += (sender, args) => PerformLayout();
            var text = input as TextBox;
            if (text != null) text.BorderStyle = BorderStyle.None;
            var number = input as NumericUpDown;
            if (number != null) number.BorderStyle = BorderStyle.None;
            input.Enter += (sender, args) => Invalidate();
            input.Leave += (sender, args) => Invalidate();
            Controls.Add(input);
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (input == null) return;
            int height = input.PreferredSize.Height;
            input.Bounds = new Rectangle(Padding.Left, Math.Max(0, (Height - height) / 2),
                Math.Max(1, Width - Padding.Horizontal), height);
            // Owner-drawn native combo boxes can enforce a height greater than PreferredSize.
            input.Top = Math.Max(0, (Height - input.Height) / 2);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width < 2 || Height < 2) return;
            using (var path = UiStyle.RoundRect(new RectangleF(.5F, .5F, Width - 1, Height - 1), CornerRadius * DeviceDpi / 96F))
            using (var pen = new Pen(input.ContainsFocus ? AppTheme.Text(AppTheme.Accent) : AppTheme.Edge))
                e.Graphics.DrawPath(pen, path);
        }
    }

    internal sealed class ThemedProgressBar : ProgressBar
    {
        internal ThemedProgressBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? AppTheme.Background(AppTheme.Page) : Parent.BackColor);
            if (Width < 2 || Height < 2) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var area = new RectangleF(0, 0, Width, Height);
            using (var track = UiStyle.RoundRect(area, Height / 2F))
            using (var brush = new SolidBrush(AppTheme.Edge))
                e.Graphics.FillPath(brush, track);
            float portion = (Value - Minimum) / (float)Math.Max(1, Maximum - Minimum);
            float width = Width * portion;
            if (width < 1) return;
            using (var fill = UiStyle.RoundRect(new RectangleF(0, 0, width, Height), Height / 2F))
            using (var brush = new SolidBrush(AppTheme.Background(AppTheme.Accent)))
                e.Graphics.FillPath(brush, fill);
        }
    }
}
