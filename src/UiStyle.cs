using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SteamDirect
{
    internal enum UiIcon { None, Home, Network, History, Settings, External, Sidebar }

    internal sealed class FluentTooltip : ToolTip
    {
        internal FluentTooltip()
        {
            InitialDelay = 400;
            ReshowDelay = 150;
            OwnerDraw = true;
            Popup += (sender, args) =>
            {
                string text = GetToolTip(args.AssociatedControl);
                if (string.IsNullOrWhiteSpace(text)) return;
                Size size = TextRenderer.MeasureText(text, UiStyle.Caption);
                args.ToolTipSize = size + new Size(UiStyle.Scale(args.AssociatedControl, UiStyle.Space16),
                    UiStyle.Scale(args.AssociatedControl, UiStyle.Space12));
            };
            Draw += (sender, args) =>
            {
                using (var background = new SolidBrush(AppTheme.Background(AppTheme.ElevatedSurface)))
                    args.Graphics.FillRectangle(background, args.Bounds);
                using (var border = new Pen(AppTheme.Edge))
                    args.Graphics.DrawRectangle(border, args.Bounds.X, args.Bounds.Y, args.Bounds.Width - 1, args.Bounds.Height - 1);
                TextRenderer.DrawText(args.Graphics, args.ToolTipText, UiStyle.Caption, args.Bounds, AppTheme.Text(AppTheme.Ink),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            };
        }
    }

    // Explicit content bounds avoid AutoScroll/Dock feedback and preserve readable minimum heights.
    internal sealed class ContentViewport : Panel
    {
        private readonly Control content;
        private int minimumHeight;
        private readonly DarkScrollBar scroll = new DarkScrollBar();
        private bool arranging;
        internal ContentViewport(Control content, int minimumHeight)
        {
            this.content = content; this.minimumHeight = minimumHeight;
            Dock = DockStyle.Fill; Margin = Padding.Empty; BackColor = content.BackColor; DoubleBuffered = true;
            content.Dock = DockStyle.None; Controls.Add(content); Controls.Add(scroll);
            scroll.ScrollTo += ScrollTo;
        }
        internal void ScrollTo(int value)
        {
            scroll.SetRange(Math.Max(0, content.Height - Height), Math.Max(1, Height), value);
            if (content.Top != -scroll.Value) content.Top = -scroll.Value;
        }
        internal void SetMinimumHeight(int height)
        {
            if (minimumHeight == height) return;
            minimumHeight = height;
            PerformLayout();
        }
        internal void RevealTop(Control target, int height)
        {
            int visibleHeight = Math.Min(ClientSize.Height, Math.Min(target.Height, height));
            if (visibleHeight <= 0) return;
            int top = PointToClient(target.PointToScreen(Point.Empty)).Y;
            int shift = top < 0 ? top : Math.Max(0, top + visibleHeight - ClientSize.Height);
            if (shift != 0) ScrollTo(scroll.Value + shift);
        }
        protected override void OnLayout(LayoutEventArgs args)
        {
            if (arranging || content == null) return;
            arranging = true;
            try {
                base.OnLayout(args);
                int height = Math.Max(Height, UiStyle.Scale(this, minimumHeight));
                int width = height > Height ? UiStyle.Scale(this, 14) : 0;
                scroll.SetRange(Math.Max(0, height - Height), Math.Max(1, Height), scroll.Value);
                scroll.Bounds = new Rectangle(Math.Max(0, Width - width), 0, width, Height);
                int availableWidth = Math.Max(1, Width - width);
                content.Bounds = new Rectangle(0, -scroll.Value, availableWidth, height);
            } finally { arranging = false; }
        }
        protected override void OnMouseWheel(MouseEventArgs args)
        {
            base.OnMouseWheel(args); ScrollTo(scroll.Value - Math.Sign(args.Delta) * UiStyle.Scale(this, 40));
        }
    }

    internal class SectionRule : Panel
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(AppTheme.Divider))
                e.Graphics.DrawLine(pen, 0, 0, Width, 0);
        }
    }

    internal static class UiStyle
    {
        internal const int Space4 = 4, Space8 = 8, Space12 = 12, Space16 = 16, Space20 = 20,
            Space24 = 24, Space32 = 32, Space40 = 40, Space48 = 48;
        internal const int HeaderHeight = 80, ButtonHeight = 40, CompactHeight = 32, Gap = Space16, SmallGap = Space8;
        internal const int LargeButtonHeight = 44, AppHeaderHeight = Space48, StatusBarHeight = Space32,
            SidebarWidth = 232, CollapsedSidebarWidth = 64, MinimumWindowWidth = 1100, MinimumWindowHeight = 700,
            CompactWindowWidth = 880, CompactWindowHeight = 540, ExpandedSidebarBreakpoint = 1120;
        internal const int ButtonMotion = 100, StatusMotion = 180, PageMotion = 220;
        internal const float SmallRadius = 6, ControlRadius = 8, CardRadius = 12, DialogRadius = 16, NavigationRadius = ControlRadius;
        internal const int SpinnerPeriodMs = 1600; // Continuous motion is reserved for active operations; controls animate only on interaction.
        private static readonly string Family = PickFont("Segoe UI Variable Text", "Segoe UI");
        private static readonly string Semibold = PickFont("Segoe UI Variable Text Semibold", "Segoe UI Semibold");
        internal static readonly Font Body = FontAt(14);
        internal static readonly Font BodyStrong = FontAt(14, true);
        internal static readonly Font Caption = FontAt(12);
        internal static readonly Font CaptionStrong = FontAt(12, true);
        internal static readonly Font Button = BodyStrong;
        internal static readonly Font PageTitle = FontAt(24, true);
        internal static readonly Font LeadTitle = FontAt(28, true);
        internal static readonly Font SectionTitle = FontAt(18, true);
        internal static readonly Font HeroTitle = SectionTitle;
        internal static readonly Font Subtitle = FontAt(16, true);
        internal static readonly Font DiagramCaption = new Font(Family, 13.5F, FontStyle.Regular, GraphicsUnit.Pixel);
        internal static readonly Font DiagramTitle = new Font(Family, 15.5F, FontStyle.Bold, GraphicsUnit.Pixel);
        internal static Padding PagePadding { get { return new Padding(Space24); } }
        internal static Padding CardPadding { get { return new Padding(Space20); } }
        internal static Padding DensePadding { get { return new Padding(Space16); } }

        private static Font FontAt(int logicalPixels, bool semibold = false)
        {
            return new Font(semibold ? Semibold : Family, logicalPixels * .75F);
        }

        private static string PickFont(params string[] names)
        {
            foreach (string name in names)
                using (var font = new Font(name, 10F))
                    if (string.Equals(font.Name, name, StringComparison.OrdinalIgnoreCase)) return name;
            return "Microsoft YaHei UI";
        }

        private static int NativeDpi(Control control) { Control root = control; while (root.Parent != null) root = root.Parent; return root.DeviceDpi; }
        internal static int Dpi(Control control) { return WindowEnvironment.OffscreenTests && WindowEnvironment.LayoutDpi > 0 ? WindowEnvironment.LayoutDpi : NativeDpi(control); }
        internal static Color ParentSurface(Control control)
        {
            for (Control parent = control.Parent; parent != null; parent = parent.Parent)
                if (parent.BackColor.A == 255) return parent.BackColor;
            return AppTheme.Background(AppTheme.Page);
        }

        internal static int Scale(Control control, int value) { return (int)Math.Round(value * Dpi(control) / 96F); }
        internal static int TextHeight(Control control) { return (int)Math.Ceiling(control.Font.GetHeight(NativeDpi(control))); }
        internal static TableLayoutPanel Grid(int columns, int rows)
        {
            return new BufferedGrid { Dock = DockStyle.Fill, ColumnCount = columns, RowCount = rows, Margin = Padding.Empty };
        }

        private sealed class BufferedGrid : TableLayoutPanel
        {
            internal BufferedGrid() { DoubleBuffered = true; }
            protected override void OnLayout(LayoutEventArgs args)
            {
                var trace = NavigationTrace.Current;
                if (trace != null) trace.BeginLayout();
                try { base.OnLayout(args); } finally { if (trace != null) trace.EndLayout(); }
            }
        }

        internal sealed class PageTitleLabel : Label
        {
            private readonly Motion.Value reveal;

            internal PageTitleLabel()
            {
                DoubleBuffered = true;
                reveal = new Motion.Value(this, 1F);
            }

            protected override void OnVisibleChanged(EventArgs e)
            {
                base.OnVisibleChanged(e);
                if (reveal == null || !Visible || !IsHandleCreated || !Motion.Allowed) return;
                var form = FindForm();
                var main = TopLevelControl as MainWindow;
                if (form is HistoryWindow || (main != null && main.IsPreparingPresentation)) return;
                reveal.Set(0F);
                reveal.To(1F, PageMotion);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                if (reveal == null || reveal.Current >= 1F) { base.OnPaint(e); return; }
                var bounds = ClientRectangle;
                int offset = (int)Math.Round(UiStyle.Scale(this, Space8) * (1F - reveal.Current));
                bounds.X += offset;
                bounds.Width = Math.Max(0, bounds.Width - offset);
                TextRenderer.DrawText(e.Graphics, Text, Font, bounds,
                    Motion.Blend(ParentSurface(this), ForeColor, .45F + .55F * reveal.Current),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && reveal != null) reveal.Dispose();
                base.Dispose(disposing);
            }
        }

        internal static Control Header(string title, string description)
        {
            var header = Grid(1, 2);
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, string.IsNullOrEmpty(description)?100:64));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, string.IsNullOrEmpty(description)?0:36));
            header.Controls.Add(new PageTitleLabel { Text = title, Dock = DockStyle.Fill, Margin = Padding.Empty,
                Font = PageTitle, ForeColor = AppTheme.Ink, AutoEllipsis = true, TextAlign=ContentAlignment.MiddleLeft }, 0, 0);
            header.Controls.Add(new Label { Text = description, Dock = DockStyle.Fill, Margin = Padding.Empty,
                Font = Caption, ForeColor = AppTheme.Muted, AutoEllipsis = true }, 0, 1);
            header.Margin = new Padding(0, 0, 0, Space12);
            return header;
        }

        internal static RoundedPanel Card(bool compact = false)
        {
            return new RoundedPanel { Dock = DockStyle.Fill, BackColor = AppTheme.Surface,
                Padding = compact ? DensePadding : CardPadding, Margin = Padding.Empty };
        }

        internal static Control Metric(string title, UiIcon icon, out Label value, out Label note, bool includeNote = true)
        {
            int column = icon == UiIcon.None ? 0 : 1;
            var grid = Grid(column + 1, includeNote ? 3 : 2);
            if (column != 0)
            {
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Space32));
                var symbol = new PictureBox { Dock = DockStyle.Fill, Margin = Padding.Empty, TabStop = false };
                symbol.Paint += (sender, args) =>
                {
                    int size = Scale(symbol, Space20);
                    DrawIcon(args.Graphics, icon, new RectangleF(0, (symbol.Height - size) / 2F, size, size), AppTheme.Text(AppTheme.Muted));
                };
                grid.Controls.Add(symbol, 0, 0);
                grid.SetRowSpan(symbol, 2);
            }
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, Space20));
            grid.RowStyles.Add(new RowStyle(includeNote ? SizeType.Absolute : SizeType.Percent, includeNote ? Space24 : 100));
            if (includeNote) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.Controls.Add(MetricLabel(title, Caption, AppTheme.Muted), column, 0);
            value = MetricLabel("", Subtitle, AppTheme.Ink);
            value.AccessibleName = title + "状态";
            grid.Controls.Add(value, column, 1);
            note = includeNote ? MetricLabel("", Caption, AppTheme.Muted) : null;
            if (note != null) grid.Controls.Add(note, column, 2);
            return grid;
        }

        private static Label MetricLabel(string text, Font font, Color color)
        {
            return new Label { Text = text, Font = font, ForeColor = color, Dock = DockStyle.Fill,
                Margin = Padding.Empty, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
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

        internal static void DrawIcon(Graphics graphics, UiIcon icon, RectangleF bounds, Color color, float sidebarProgress = 0F)
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
                    case UiIcon.Sidebar:
                        graphics.DrawRectangle(pen, 3, 4, 14, 12);
                        float divider = 8F + 4F * Math.Max(0F, Math.Min(1F, sidebarProgress));
                        graphics.DrawLine(pen, divider, 4, divider, 16);
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
        private readonly Motion.Value focus;
        internal EntryFrame(Control input, string accessibleName)
        {
            if (input == null) throw new ArgumentNullException("input");
            this.input = input;
            focus = new Motion.Value(this, 0F);
            Dock = DockStyle.Fill;
            Padding = new Padding(UiStyle.Space12, UiStyle.Space8, UiStyle.Space12, UiStyle.Space8);
            Margin = Padding.Empty;
            BackColor = AppTheme.InputSurface;
            CornerRadius = UiStyle.ControlRadius;
            input.AccessibleName = accessibleName;
            input.Font = UiStyle.Body;
            input.BackColor = AppTheme.InputSurface;
            input.ForeColor = AppTheme.Ink;
            input.Dock = DockStyle.None;
            input.Margin = Padding.Empty;
            input.FontChanged += (sender, args) => PerformLayout();
            var text = input as TextBox;
            if (text != null) text.BorderStyle = BorderStyle.None;
            var number = input as NumericUpDown;
            if (number != null) number.BorderStyle = BorderStyle.None;
            input.Enter += (sender, args) => focus.SpringTo(1F, UiStyle.StatusMotion);
            input.Leave += (sender, args) => focus.SpringTo(0F, UiStyle.StatusMotion);
            Controls.Add(input);
        }
        protected override void Dispose(bool disposing) { if (disposing && focus != null) focus.Dispose(); base.Dispose(disposing); }
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
            using (var path = UiStyle.RoundRect(new RectangleF(.5F, .5F, Width - 1, Height - 1), CornerRadius * UiStyle.Dpi(this) / 96F))
            using (var pen = new Pen(Motion.Blend(AppTheme.Edge, AppTheme.Focus, focus.Current)))
                e.Graphics.DrawPath(pen, path);
        }
    }

    internal sealed class ThemedProgressBar : ProgressBar
    {
        private readonly Motion.Value amount;
        private float target;
        internal ThemedProgressBar()
        {
            amount = new Motion.Value(this, 0F);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void Dispose(bool disposing) { if (disposing && amount != null) amount.Dispose(); base.Dispose(disposing); }
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
            if (portion != target) { target = portion; amount.To(portion, 240); }
            float width = Width * amount.Current;
            if (width < 1) return;
            using (var fill = UiStyle.RoundRect(new RectangleF(0, 0, width, Height), Height / 2F))
            using (var brush = new SolidBrush(AppTheme.Background(AppTheme.Accent)))
                e.Graphics.FillPath(brush, fill);
        }
    }
}
