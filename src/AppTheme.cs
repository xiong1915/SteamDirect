using System;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SteamDirect
{
    internal static class AppTheme
    {
        internal static bool IsDark;
        internal static readonly Color Page = Color.FromArgb(247, 245, 250);
        internal static readonly Color Ink = Color.FromArgb(32, 29, 39);
        internal static readonly Color Muted = Color.FromArgb(99, 94, 111);
        internal static readonly Color Accent = Color.FromArgb(103, 80, 164);
        internal static readonly Color Pill = Color.FromArgb(233, 224, 249);
        internal static readonly Color Surface = Color.White;
        internal static readonly Color Success = Color.FromArgb(24, 137, 110);
        internal static readonly Color Warning = Color.FromArgb(184, 102, 24);
        internal static readonly Color Error = Color.FromArgb(191, 72, 57);
        internal static readonly Color Info = Accent;
        internal static readonly Color Disabled = Color.FromArgb(145, 145, 145);
        internal static Color ThemeColor = Accent;
        private static Color primary, accentText, tone, primaryText;
        private static Color canvas, surface, mutedText, inkText, successText, warningText, errorText;
        internal static Color AccentForeground { get { return primaryText; } }
        internal static Color Edge { get { return Blend(surface, inkText, .15); } }
        private static Color Hover { get { return Blend(tone, accentText, .12); } }

        internal static bool TryColor(string value, out Color color)
        {
            color = Accent;
            int rgb;
            if (value == null || value.Length != 7 || value[0] != '#') return false;
            for (int i = 1; i < value.Length; i++) if (!Uri.IsHexDigit(value[i])) return false;
            if (!int.TryParse(value.Substring(1), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out rgb)) return false;
            color = Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
            return true;
        }

        internal static string ColorCode(Color color) { return "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2"); }

        private static Color Blend(Color a, Color b, double amount)
        {
            return Color.FromArgb((int)Math.Round(a.R + (b.R - a.R) * amount),
                (int)Math.Round(a.G + (b.G - a.G) * amount), (int)Math.Round(a.B + (b.B - a.B) * amount));
        }

        private static double Luminance(Color color)
        {
            return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        }

        private static double Linear(byte channel)
        {
            double value = channel / 255.0;
            return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }

        internal static double Contrast(Color a, Color b)
        {
            double x = Luminance(a), y = Luminance(b);
            return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
        }

        private static void UpdatePalette()
        {
            primary = ThemeColor;
            canvas = IsDark ? Color.FromArgb(16, 17, 20) : Color.FromArgb(247, 248, 250);
            surface = IsDark ? Color.FromArgb(26, 28, 32) : Color.White;
            inkText = IsDark ? Color.FromArgb(236, 236, 236) : Color.FromArgb(30, 30, 30);
            mutedText = IsDark ? Color.FromArgb(180, 184, 192) : Color.FromArgb(96, 96, 96);
            Color textSurface = IsDark ? surface : canvas;
            Color limit = IsDark ? Color.White : Color.Black;
            accentText = primary;
            for (int i = 0; i < 24 && Contrast(accentText, textSurface) < 4.5; i++)
                accentText = Blend(accentText, limit, .12);
            tone = IsDark ? Color.FromArgb(38, 40, 46) : Blend(Color.White, accentText, .14);
            for (int i = 0; i < 24 && Contrast(accentText, tone) < 4.5; i++)
            {
                accentText = Blend(accentText, limit, .12);
                tone = IsDark ? Color.FromArgb(38, 40, 46) : Blend(Color.White, accentText, .14);
            }
            primaryText = Contrast(primary, Color.White) >= Contrast(primary, Color.Black) ? Color.White : Color.Black;
            successText = Readable(IsDark ? Blend(Success, Color.White, .4) : Success, textSurface);
            warningText = Readable(IsDark ? Blend(Warning, Color.White, .45) : Warning, textSurface);
            errorText = Readable(IsDark ? Blend(Error, Color.White, .4) : Error, textSurface);
        }

        private static Color Readable(Color color, Color background)
        {
            Color target = IsDark ? Color.White : Color.Black;
            for (int i = 0; i < 24 && Contrast(color, background) < 4.5; i++) color = Blend(color, target, .12);
            return color;
        }

        static AppTheme() { UpdatePalette(); }
        private sealed class Original
        {
            internal Color Back, Fore;
            internal FlatStyle ButtonStyle;
            internal bool VisualStyle;
            internal Original(Control control)
            {
                Back = control.BackColor; Fore = control.ForeColor;
                var button = control as ButtonBase;
                if (button != null) { ButtonStyle = button.FlatStyle; VisualStyle = button.UseVisualStyleBackColor; }
            }
        }
        private static readonly ConditionalWeakTable<Control, Original> originals = new ConditionalWeakTable<Control, Original>();
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        internal static Color Text(Color light)
        {
            bool accent = light.ToArgb() == Accent.ToArgb();
            if (accent) return accentText;
            if (light.ToArgb() == Color.White.ToArgb()) return light;
            if (light.ToArgb() == Success.ToArgb()) return successText;
            if (light.ToArgb() == Warning.ToArgb()) return warningText;
            if (light.ToArgb() == Error.ToArgb()) return errorText;
            if (light.ToArgb() == Disabled.ToArgb() || light.ToArgb() == SystemColors.GrayText.ToArgb()) return mutedText;
            if (light.ToArgb() == Muted.ToArgb()) return mutedText;
            return inkText;
        }
        internal static Color Background(Color light)
        {
            if (light.A == 0) return light;
            if (light.ToArgb() == Pill.ToArgb()) return tone;
            if (light.ToArgb() == Accent.ToArgb()) return primary;
            bool page = light.ToArgb() == Page.ToArgb();
            if (page) return canvas;
            if (light.ToArgb() == Color.White.ToArgb() || light.IsSystemColor) return surface;
            return light;
        }
        internal static void SetForeground(Control control, Color light)
        {
            var original = originals.GetValue(control, c => new Original(c));
            original.Fore = light;
            control.ForeColor = Text(light);
        }
        internal static void SetBackground(Control control, Color light)
        {
            var original = originals.GetValue(control, c => new Original(c));
            original.Back = light;
            control.BackColor = Background(light);
        }
        internal static void ActionButton(Button button, bool primary)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.UseVisualStyleBackColor = false;
            button.BackColor = primary ? Accent : Pill;
            button.ForeColor = primary ? Color.White : Accent;
            button.FlatAppearance.BorderSize = 0;
            button.Cursor = Cursors.Hand;
            button.Font = UiStyle.Button;
        }

        internal static void ApplyTray(ContextMenuStrip menu)
        {
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = false;
            menu.Font = UiStyle.Body;
            menu.Padding = new Padding(6);
            menu.Renderer = new RoundedMenuRenderer();
            menu.BackColor = Background(Color.White);
            foreach (ToolStripItem item in menu.Items)
            {
                item.ForeColor = Text(Ink);
                item.Padding = new Padding(12, 7, 12, 7);
            }
            menu.SizeChanged -= RoundMenu;
            menu.SizeChanged += RoundMenu;
            RoundMenu(menu, EventArgs.Empty);
        }

        private static void RoundMenu(object sender, EventArgs args)
        {
            var menu = (ContextMenuStrip)sender;
            if (menu.Width < 2 || menu.Height < 2) return;
            var old = menu.Region;
            using (var path = UiStyle.RoundRect(new RectangleF(0, 0, menu.Width, menu.Height), UiStyle.ControlRadius))
                menu.Region = new Region(path);
            if (old != null) old.Dispose();
        }

        private sealed class RoundedMenuRenderer : ToolStripProfessionalRenderer
        {
            internal RoundedMenuRenderer() : base(new MenuColors()) { RoundedEdges = false; }
            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var path = UiStyle.RoundRect(new RectangleF(.5F, .5F, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1), UiStyle.ControlRadius))
                using (var pen = new Pen(Edge)) e.Graphics.DrawPath(pen, path);
            }
            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Selected) return;
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var path = UiStyle.RoundRect(new RectangleF(1, 1, e.Item.Width - 2, e.Item.Height - 2), UiStyle.ControlRadius - 2))
                using (var brush = new SolidBrush(Hover)) e.Graphics.FillPath(brush, path);
            }
        }

        private sealed class MenuColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Background(Color.White); } }
            public override Color MenuItemSelected { get { return Hover; } }
            public override Color MenuItemBorder { get { return Edge; } }
            public override Color MenuBorder { get { return Edge; } }
            public override Color SeparatorDark { get { return Edge; } }
            public override Color SeparatorLight { get { return ToolStripDropDownBackground; } }
        }
        private static void Capture(Control control)
        {
            originals.GetValue(control, c => new Original(c));
            foreach (Control child in control.Controls) Capture(child);
        }
        private static void Paint(Control control)
        {
            var original = originals.GetValue(control, c => new Original(c));
            control.BackColor = Background(original.Back);
            control.ForeColor = Text(original.Fore);
            var grid = control as DataGridView;
            if (grid != null)
            {
                grid.BackgroundColor = Background(Color.White);
                grid.GridColor = Edge;
                grid.EnableHeadersVisualStyles = false;
                grid.ColumnHeadersDefaultCellStyle.BackColor = Background(Page);
                grid.ColumnHeadersDefaultCellStyle.ForeColor = Text(Muted);
                grid.DefaultCellStyle.BackColor = Background(Color.White);
                grid.DefaultCellStyle.ForeColor = Text(Ink);
                grid.DefaultCellStyle.SelectionBackColor = Background(Pill);
                grid.DefaultCellStyle.SelectionForeColor = Text(Ink);
            }
            var button = control as ButtonBase;
            if (button != null)
            {
                button.FlatStyle = IsDark ? FlatStyle.Flat : original.ButtonStyle;
                button.UseVisualStyleBackColor = IsDark ? false : original.VisualStyle;
            }
            var push = control as Button;
            if (push != null)
            {
                bool primary = original.Back.ToArgb() == Accent.ToArgb();
                if (primary)
                {
                    push.BackColor = AppTheme.primary;
                    push.ForeColor = primaryText;
                }
                push.FlatAppearance.BorderColor = Edge;
                Color awayFromText = primaryText == Color.White ? Color.Black : Color.White;
                push.FlatAppearance.MouseOverBackColor = primary ? Blend(AppTheme.primary, awayFromText, .08) : Hover;
                push.FlatAppearance.MouseDownBackColor = primary ? Blend(AppTheme.primary, awayFromText, .15) : Edge;
                push.Paint -= PaintDisabledButton; push.Paint += PaintDisabledButton;
            }
            foreach (Control child in control.Controls) Paint(child);
            control.Invalidate();
        }
        private static void PaintDisabledButton(object sender, PaintEventArgs args)
        {
            var button = (Button)sender;
            if (button.Enabled) return;
            using (var brush = new SolidBrush(Background(Page))) args.Graphics.FillRectangle(brush, button.ClientRectangle);
            if (button.FlatAppearance.BorderSize > 0)
                using (var pen = new Pen(Edge)) args.Graphics.DrawRectangle(pen, 0, 0, button.Width - 1, button.Height - 1);
            TextRenderer.DrawText(args.Graphics, button.Text, button.Font, button.ClientRectangle, Text(Muted), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
        internal static void Apply(Control control)
        {
            UpdatePalette();
            Capture(control); Paint(control);
            var window = control as Form;
            if (window != null)
            {
                window.HandleCreated -= OnHandleCreated;
                window.HandleCreated += OnHandleCreated;
                if (window.IsHandleCreated) SetTitle(window);
            }
        }
        private static void OnHandleCreated(object sender, EventArgs args) { SetTitle((Form)sender); }
        private static void SetTitle(Form window)
        {
            int value = IsDark ? 1 : 0;
            try
            {
                DwmSetWindowAttribute(window.Handle, 20, ref value, 4);
                int corner = 2;
                DwmSetWindowAttribute(window.Handle, 33, ref corner, 4);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }
        internal static void SetTheme(bool dark, string colorCode)
        {
            Color color;
            if (!TryColor(colorCode, out color)) throw new ArgumentException("主题色格式应为 #RRGGBB。", "colorCode");
            ThemeColor = color;
            IsDark = dark;
            UpdatePalette();
            foreach (Form window in Application.OpenForms) Apply(window);
        }
    }
}
