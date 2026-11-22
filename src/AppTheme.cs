using System;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SteamDirect
{
    internal static class AppTheme
    {
        internal const string DefaultAccent = "#D1D5DB";
        private static readonly Color DefaultAccentColor = Color.FromArgb(0xD1, 0xD5, 0xDB);
        internal static bool IsDark = true;
        internal static readonly Color Page = Color.FromArgb(246, 247, 249);
        internal static readonly Color Ink = Color.FromArgb(29, 33, 41);
        internal static readonly Color Muted = Color.FromArgb(99, 108, 122);
        internal static readonly Color Accent = DefaultAccentColor;
        internal static readonly Color Pill = Color.FromArgb(234, 235, 240);
        internal static readonly Color Surface = Color.White;
        internal static readonly Color InputSurface = Color.FromArgb(242, 244, 247);
        internal static readonly Color ElevatedSurface = Color.FromArgb(236, 239, 243);
        internal static readonly Color Rail = Color.FromArgb(231, 234, 239);
        internal static readonly Color Success = Color.FromArgb(24, 137, 110);
        internal static readonly Color Warning = Color.FromArgb(184, 102, 24);
        internal static readonly Color Error = Color.FromArgb(191, 72, 57);
        internal static readonly Color Info = Muted;
        internal static readonly Color Disabled = Color.FromArgb(145, 145, 145);
        internal static Color ThemeColor = DefaultAccentColor;
        private static Color primary, accentText, tone, primaryText;
        private static Color canvas, surface, mutedText, inkText, successText, warningText, errorText;
        private static readonly Color DarkRail = Color.FromArgb(0x15, 0x19, 0x1F);
        private static readonly Color DarkElevated = Color.FromArgb(0x22, 0x28, 0x32);
        private static readonly Color DarkSelected = Color.FromArgb(0x30, 0x36, 0x40);
        private static readonly Color DarkHover = Color.FromArgb(0x29, 0x2F, 0x39);
        private static readonly Color DarkBorderSubtle = Color.FromArgb(0x26, 0x2C, 0x35);
        private static readonly Color AccentHover = Color.FromArgb(0xF3, 0xF4, 0xF6);
        private static readonly Color AccentPressed = Color.FromArgb(0xB8, 0xBE, 0xC8);
        private static Color focus;
        internal static readonly Color Subtle = Color.FromArgb(109, 117, 130);
        internal static Color AccentForeground { get { return primaryText; } }
        internal static Color Edge { get { return IsDark ? DarkSelected : Blend(surface, inkText, .13); } }
        internal static Color Divider { get { return IsDark ? DarkBorderSubtle : Edge; } }
        internal static Color Focus { get { return focus; } }
        internal static Color Hover { get { return IsDark ? DarkHover : Blend(tone, accentText, .12); } }

        internal static bool TryColor(string value, out Color color)
        {
            color = DefaultAccentColor;
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
            primary = DefaultAccentColor;
            canvas = IsDark ? Color.FromArgb(0x10, 0x12, 0x16) : Page;
            surface = IsDark ? Color.FromArgb(0x1A, 0x1F, 0x27) : Color.White;
            inkText = IsDark ? AccentHover : Color.FromArgb(28, 30, 37);
            mutedText = IsDark ? Color.FromArgb(0xA5, 0xAB, 0xB4) : Color.FromArgb(83, 89, 99);
            Color textSurface = IsDark ? surface : canvas;
            Color limit = IsDark ? Color.White : Color.Black;
            accentText = primary;
            for (int i = 0; i < 24 && Contrast(accentText, textSurface) < 4.5; i++)
                accentText = Blend(accentText, limit, .12);
            tone = IsDark ? DarkSelected : Blend(Color.White, accentText, .08);
            for (int i = 0; i < 24 && Contrast(accentText, tone) < 4.5; i++)
            {
                accentText = Blend(accentText, limit, .12);
                if (!IsDark) tone = Blend(Color.White, accentText, .08);
            }
            primaryText = Color.FromArgb(0x17, 0x1A, 0x20);
            // A saved custom color controls focus intensity only. Action fills and text remain neutral.
            int gray = (int)Math.Round(.2126 * ThemeColor.R + .7152 * ThemeColor.G + .0722 * ThemeColor.B);
            focus = ThemeColor.ToArgb() == DefaultAccentColor.ToArgb()
                ? mutedText : Readable(Color.FromArgb(gray, gray, gray), textSurface);
            successText = Readable(IsDark ? Color.FromArgb(0x4C, 0xAF, 0x79) : Success, textSurface);
            warningText = Readable(IsDark ? Color.FromArgb(0xD9, 0xA4, 0x41) : Warning, textSurface);
            errorText = Readable(IsDark ? Color.FromArgb(0xD9, 0x6B, 0x6B) : Error, textSurface);
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
            if (light.ToArgb() == Disabled.ToArgb() || light.ToArgb() == SystemColors.GrayText.ToArgb())
                return IsDark ? Color.FromArgb(0x59, 0x61, 0x6D) : Subtle;
            if (light.ToArgb() == Subtle.ToArgb()) return IsDark ? Color.FromArgb(0x7B, 0x84, 0x90) : Subtle;
            if (light.ToArgb() == Muted.ToArgb()) return mutedText;
            return inkText;
        }
        internal static Color Background(Color light)
        {
            if (light.A == 0) return light;
            if (light.ToArgb() == Rail.ToArgb()) return IsDark ? DarkRail : Rail;
            if (light.ToArgb() == InputSurface.ToArgb()) return IsDark ? DarkRail : InputSurface;
            if (light.ToArgb() == ElevatedSurface.ToArgb()) return IsDark ? DarkElevated : ElevatedSurface;
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

        private sealed class MenuPalette { internal bool Applied, Dark; }
        private static readonly ConditionalWeakTable<ContextMenuStrip, MenuPalette> menuPalettes =
            new ConditionalWeakTable<ContextMenuStrip, MenuPalette>();

        internal static void ApplyTray(ContextMenuStrip menu)
        {
            var palette = menuPalettes.GetValue(menu, key => new MenuPalette());
            if (palette.Applied && palette.Dark == IsDark) return;
            palette.Applied = true;
            palette.Dark = IsDark;
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
            using (var path = UiStyle.RoundRect(new RectangleF(0, 0, menu.Width, menu.Height), UiStyle.ControlRadius * UiStyle.Dpi(menu) / 96F))
                menu.Region = new Region(path);
            if (old != null) old.Dispose();
        }

        private sealed class RoundedMenuRenderer : ToolStripProfessionalRenderer
        {
            internal RoundedMenuRenderer() : base(new MenuColors()) { RoundedEdges = false; }
            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var path = UiStyle.RoundRect(new RectangleF(.5F, .5F, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1), UiStyle.ControlRadius * UiStyle.Dpi(e.ToolStrip) / 96F))
                using (var pen = new Pen(Edge)) e.Graphics.DrawPath(pen, path);
            }
            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Selected) return;
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var path = UiStyle.RoundRect(new RectangleF(1, 1, e.Item.Width - 2, e.Item.Height - 2), UiStyle.ControlRadius * UiStyle.Dpi(e.ToolStrip) / 96F))
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
            bool transparent = control is Label || control is PictureBox || control is TableLayoutPanel || control is FlowLayoutPanel;
            control.BackColor = transparent ? Color.Transparent : Background(original.Back);
            control.ForeColor = Text(original.Fore);
            if (control is ListView || control is TextBoxBase || control is ComboBox ||
                control is DataGridView || control is ScrollableControl || control is ScrollBar)
            {
                control.HandleCreated -= NativeThemeCreated;
                control.HandleCreated += NativeThemeCreated;
                if (control.IsHandleCreated) ApplyNativeTheme(control);
            }
            var grid = control as DataGridView;
            if (grid != null)
            {
                grid.BackgroundColor = Background(Color.White);
                grid.GridColor = Edge;
                grid.EnableHeadersVisualStyles = false;
                grid.ColumnHeadersDefaultCellStyle.BackColor = Background(Page);
                grid.ColumnHeadersDefaultCellStyle.ForeColor = Text(Muted);
                grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Background(Page);
                grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Text(Muted);
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
                push.FlatAppearance.MouseOverBackColor = primary ? AccentHover : Hover;
                push.FlatAppearance.MouseDownBackColor = primary ? AccentPressed : Edge;
                push.Paint -= PaintDisabledButton; if (!(push is RoundedButton)) push.Paint += PaintDisabledButton;
            }
            foreach (Control child in control.Controls) Paint(child);
            control.Invalidate();
        }
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr window, string app, string id);
        private static void NativeThemeCreated(object sender, EventArgs args) { ApplyNativeTheme((Control)sender); }
        private static void ApplyNativeTheme(Control control)
        {
            try
            {
                SetWindowTheme(control.Handle, IsDark ? "DarkMode_Explorer" : "Explorer", null);
                var combo = control as DarkComboBox;
                if (combo != null) combo.ApplyListTheme();
            }
            catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
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
            if (IsDark == dark && ThemeColor.ToArgb() == color.ToArgb()) return;
            ThemeColor = color;
            IsDark = dark;
            UpdatePalette();
            foreach (Form window in Application.OpenForms) if (window.TopLevel) Apply(window);
        }
    }
}
