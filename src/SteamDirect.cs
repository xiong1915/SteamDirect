using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SteamDirect
{
    internal static class Program
    {
        private static string CrashLog { get { return Path.Combine(LogStore.DirectoryPath, "SteamDirect-" + DateTime.Today.ToString("yyyy-MM-dd") + "-crash.log"); } }

        private static void SaveCrash(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CrashLog));
                File.AppendAllText(CrashLog, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                    Environment.NewLine + (ex == null ? "Unknown error" : ex.ToString()) +
                    Environment.NewLine + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        private static void ShowFatal(Exception ex)
        {
            SaveCrash(ex);
            try
            {
                MessageBox.Show("程序启动或运行时发生异常：\n" +
                    (ex == null ? "未知错误" : ex.Message) +
                    "\n\n详细记录：" + CrashLog,
                    "SteamDirect 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }

        [STAThread]
        private static void Main(string[] arguments)
        {
            if (HighDpi.EnsureConfiguration(arguments)) return;
            RunApplication(arguments);
        }
        internal static void RunApplication(string[] arguments)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (sender, args) =>
            {
                ShowFatal(args.Exception);
                Application.Exit();
            };
            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
                SaveCrash(args.ExceptionObject as Exception);
            try
            {
                using (var singleInstance = new Mutex(false, @"Global\SteamDirect.Desktop." + System.Security.Principal.WindowsIdentity.GetCurrent().User.Value))
                {
                    bool owned;
                    try { owned = singleInstance.WaitOne(0); }
                    catch (AbandonedMutexException) { owned = true; }
                    if (!owned)
                    {
                        WindowMessages.PostMessage(new IntPtr(0xffff), WindowMessages.ShowMain, IntPtr.Zero, IntPtr.Zero);
                        return;
                    }
                    try
                    {
                        using (var legacy = new Mutex(false, @"Local\SteamDirect.Desktop"))
                        {
                            bool legacyOwned;
                            try { legacyOwned = legacy.WaitOne(0); } catch (AbandonedMutexException) { legacyOwned = true; }
                            if (!legacyOwned) { WindowMessages.PostMessage(new IntPtr(0xffff), WindowMessages.ShowMain, IntPtr.Zero, IntPtr.Zero); return; }
                            try { Application.Run(new MainWindow(Array.IndexOf(arguments, "--background") >= 0)); }
                            finally { legacy.ReleaseMutex(); }
                        }
                    }
                    finally { singleInstance.ReleaseMutex(); }
                }
            }
            catch (Exception ex)
            {
                ShowFatal(ex);
            }
        }
    }

    internal sealed class MainWindow : Form
    {
        private readonly Button runButton = new RoundedButton();
        private readonly Button logButton = new RoundedButton();
        private readonly Button restoreButton = new RoundedButton();
        private readonly CleanupPanel cleanupPanel;
        private readonly Label statusLabel = new Label();
        private readonly TextBox logBox = new TextBox();
        private string logPath { get { return LogStore.CurrentPath; } }
        private DateTime lastCleanupDate = DateTime.MinValue;
        private bool running;
        private readonly StepTimeline timeline = new StepTimeline();
        private readonly Label elapsedLabel = new Label();
        private readonly System.Windows.Forms.Timer animationTimer = new System.Windows.Forms.Timer();
        private readonly Stopwatch operationWatch = new Stopwatch();

        private readonly NotifyIcon trayIcon = new NotifyIcon();
        private readonly ContextMenuStrip trayMenu = new ContextMenuStrip();
        private readonly UserPreferences preferences = UserPreferences.Load(UserPreferences.DefaultPath);
        private bool exitRequested;
        private readonly HistoryMonitor history;
        private readonly Panel pageHost = new Panel { Dock = DockStyle.Fill };
        private readonly Dictionary<string, Button> navigation = new Dictionary<string, Button>();
        private Control dashboard;
        private Form activePage;

        internal MainWindow(bool background = false, HistoryMonitor historyOverride = null)
        {
            history = historyOverride ?? new HistoryMonitor(HistoryMonitor.DefaultPath);
            Motion.Reduced = preferences.ReduceMotion; Motion.IsGaming = () => history.IsPlaying;
            SuspendLayout();
            AppTheme.IsDark = preferences.NightMode;
            AppTheme.TryColor(preferences.ThemeColor, out AppTheme.ThemeColor);
            Text = "Steam 直连助手 v3.0.0";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = UiStyle.Body;
            BackColor = AppTheme.Page;
            ForeColor = AppTheme.Ink;
            ClientSize = new Size(1180, 780);
            MinimumSize = new Size(1080, 800);
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;

            BuildNavigation();
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = UiStyle.PagePadding, ColumnCount = 1, RowCount = 3 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiStyle.HeaderHeight));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 448));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            dashboard = root;
            pageHost.Controls.Add(root);
            var header = UiStyle.Header("概览", "准备好网络，再开始游戏。");
            root.Controls.Add(header, 0, 0);

            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 238));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.Controls.Add(body, 0, 1);
            var left = UiStyle.Card();
            left.Margin = new Padding(0, 0, UiStyle.Gap, 0);
            timeline.Dock = DockStyle.Fill;
            left.Controls.Add(timeline);
            body.Controls.Add(left, 0, 0);

            var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 272));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            body.Controls.Add(content, 1, 0);
            var action = UiStyle.Card();
            var actionLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
            actionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            actionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiStyle.Space24));
            actionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiStyle.Space32 + UiStyle.Space24));
            actionLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            actionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiStyle.ButtonHeight));
            actionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiStyle.ButtonHeight));
            elapsedLabel.Text = "●  等待执行";
            elapsedLabel.ForeColor = AppTheme.Muted;
            elapsedLabel.Font = UiStyle.Caption;
            elapsedLabel.Dock = DockStyle.Fill;
            elapsedLabel.Margin = Padding.Empty;
            actionLayout.Controls.Add(elapsedLabel, 0, 0);
            statusLabel.Text = "准备开始直连";
            statusLabel.Dock = DockStyle.Fill;
            statusLabel.Margin = Padding.Empty;
            statusLabel.AutoEllipsis = true;
            statusLabel.Font = UiStyle.HeroTitle;
            actionLayout.Controls.Add(statusLabel, 0, 1);
            actionLayout.Controls.Add(new Label { Text = "先保存 ChatGPT 草稿与编辑器文件并退出游戏。检查通过后重启 Steam。", Dock = DockStyle.Fill, ForeColor = AppTheme.Muted }, 0, 2);
            runButton.Text = "严格直连并重启 Steam";
            runButton.Dock = DockStyle.Fill;
            runButton.Margin = Padding.Empty;
            AppTheme.ActionButton(runButton, true);
            runButton.Font = UiStyle.SectionTitle;
            runButton.Cursor = Cursors.Hand;
            runButton.TabIndex = 0;
            runButton.Click += async (sender, args) => await RunAsync();
            actionLayout.Controls.Add(runButton, 0, 3);
            restoreButton.Text = "恢复原代理设置";
            restoreButton.Dock = DockStyle.Fill;
            restoreButton.Margin = new Padding(0, UiStyle.SmallGap, 0, 0);
            AppTheme.ActionButton(restoreButton, false);
            restoreButton.FlatAppearance.BorderSize = 0;
            restoreButton.Enabled = File.Exists(ProxySession.BackupPath);
            restoreButton.Click += async (sender, args) => await RestoreProxyAsync();
            actionLayout.Controls.Add(restoreButton, 0, 4);
            action.Controls.Add(actionLayout);
            content.Controls.Add(action, 0, 0);
            cleanupPanel = new CleanupPanel(Log);
            content.Controls.Add(cleanupPanel, 0, 1);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 12, 0, 0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, UiStyle.Space48));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.Controls.Add(footer, 0, 2);
            var logHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 0, 6) };
            logHeader.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            logHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            logHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
            logHeader.Controls.Add(new Label { Text = "操作记录", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = UiStyle.SectionTitle }, 0, 0);
            logButton.Text = "打开日志";
            logButton.Dock = DockStyle.Fill;
            AppTheme.ActionButton(logButton, false);
            logButton.Cursor = Cursors.Hand;
            logButton.TabIndex = 1;
            logHeader.Controls.Add(logButton, 1, 0);
            footer.Controls.Add(logHeader, 0, 0);
            logButton.Click += (s, e) =>
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(logPath));
                    using (File.Open(logPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite)) { }
                    Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "notepad.exe"), "\"" + logPath + "\"")
                    { UseShellExecute = false });
                }
                catch (Exception ex)
                {
                    NoticeDialog.Show(this, "无法打开日志：" + ex.Message + "\n日志路径：" + logPath, "日志", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };

            var logFrame = UiStyle.Card(true);
            logBox.Dock = DockStyle.Fill;
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.BackColor = Color.White;
            logBox.ForeColor = AppTheme.Muted;
            logBox.Font = UiStyle.Caption;
            logBox.BorderStyle = BorderStyle.None;
            logBox.AccessibleName = "操作记录";
            logFrame.Controls.Add(logBox);
            footer.Controls.Add(logFrame, 0, 1);
            animationTimer.Interval = UiStyle.MotionTick;
            animationTimer.Tick += (sender, args) =>
            {
                timeline.Angle = (timeline.Angle + UiStyle.SpinnerStep) % 360;
                timeline.Invalidate();
                if (timeline.ActiveStep >= 0 && timeline.ActiveStep < timeline.StepCount)
                    elapsedLabel.Text = "第 " + (timeline.ActiveStep + 1) + " / " + timeline.StepCount + " 步  ·  " + operationWatch.Elapsed.ToString(@"mm\:ss");
            };
            trayMenu.Items.Add("打开主窗口", null, (sender, args) => ShowMainWindow());
            trayMenu.Items.Add("设置", null, (sender, args) => OpenSettings());
            var restoreMenu = trayMenu.Items.Add("恢复原代理设置", null, async (sender, args) => { ShowMainWindow(); await RestoreProxyAsync(); });
            trayMenu.Opening += (sender, args) =>
            {
                restoreMenu.Enabled = !running && File.Exists(ProxySession.BackupPath);
                AppTheme.ApplyTray(trayMenu);
            };
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("退出 Steam 直连助手", null, (sender, args) => RequestExit());
            trayIcon.Text = "Steam 直连助手";
            trayIcon.Icon = Icon;
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.DoubleClick += (sender, args) => ShowMainWindow();
            trayIcon.Visible = preferences.TrayEnabled;
            FormClosing += (sender, args) =>
            {
                if (args.CloseReason == CloseReason.UserClosing && !exitRequested && preferences.TrayEnabled)
                {
                    args.Cancel = true;
                    Hide();
                    return;
                }
                if (running && args.CloseReason == CloseReason.UserClosing)
                {
                    args.Cancel = true;
                    exitRequested = false;
                    NoticeDialog.Show(this, "操作正在进行，请等待完成后退出。", "提示");
                }
            };
            Shown += (sender, args) => { if (background && preferences.TrayEnabled) Hide(); };
            Log("就绪，请先退出游戏。");
            Shown += (sender, args) => history.Start();
            AppTheme.Apply(this);
            ResumeLayout(true);
        }

        private void BuildNavigation()
        {
            var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 172));
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var rail = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7,
                Padding = new Padding(12, 24, 12, 16), Margin = Padding.Empty };
            rail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
            for (int i = 0; i < 4; i++) rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            rail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            var brand = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            brand.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            brand.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            brand.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var mascot = new PictureBox { Image = Icon.ToBitmap(), Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom, TabStop = false, Margin = new Padding(40, 0, 40, 0) };
            using (var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("SteamDirect.Mascot"))
                if (stream != null)
                    using (var source = Image.FromStream(stream))
                    {
                        mascot.Image.Dispose();
                        mascot.Image = new Bitmap(source);
                    }
            mascot.Disposed += (sender, args) => mascot.Image.Dispose();
            brand.Controls.Add(mascot, 0, 0);
            brand.Controls.Add(new Label { Text = "SteamDirect", Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter, Font = UiStyle.SectionTitle }, 0, 1);
            rail.Controls.Add(brand, 0, 0);
            string[] pages = { "概览", "网络诊断", "游戏记录", "设置" };
            UiIcon[] icons = { UiIcon.Home, UiIcon.Network, UiIcon.History, UiIcon.Settings };
            for (int i = 0; i < pages.Length; i++)
            {
                string page = pages[i];
                var button = new RoundedButton { Text = page, Dock = DockStyle.Fill, Margin = new Padding(0, 5, 0, 5),
                    AccessibleName = page + "页面", TabIndex = i, Icon = icons[i], CornerRadius = UiStyle.NavigationRadius };
                AppTheme.ActionButton(button, false);
                button.Font = UiStyle.Body;
                button.Click += (sender, args) => NavigateTo(page);
                navigation.Add(page, button);
                rail.Controls.Add(button, 0, i + 1);
            }
            rail.Controls.Add(new Label { Text = "v3.0.0 · 游戏网络助手", ForeColor = AppTheme.Muted,
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomCenter, Font = UiStyle.Caption }, 0, 6);
            shell.Controls.Add(rail, 0, 0);
            shell.Controls.Add(pageHost, 1, 0);
            Controls.Add(shell);
            SelectNavigation("概览");
        }

        private void SelectNavigation(string page)
        {
            foreach (var entry in navigation)
            {
                AppTheme.SetBackground(entry.Value, entry.Key == page ? AppTheme.Pill : AppTheme.Page);
                AppTheme.SetForeground(entry.Value, entry.Key == page ? AppTheme.Accent : AppTheme.Muted);
                var nav = entry.Value as RoundedButton;
                if (nav != null) nav.SelectedNavigation = entry.Key == page;
            }
        }

        private void NavigateTo(string page)
        {
            if (page != "概览" && running)
            {
                NoticeDialog.Show(this, "请等待直连流程完成后再切换页面。", "正在执行");
                return;
            }
            if (page == "游戏记录" && history.IsPlaying)
            {
                NoticeDialog.Show(this, "游戏结束后可查看报告。", "游戏记录");
                return;
            }
            if (activePage != null && activePage.Tag as string == page) return;
            if (activePage != null)
            {
                activePage.Close();
                activePage.Dispose();
                activePage = null;
            }
            dashboard.Visible = page == "概览";
            SelectNavigation(page);
            if (page == "概览") return;
            if (page == "网络诊断") activePage = new NetworkWindow(Log);
            else if (page == "游戏记录") activePage = new HistoryWindow(history.Store, () => history.IsPlaying);
            else
            {
                var settings = new SettingsWindow(preferences.TrayEnabled, StartupSettings.IsEnabled(),
                    preferences.RetentionDays, preferences.NightMode, preferences.ThemeColor, preferences.LogDirectory, preferences.ReduceMotion);
                settings.SaveRequested += () => SaveSettings(settings);
                settings.CancelRequested += () => NavigateTo("概览");
                activePage = settings;
            }
            activePage.Tag = page;
            activePage.TopLevel = false;
            activePage.FormBorderStyle = FormBorderStyle.None;
            activePage.MinimumSize = Size.Empty;
            activePage.Dock = DockStyle.Fill;
            pageHost.Controls.Add(activePage);
            activePage.Show();
            activePage.BringToFront();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (activePage != null)
                {
                    activePage.Close();
                    activePage.Dispose();
                    activePage = null;
                }
                history.Dispose();
                animationTimer.Dispose();
                trayIcon.Dispose();
                trayMenu.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WindowMessages.ShowMain) ShowMainWindow();
            base.WndProc(ref message);
        }

        internal void ShowMainWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        internal void RequestExit()
        {
            exitRequested = true;
            Close();
        }

        private void OpenSettings()
        {
            ShowMainWindow();
            NavigateTo("设置");
        }

        private void SaveSettings(SettingsWindow dialog)
        {
                try
                {
                    if (!dialog.ValidateThemeColor()) return;
                    Directory.CreateDirectory(dialog.LogDirectory);
                    if (dialog.AutoStart != StartupSettings.IsEnabled()) StartupSettings.SetEnabled(dialog.AutoStart);
                    UserPreferences.Save(UserPreferences.DefaultPath, dialog.TrayEnabled, dialog.RetentionDays, dialog.NightMode, dialog.ThemeColor, dialog.LogDirectory, dialog.ReduceMotion);
                    preferences.LogDirectory = dialog.LogDirectory; LogStore.DirectoryPath = preferences.LogDirectory;
                    preferences.ReduceMotion = dialog.ReduceMotion; Motion.Reduced = preferences.ReduceMotion;
                    preferences.NightMode = dialog.NightMode;
                    preferences.ThemeColor = dialog.ThemeColor;
                    AppTheme.SetTheme(preferences.NightMode, preferences.ThemeColor);
                    preferences.RetentionDays = dialog.RetentionDays;
                    lastCleanupDate = DateTime.MinValue;
                    preferences.TrayEnabled = dialog.TrayEnabled;
                    trayIcon.Visible = preferences.TrayEnabled;
                    Log("设置已保存：托盘" + (preferences.TrayEnabled ? "开启" : "关闭") + "，开机自启" + (dialog.AutoStart ? "开启" : "关闭") + "。");
                    dialog.ShowSaved();
                }
                catch (Exception ex) { NoticeDialog.Show(this, "设置未能全部保存：" + ex.Message, "设置", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void Log(string message)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action<string>(Log), message); return; }
            string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message;
            logBox.AppendText(line + Environment.NewLine);
            try
            {
                Directory.CreateDirectory(LogStore.DirectoryPath);
                if (lastCleanupDate != DateTime.Today)
                {
                    lastCleanupDate = DateTime.Today;
                    try { LogStore.Cleanup(LogStore.DirectoryPath, preferences.RetentionDays, DateTime.Today); }
                    catch (Exception ex) { logBox.AppendText("过期日志暂未清理：" + ex.Message + Environment.NewLine); }
                }
                File.AppendAllText(logPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) { logBox.AppendText("日志写入失败：" + ex.Message + Environment.NewLine); }
        }

        private void Status(string message)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action<string>(Status), message); return; }
            statusLabel.Text = message;
            AppTheme.SetForeground(statusLabel, message.StartsWith("已停止") ? AppTheme.Error :
                message.StartsWith("完成") ? AppTheme.Success : AppTheme.Ink);

        }

        internal void AdvanceStep(int index, string message)
        {
            if (InvokeRequired) { BeginInvoke(new Action<int, string>(AdvanceStep), index, message); return; }
            timeline.ActiveStep = index;
            timeline.Failed = false;
            timeline.AccessibleDescription = "第 " + (index + 1) + " 步，" + message;
            timeline.Invalidate();
            Status(message);
        }

        internal void FinishProgress(bool success)
        {
            animationTimer.Stop();
            operationWatch.Stop();
            timeline.Failed = !success;
            if (success) timeline.ActiveStep = timeline.StepCount;
            timeline.AccessibleDescription = success ? "直连流程已完成" : "直连流程已停止";
            timeline.Invalidate();
            elapsedLabel.Text = (success ? "已完成" : "已停止") + "  ·  耗时 " + operationWatch.Elapsed.ToString(@"mm\:ss");
        }

        private async Task RestoreProxyAsync()
        {
            if (running) return;
            if (!File.Exists(ProxySession.BackupPath)) { NoticeDialog.Show(this, "当前没有待恢复的代理备份。", "恢复代理"); return; }
            if (IsProcessRunning("cs2") || IsProcessRunning("csgo") || IsProcessRunning("GameOverlayUI"))
            { NoticeDialog.Show(this, "请先退出游戏，再恢复代理设置。", "恢复代理"); return; }
            running = true; runButton.Enabled = false; restoreButton.Enabled = false; cleanupPanel.AllowCleanup(false);
            Status("正在检查代理并恢复设置…");
            try
            {
                if (ProxySnapshot.IsLegacy(ProxySession.BackupPath))
                {
                    if (NoticeDialog.Show(this, "检测到旧版明文代理备份。程序无法证明旧文件是否被修改。请先核对原代理配置；只有确认内容可信时才迁移。迁移会保留原始 .legacy 文件，其中可能包含敏感代理信息。取消不会修改代理。", "确认旧版备份来源", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                    ProxySnapshot.MigrateLegacy(ProxySession.BackupPath);
                }
                await Task.Run(() => ProxySession.RestoreOriginal(ProxySession.BackupPath, new WindowsProxyAccess()));
                Status("已恢复原代理设置");
                Log("原代理设置已恢复并核验。");
                NoticeDialog.Show(this, "原代理设置已恢复，并已核对写入结果。", "恢复完成");
            }
            catch (Exception ex)
            {
                Status("恢复未完成 · 备份已保留"); Log("恢复未完成：" + ex.Message);
                NoticeDialog.Show(this, ex.Message + "\n恢复备份仍保留，可重试恢复。", "恢复未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { running = false; runButton.Enabled = true; restoreButton.Enabled = File.Exists(ProxySession.BackupPath); cleanupPanel.AllowCleanup(true); }
        }

        private async Task RunAsync()
        {
            if (running) return;
            running = true;
            timeline.ActiveStep = 0;
            timeline.Failed = false;
            operationWatch.Restart();
            animationTimer.Start();
            runButton.Enabled = false;
            restoreButton.Enabled = false;
            cleanupPanel.AllowCleanup(false);
            runButton.Text = "正在执行，请稍候…";
            Log("开始直连流程。");
            try
            {
                await Task.Run(() => RunWorkflow());
                FinishProgress(true);
                Status("完成 · 已提交 Steam 启动请求");
                Log("成功：本机代理检查通过，已提交 Steam 启动请求。");
                NoticeDialog.Show(this, "Steam 启动请求已提交，已通过本机严格检查。\n原代理设置已备份，可使用恢复按钮。不能保证排除网络侧透明代理。", "操作完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                FinishProgress(false);
                Status("已停止 · 请查看操作记录");
                Log("停止：" + ex.Message);
                NoticeDialog.Show(this, ex.Message + "\n\n为避免误用代理或中断游戏，已停止流程。可查看日志定位问题。", "未能完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                running = false;
                runButton.Enabled = true;
                restoreButton.Enabled = File.Exists(ProxySession.BackupPath);
                cleanupPanel.AllowCleanup(true);
                runButton.Text = "严格直连并重启 Steam  →";
            }
        }

        private void RunWorkflow()
        {
            AdvanceStep(0, "检查 Steam 和游戏状态...");
            if (IsProcessRunning("GameOverlayUI") || IsProcessRunning("cs2") || IsProcessRunning("csgo"))
                throw new InvalidOperationException("检测到 CS2/CSGO 或 Steam 游戏覆盖层。请退出游戏后重试。");
            string steamPath = FindSteamExecutable();
            if (string.IsNullOrEmpty(steamPath))
                throw new InvalidOperationException("未找到 Steam 安装路径。请先正常打开一次 Steam，确认注册表安装路径。软件不会启动未知文件。");
            Log("找到 Steam：" + steamPath);

            using (SteamTrust.OpenVerified(steamPath)) { }
            ProxySettings.CheckPrerequisites();
            var session = new ProxySession(ProxySession.BackupPath, new WindowsProxyAccess());
            Log("已保存恢复点，开始本机严格直连检查。");
            try
            {
            AdvanceStep(1, "正在关闭应用和 FlClash…");
            ProxyAppShutdown.Run(Log);
            ExitFlClash();
            Log("FlClash 主程序已退出或原本未运行。");

            AdvanceStep(2, "正在清除并检查代理...");
            ProxySettings.CheckProxyProcesses();
            ProxySettings.SetWinInetDirect();
            Log("系统代理已关闭。");
            ProxySettings.CleanLocalEnvironmentProxy(Log);
            ProxySettings.CheckDirect();
            Log("代理、VPN 和后台服务检查通过。");

            AdvanceStep(3, "正在关闭 Steam...");
            ShutdownSteam(steamPath);

            AdvanceStep(4, "启动前再次检查代理...");
            ProxySession.VerifyStable(ProxySettings.CheckDirect, () => Thread.Sleep(450));
            Log("连续复检通过，准备启动 Steam。");

            var start = new ProcessStartInfo(steamPath);
            start.WorkingDirectory = Path.GetDirectoryName(steamPath);
            start.UseShellExecute = false;
            foreach (string name in ProxySettings.ProxyVariableNames)
                start.EnvironmentVariables.Remove(name);
            ProxySession.VerifyAndLaunch(() => { ProxyAppShutdown.CheckStopped(); ProxySettings.CheckDirect(); }, () =>
            {
                using (SteamTrust.OpenVerified(steamPath))
                using (Process launched = Process.Start(start))
                {
                    if (launched == null) throw new InvalidOperationException("Steam 启动请求失败。");
                    Log("Steam 进程启动请求已提交，PID=" + launched.Id);
                }
            });
            session.Complete();
            }
            catch (Exception failure)
            {
                try { session.Rollback(); Log("已恢复到本次操作前的代理设置；如 FlClash 已退出，请重新打开。"); }
                catch (Exception recovery)
                {
                    throw new InvalidOperationException("流程停止：" + failure.Message + "\n自动恢复未完成：" + recovery.Message + "\n恢复备份已保留，请使用恢复按钮重试。", new AggregateException(failure, recovery));
                }
                throw;
            }
            AdvanceStep(5, "已完成；文件清理仅由手动触发。");
        }

        private void ExitFlClash()
        {
            FlClashShutdown.Run(Log);
        }

        private static bool IsProcessRunning(string name)
        {
            Process[] processes = Process.GetProcessesByName(name);
            try { return processes.Length != 0; }
            finally { foreach (Process process in processes) process.Dispose(); }
        }

        private static void WaitForProcessesToExit(string name, int timeoutMs)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                if (!IsProcessRunning(name)) return;
                Thread.Sleep(250);
            }
        }

        private void ShutdownSteam(string steamPath)
        {
            if (IsProcessRunning("GameOverlayUI") || IsProcessRunning("cs2") || IsProcessRunning("csgo"))
                throw new InvalidOperationException("检测到游戏正在运行；不会关闭 Steam。请先退出游戏。");
            if (!IsProcessRunning("steam"))
            {
                Log("Steam 原本未运行，不需要关闭。");
                return;
            }
            Log("通过 Steam 的 -shutdown 参数请求正常退出。");
            try
            {
                using (SteamTrust.OpenVerified(steamPath))
            using (Process p = Process.Start(new ProcessStartInfo(steamPath, "-shutdown")
                {
                    WorkingDirectory = Path.GetDirectoryName(steamPath),
                    UseShellExecute = false,
                    CreateNoWindow = true
                })) { }
            }
            catch (Exception ex) { throw new InvalidOperationException("无法发送 Steam 退出请求：" + ex.Message, ex); }
            WaitForProcessesToExit("steam", 16000);
            if (IsProcessRunning("steam"))
                throw new InvalidOperationException("Steam 未能正常退出。请在任务栏托盘中右键 Steam → 退出，然后重新点击按钮（不会强杀 Steam 或游戏）。");
            Thread.Sleep(1200);
            Log("Steam 已退出。");
        }

        private static string FindSteamExecutable()
        {
            foreach (Process p in Process.GetProcessesByName("steam"))
            using (p) {
                try { string live = p.MainModule.FileName; if (File.Exists(live) && string.Equals(Path.GetFileName(live), "steam.exe", StringComparison.OrdinalIgnoreCase)) return Path.GetFullPath(live); } catch { }
            }
            var candidates = new List<string>();
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    if (key != null)
                    {
                        string steamExe = key.GetValue("SteamExe") as string;
                        string steamPath = key.GetValue("SteamPath") as string;
                        if (!string.IsNullOrEmpty(steamExe)) candidates.Add(steamExe);
                        if (!string.IsNullOrEmpty(steamPath)) candidates.Add(Path.Combine(steamPath.Replace('/', '\\'), "steam.exe"));
                    }
                }
            }
            catch { }
            string[] roots = {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"C:\Steam", @"D:\Steam", @"E:\Steam"
            };
            foreach (string root in roots) if (!string.IsNullOrEmpty(root)) candidates.Add(Path.Combine(root, "Steam", "steam.exe"));
            candidates.Add(@"C:\Steam\steam.exe"); candidates.Add(@"D:\Steam\steam.exe"); candidates.Add(@"E:\Steam\steam.exe");
            foreach (string candidate in candidates)
            {
                try
                {
                    string expanded = Environment.ExpandEnvironmentVariables(candidate.Trim('"'));
                    if (File.Exists(expanded) && string.Equals(Path.GetFileName(expanded), "steam.exe", StringComparison.OrdinalIgnoreCase))
                        return Path.GetFullPath(expanded);
                }
                catch { }
            }
            return null;
        }
    }

    internal class RoundedPanel : Panel
    {
        internal float CornerRadius = UiStyle.CardRadius;
        internal RoundedPanel() { DoubleBuffered = true; }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Width < 2 || Height < 2) return;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent == null ? BackColor : Parent.BackColor);
            float radius = Math.Min(CornerRadius * DeviceDpi / 96F, (Math.Min(Width, Height) - 1) / 2F);
            var rect = new RectangleF(0, 0, Width - 1, Height - 1);
            using (var path = UiStyle.RoundRect(rect, radius))
            {
                using (var brush = new SolidBrush(BackColor)) e.Graphics.FillPath(brush, path);
                using (var border = new Pen(AppTheme.Edge, 1F))
                    e.Graphics.DrawPath(border, path);
            }
        }
    }

    internal sealed class StepTimeline : Control
    {
        internal int ActiveStep = -1;
        internal bool Failed;
        internal int Angle;
        private readonly string[] titles = { "准备检查", "关闭应用与代理", "核验本机代理", "退出 Steam", "复检并启动", "完成检查" };
        private readonly string[] descriptions = { "安装路径与游戏状态", "关闭指定应用和 FlClash", "系统代理 / VPN / 内核", "等待客户端正常退出", "再次核验后提交启动", "清理需单独预览和确认" };
        internal int StepCount { get { return titles.Length; } }
        internal StepTimeline() { DoubleBuffered = true; BackColor = Color.White; TabStop = false; AccessibleName = "直连执行步骤"; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            float scale = DeviceDpi / 96F;
            g.ScaleTransform(scale, scale);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            float gap = Math.Min(76, (Height / scale - 60) / titles.Length);
            var small = UiStyle.DiagramCaption;
            var normal = UiStyle.DiagramTitle;
            using (var muted = new SolidBrush(AppTheme.Text(AppTheme.Muted)))
            using (var ink = new SolidBrush(AppTheme.Text(AppTheme.Ink)))
            {
                g.DrawString("直连流程", normal, ink, 4, 4);
                g.DrawString("共 " + StepCount + " 步", small, muted, 4, 23);
                for (int i = 0; i < titles.Length; i++)
                {
                    float y = 56 + i * gap;
                    bool done = ActiveStep > i;
                    bool active = ActiveStep == i;
                    Color accent = active && Failed ? AppTheme.Error : done ? AppTheme.Success : AppTheme.Accent;
                    if (i < titles.Length - 1)
                    {
                        using (var line = new Pen(done ? AppTheme.Success : AppTheme.Edge, 2))
                            g.DrawLine(line, 18, y + 16, 18, y + gap - 16);
                    }
                    using (var fill = new SolidBrush(done || active && Failed ? accent : active ? AppTheme.Background(AppTheme.Accent) : AppTheme.Background(AppTheme.Pill)))
                        g.FillEllipse(fill, 5, y - 13, 26, 26);
                    using (var white = new Pen(active && !Failed ? AppTheme.AccentForeground : Color.White, 2))
                    {
                        if (done) g.DrawLines(white, new PointF[] { new PointF(12, y), new PointF(16, y + 4), new PointF(24, y - 5) });
                        else if (active && !Failed) g.DrawArc(white, 11, y - 7, 14, 14, Angle, 245);
                        else if (active)
                        {
                            g.DrawLine(white, 14, y - 4, 22, y + 4);
                            g.DrawLine(white, 22, y - 4, 14, y + 4);
                        }
                        else g.DrawString((i + 1).ToString(), small, muted, 14, y - 8);
                    }
                    using (var titleBrush = new SolidBrush(AppTheme.Text(active || done ? accent : AppTheme.Ink)))
                        g.DrawString(titles[i], normal, titleBrush, 42, y - 15);
                    g.DrawString(active ? (Failed ? "已停止 · 查看右侧原因" : "正在处理…") : done ? (i == titles.Length - 1 ? "已结束，结果见日志" : "已完成") : descriptions[i], small, muted, 42, y + 7);
                }
            }
        }
    }
}




