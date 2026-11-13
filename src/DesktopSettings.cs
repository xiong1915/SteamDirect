using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
namespace SteamDirect
{
    internal static class WindowMessages
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        internal static readonly int ShowMain = RegisterWindowMessage("SteamDirect.ShowMain.v1");
    }

    internal sealed class UserPreferences
    {
        internal static readonly string DefaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamDirect", "settings.ini");
        internal string LogDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamDirect", "Logs");
        internal bool ReduceMotion;
        internal bool TrayEnabled = true;
        internal int RetentionDays = 7;
        internal bool NightMode = true;
        internal string ThemeColor = "#6750A4";
        internal static UserPreferences Load(string path)
        {
            var result = new UserPreferences();
            if (!File.Exists(path)) return result;
            string[] lines; try { if (new FileInfo(path).Length > 65536) return result; lines = File.ReadAllLines(path); } catch (IOException) { return result; } catch (UnauthorizedAccessException) { return result; }
            foreach (string line in lines)
            {
                if (line.StartsWith("LogDirectory=", StringComparison.Ordinal)) { try { var value = line.Substring(13); if (Path.IsPathRooted(value)) result.LogDirectory = Path.GetFullPath(value); } catch (ArgumentException) { } catch (NotSupportedException) { } catch (PathTooLongException) { } }
                bool reduce; if (line.StartsWith("ReduceMotion=", StringComparison.Ordinal) && bool.TryParse(line.Substring(13), out reduce)) result.ReduceMotion = reduce;
                bool tray;
                int days;
                bool night;
                if (line.StartsWith("TrayEnabled=", StringComparison.Ordinal) && bool.TryParse(line.Substring(12), out tray)) result.TrayEnabled = tray;
                if (line.StartsWith("NightMode=", StringComparison.Ordinal) && bool.TryParse(line.Substring(10), out night)) result.NightMode = night;
                if (line.StartsWith("RetentionDays=", StringComparison.Ordinal) && int.TryParse(line.Substring(14), out days) && days >= 1 && days <= 365) result.RetentionDays = days;
                Color color;
                if (line.StartsWith("ThemeColor=", StringComparison.Ordinal) && AppTheme.TryColor(line.Substring(11), out color))
                    result.ThemeColor = AppTheme.ColorCode(color);
            }
            return result;
        }
        internal static void Save(string path, bool trayEnabled, int retentionDays, bool nightMode = false, string themeColor = "#6750A4", string logDirectory = null, bool reduceMotion = false)
        {
            if (retentionDays < 1 || retentionDays > 365) throw new ArgumentOutOfRangeException("retentionDays");
            Color color;
            if (!AppTheme.TryColor(themeColor, out color)) throw new ArgumentException("主题色格式应为 #RRGGBB。", "themeColor");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            logDirectory = logDirectory ?? new UserPreferences().LogDirectory;
            if (!Path.IsPathRooted(logDirectory)) throw new ArgumentException("日志目录必须是绝对路径。", "logDirectory");
            logDirectory = Path.GetFullPath(logDirectory);
            string stage = path + ".new-" + Guid.NewGuid().ToString("N");
            try { File.WriteAllLines(stage, new[] { "TrayEnabled=" + trayEnabled, "RetentionDays=" + retentionDays, "NightMode=" + nightMode,
                "ThemeColor=" + AppTheme.ColorCode(color), "LogDirectory=" + (logDirectory ?? new UserPreferences().LogDirectory), "ReduceMotion=" + reduceMotion }, Encoding.UTF8);
            if (File.Exists(path)) File.Replace(stage, path, null); else File.Move(stage, path); } finally { if (File.Exists(stage)) File.Delete(stage); }
        }
    }

    internal static class StartupSettings
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "SteamDirect";
        internal static bool IsEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                return key != null && string.Equals(key.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
        private static string Command { get { return "\"" + Application.ExecutablePath + "\" --background"; } }
        internal static void SetEnabled(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
                else key.DeleteValue(ValueName, false);
            }
        }
    }

    internal static class LogStore
    {
        internal static string DirectoryPath = UserPreferences.Load(UserPreferences.DefaultPath).LogDirectory;
        internal static string CurrentPath { get { return Path.Combine(DirectoryPath, "SteamDirect-" + DateTime.Today.ToString("yyyy-MM-dd") + ".log"); } }
        internal static int Cleanup(string directory, int retentionDays, DateTime today)
        {
            if (retentionDays < 1 || retentionDays > 365) throw new ArgumentOutOfRangeException("retentionDays");
            if (!Directory.Exists(directory)) return 0;
            if (!TempCleaner.NoReparseAncestors(Path.GetFullPath(directory))) throw new IOException("日志目录包含重解析路径，拒绝清理。");
            int removed = 0;
            foreach (string path in Directory.GetFiles(directory, "*.log", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                string name = Path.GetFileNameWithoutExtension(path);
                if (!name.StartsWith("SteamDirect-", StringComparison.Ordinal)) continue;
                name = name.Substring(12);
                if (name.EndsWith("-crash", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 6);
                DateTime date;
                if (!DateTime.TryParseExact(name, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out date)) continue;
                if (date.Date < today.Date.AddDays(1 - retentionDays)) { File.Delete(path); removed++; }
            }
            return removed;
        }
    }

    internal sealed class SettingsWindow : Form
    {
        internal event Action SaveRequested;
        internal event Action CancelRequested;
        private readonly Label feedback = new Label();
        private readonly CheckBox tray = new ToggleSwitch();
        private readonly CheckBox startup = new ToggleSwitch();
        private readonly CheckBox night = new ToggleSwitch();
        private readonly NumericUpDown days = new NumericUpDown();
        private readonly TextBox colorCode = new TextBox();
        private readonly TextBox logDirectoryBox = new TextBox();
        private readonly CheckBox reduce = new ToggleSwitch();
        internal string LogDirectory { get { return Path.GetFullPath(logDirectoryBox.Text.Trim()); } }
        internal bool ReduceMotion { get { return reduce.Checked; } }
        private Color selectedColor;
        internal bool TrayEnabled { get { return tray.Checked; } }
        internal bool AutoStart { get { return startup.Checked; } }
        internal bool NightMode { get { return night.Checked; } }
        internal int RetentionDays { get { return (int)days.Value; } }
        internal string ThemeColor { get { return colorCode.Text.Trim().ToUpperInvariant(); } }
        internal SettingsWindow(bool trayEnabled, bool autoStart, int retentionDays, bool nightMode = false, string themeColor = "#6750A4", string logDirectory = null, bool reduceMotion = false)
        {
            SuspendLayout();
            Text = "设置";
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = UiStyle.Body;
            BackColor = AppTheme.Page;
            ForeColor = AppTheme.Ink;
            ClientSize = new Size(640, 780);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;

            var layout = UiStyle.Grid(1, 3);
            layout.Padding = UiStyle.PagePadding;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiStyle.HeaderHeight));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
            layout.Controls.Add(UiStyle.Header("设置", "外观、启动行为与日志管理"), 0, 0);

            var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
            var cards = UiStyle.Grid(1, 3);
            cards.Dock = DockStyle.Top;
            cards.Height = 528;
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 176, 152, 200 }) cards.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            viewport.Controls.Add(cards);
            layout.Controls.Add(viewport, 0, 1);

            var appearance = Section(cards, 0, "外观", 32, 32, 40);
            night.Text = "启用夜间模式";
            night.Checked = nightMode;
            night.Dock = DockStyle.Fill;
            appearance.Controls.Add(night, 0, 1);
            appearance.Controls.Add(BuildColorPicker(themeColor), 0, 2);
            appearance.Controls.Add(new Label { Text = "选择主题色或输入 #RRGGBB，保存后生效。", Dock = DockStyle.Fill,
                ForeColor = AppTheme.Muted, AutoEllipsis = true, Margin = Padding.Empty, Font = UiStyle.Caption }, 0, 3);

            var behavior = Section(cards, 1, "启动与托盘", 32, 40);
            tray.Text = "关闭窗口后留在托盘";
            tray.Checked = trayEnabled;
            tray.Dock = DockStyle.Fill;
            startup.Text = "登录 Windows 时自动启动";
            startup.Checked = autoStart;
            startup.Dock = DockStyle.Fill;
            behavior.Controls.Add(tray, 0, 1);
            behavior.Controls.Add(startup, 0, 2);

            var logs = Section(cards, 2, "日志与动效", 32, 40, 40);
            logDirectoryBox.Text = logDirectory ?? LogStore.DirectoryPath;
            logs.Controls.Add(UiStyle.Entry(logDirectoryBox, "日志保存目录（绝对路径）"), 0, 1);
            var retention = UiStyle.Grid(2, 1);
            retention.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            retention.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 124));
            retention.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            retention.Controls.Add(new Label { Text = "日志保留天数（1–365）", Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            days.Minimum = 1;
            days.Maximum = 365;
            days.Value = retentionDays;
            retention.Controls.Add(UiStyle.Entry(days, "日志保留天数"), 1, 0);
            logs.Controls.Add(retention, 0, 2);
            var cleanup = new RoundedButton { Text = "清理过期日志", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(220, UiStyle.ButtonHeight), Height = UiStyle.ButtonHeight,
                Margin = new Padding(0, UiStyle.SmallGap, 0, 0), Anchor = AnchorStyles.Left | AnchorStyles.Top };
            AppTheme.ActionButton(cleanup, false);
            cleanup.Click += (sender, args) =>
            {
                try
                {
                    int count = LogStore.Cleanup(LogStore.DirectoryPath, RetentionDays, DateTime.Today);
                    NoticeDialog.Show(this, "已清理 " + count + " 个过期日志文件，保留最近 " + RetentionDays + " 天。", "日志清理");
                }
                catch (Exception ex) { NoticeDialog.Show(this, "清理未完成：" + ex.Message, "日志清理", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            var actions = UiStyle.Grid(2, 1); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            reduce.Text = "减少动画"; reduce.Checked = reduceMotion; reduce.Dock = DockStyle.Fill; actions.Controls.Add(cleanup, 0, 0); actions.Controls.Add(reduce, 1, 0); logs.Controls.Add(actions, 0, 3);

            var footer = UiStyle.Grid(1, 3);
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 48, 24, 40 }) footer.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
                Margin = Padding.Empty, WrapContents = false };
            var save = new RoundedButton { Text = "保存设置", Width = 120, Height = UiStyle.ButtonHeight, Margin = new Padding(UiStyle.SmallGap, 0, 0, 0) };
            var cancel = new RoundedButton { Text = "返回概览", DialogResult = DialogResult.Cancel,
                Width = 120, Height = UiStyle.ButtonHeight, Margin = Padding.Empty };
            save.Click += (sender, args) =>
            {
                if (!ValidateThemeColor()) return;
                if (TopLevel) DialogResult = DialogResult.OK;
                else if (SaveRequested != null) SaveRequested();
            };
            cancel.Click += (sender, args) => { if (!TopLevel && CancelRequested != null) CancelRequested(); };
            AppTheme.ActionButton(save, true);
            AppTheme.ActionButton(cancel, false);
            buttons.Controls.Add(save);
            buttons.Controls.Add(cancel);
            footer.Controls.Add(buttons, 0, 0);
            feedback.Text = "更改后点击保存设置";
            feedback.Dock = DockStyle.Fill;
            feedback.Font = UiStyle.Caption;
            feedback.ForeColor = AppTheme.Muted;
            feedback.TextAlign = ContentAlignment.MiddleRight;
            footer.Controls.Add(feedback, 0, 1);

            var about = UiStyle.Grid(2, 1);
            about.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            about.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            about.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            about.Controls.Add(new Label { Text = "开发者 xiong1915 · v3.0.0", Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft, ForeColor = AppTheme.Muted, AutoEllipsis = true, Font = UiStyle.Caption }, 0, 0);
            const string repositoryUrl = "https://github.com/xiong1915/SteamDirect";
            var github = new RoundedButton { Text = "GitHub 项目", Dock = DockStyle.Fill,
                Margin = new Padding(0, UiStyle.Space4, 0, UiStyle.Space4), Icon = UiIcon.External,
                Tag = repositoryUrl, AccessibleDescription = repositoryUrl };
            AppTheme.ActionButton(github, false);
            github.Click += (sender, args) =>
            {
                try { Process.Start(new ProcessStartInfo(repositoryUrl) { UseShellExecute = true }); }
                catch (Exception ex) { NoticeDialog.Show(this, "无法打开浏览器，请访问 " + repositoryUrl + Environment.NewLine + ex.Message, "GitHub 项目"); }
            };
            about.Controls.Add(github, 1, 0);
            footer.Controls.Add(about, 0, 2);
            layout.Controls.Add(footer, 0, 2);
            foreach (var toggle in new[] { night, tray, startup })
                toggle.CheckedChanged += (sender, args) => feedback.Text = "有更改尚未保存";
            days.ValueChanged += (sender, args) => feedback.Text = "有更改尚未保存";
            AcceptButton = save;
            CancelButton = cancel;
            Controls.Add(layout);
            AppTheme.Apply(this);
            ResumeLayout(true);
        }

        internal void ShowSaved() { feedback.Text = "设置已保存"; }

        internal bool ValidateThemeColor()
        {
            Color parsed;
            if (AppTheme.TryColor(ThemeColor, out parsed)) return true;
            feedback.Text = "颜色格式应为 #RRGGBB，例如 #6750A4";
            colorCode.Focus();
            return false;
        }

        private Control BuildColorPicker(string initial)
        {
            AppTheme.TryColor(initial, out selectedColor);
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var swatch = new Panel { Dock = DockStyle.Fill, Margin = new Padding(2, 8, 8, 8),
                AccessibleName = "主题色预览" };
            swatch.Paint += (sender, args) =>
            {
                using (var brush = new SolidBrush(selectedColor)) args.Graphics.FillRectangle(brush, swatch.ClientRectangle);
                using (var pen = new Pen(AppTheme.Edge)) args.Graphics.DrawRectangle(pen, 0, 0, swatch.Width - 1, swatch.Height - 1);
            };
            colorCode.Text = AppTheme.ColorCode(selectedColor);
            colorCode.AccessibleName = "主题色代码";
            colorCode.TextChanged += (sender, args) =>
            {
                Color parsed;
                if (AppTheme.TryColor(ThemeColor, out parsed)) selectedColor = parsed;
                swatch.Invalidate();
                feedback.Text = "有更改尚未保存";
            };
            var choose = new RoundedButton { Text = "选择颜色", Dock = DockStyle.Fill };
            var reset = new RoundedButton { Text = "默认颜色", Dock = DockStyle.Fill };
            AppTheme.ActionButton(choose, false);
            AppTheme.ActionButton(reset, false);
            choose.Click += (sender, args) =>
            {
                using (var picker = new ColorDialog { Color = selectedColor, FullOpen = true })
                    if (picker.ShowDialog(TopLevelControl) == DialogResult.OK)
                        colorCode.Text = AppTheme.ColorCode(picker.Color);
            };
            reset.Click += (sender, args) => colorCode.Text = AppTheme.ColorCode(AppTheme.Accent);
            row.Controls.Add(swatch, 0, 0);
            row.Controls.Add(UiStyle.Entry(colorCode, "主题色代码"), 1, 0);
            row.Controls.Add(choose, 2, 0);
            row.Controls.Add(reset, 3, 0);
            return row;
        }

        private TableLayoutPanel Section(TableLayoutPanel parent, int row, string title, params int[] heights)
        {
            var card = UiStyle.Card(true);
            card.Margin = new Padding(0, 0, 0, UiStyle.Gap);
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = heights.Length + 1 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in heights) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, Margin = Padding.Empty, Font = UiStyle.SectionTitle }, 0, 0);
            card.Controls.Add(grid);
            parent.Controls.Add(card, 0, row);
            return grid;
        }
    }
}







