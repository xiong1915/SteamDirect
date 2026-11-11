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
        private static string CrashLog { get { return Path.Combine(LogStore.DirectoryPath, DateTime.Today.ToString("yyyy-MM-dd") + "-crash.log"); } }

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
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var singleInstance = new Mutex(false, @"Local\SteamDirect.Desktop"))
                {
                    bool owned;
                    try { owned = singleInstance.WaitOne(0); }
                    catch (AbandonedMutexException) { owned = true; }
                    if (!owned)
                    {
                        WindowMessages.PostMessage(new IntPtr(0xffff), WindowMessages.ShowMain, IntPtr.Zero, IntPtr.Zero);
                        return;
                    }
                    try { Application.Run(new MainWindow(Array.IndexOf(arguments, "--background") >= 0)); }
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
            SuspendLayout();
            AppTheme.IsDark = preferences.NightMode;
            AppTheme.TryColor(preferences.ThemeColor, out AppTheme.ThemeColor);
            Text = "Steam 直连助手 v2.5.3";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 10F);
            BackColor = AppTheme.Page;
            ForeColor = AppTheme.Ink;
            ClientSize = new Size(1180, 780);
            MinimumSize = new Size(1080, 760);
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;

            BuildNavigation();
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 20, 24, 20), ColumnCount = 1, RowCount = 3 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 402));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            dashboard = root;
            pageHost.Controls.Add(root);
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            header.Controls.Add(new Label { Text = "概览", Font = new Font(Font.FontFamily, 22F), Dock = DockStyle.Fill }, 0, 0);
            header.Controls.Add(new Label { Text = "准备好网络，再开始游戏。", Dock = DockStyle.Fill, ForeColor = AppTheme.Muted }, 0, 1);
            root.Controls.Add(header, 0, 0);

            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 238));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.Controls.Add(body, 0, 1);
            var left = new RoundedPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(14, 18, 14, 18), Margin = new Padding(0, 0, 20, 0) };
            timeline.Dock = DockStyle.Fill;
            left.Controls.Add(timeline);
            body.Controls.Add(left, 0, 0);

            var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 240));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            body.Controls.Add(content, 1, 0);
            var action = new RoundedPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(22, 18, 22, 18), Margin = Padding.Empty };
            var actionLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
            actionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            actionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            actionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            actionLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            actionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            actionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            elapsedLabel.Text = "●  等待执行";
            elapsedLabel.ForeColor = AppTheme.Muted;
            elapsedLabel.Font = new Font(Font.FontFamily, 9F, FontStyle.Bold);
            elapsedLabel.Dock = DockStyle.Fill;
            actionLayout.Controls.Add(elapsedLabel, 0, 0);
            statusLabel.Text = "准备开始直连";
            statusLabel.Dock = DockStyle.Fill;
            statusLabel.AutoEllipsis = true;
            statusLabel.Font = new Font(Font.FontFamily, 17F, FontStyle.Bold);
            actionLayout.Controls.Add(statusLabel, 0, 1);
            actionLayout.Controls.Add(new Label { Text = "先退出游戏。通过本机代理检查后，才会重新启动 Steam。", Dock = DockStyle.Fill, ForeColor = AppTheme.Muted }, 0, 2);
            runButton.Text = "严格直连并重启 Steam";
            runButton.Dock = DockStyle.Fill;
            runButton.Margin = Padding.Empty;
            AppTheme.ActionButton(runButton, true);
            runButton.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
            runButton.Cursor = Cursors.Hand;
            runButton.TabIndex = 0;
            runButton.Click += async (sender, args) => await RunAsync();
            actionLayout.Controls.Add(runButton, 0, 3);
            restoreButton.Text = "恢复原代理设置";
            restoreButton.Dock = DockStyle.Fill;
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
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.Controls.Add(footer, 0, 2);
            var logHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 0, 6) };
            logHeader.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            logHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            logHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
            logHeader.Controls.Add(new Label { Text = "操作记录", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font, FontStyle.Bold) }, 0, 0);
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

            var logFrame = new RoundedPanel { Dock = DockStyle.Fill, Padding = new Padding(16), BackColor = Color.White, Margin = Padding.Empty };
            logBox.Dock = DockStyle.Fill;
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.BackColor = Color.White;
            logBox.ForeColor = Color.FromArgb(87, 104, 128);
            logBox.Font = new Font(Font.FontFamily, 9F);
            logBox.BorderStyle = BorderStyle.None;
            logBox.AccessibleName = "操作记录";
            logFrame.Controls.Add(logBox);
            footer.Controls.Add(logFrame, 0, 1);
            animationTimer.Interval = 40;
            animationTimer.Tick += (sender, args) =>
            {
                timeline.Angle = (timeline.Angle + 9) % 360;
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
                TextAlign = ContentAlignment.MiddleCenter, Font = new Font(Font.FontFamily, 11F, FontStyle.Bold) }, 0, 1);
            rail.Controls.Add(brand, 0, 0);
            string[] pages = { "概览", "网络诊断", "游戏记录", "设置" };
            for (int i = 0; i < pages.Length; i++)
            {
                string page = pages[i];
                var button = new RoundedButton { Text = page, Dock = DockStyle.Fill, Margin = new Padding(0, 5, 0, 5),
                    AccessibleName = page + "页面", TabIndex = i };
                AppTheme.ActionButton(button, false);
                button.Click += (sender, args) => NavigateTo(page);
                navigation.Add(page, button);
                rail.Controls.Add(button, 0, i + 1);
            }
            rail.Controls.Add(new Label { Text = "v2.5.3 · 游戏网络助手", ForeColor = AppTheme.Muted,
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomCenter, Font = new Font(Font.FontFamily, 8F) }, 0, 6);
            shell.Controls.Add(rail, 0, 0);
            shell.Controls.Add(pageHost, 1, 0);
            Controls.Add(shell);
            SelectNavigation("概览");
        }

        private void SelectNavigation(string page)
        {
            foreach (var entry in navigation)
                AppTheme.SetBackground(entry.Value, entry.Key == page ? AppTheme.Pill : AppTheme.Page);
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
                    preferences.RetentionDays, preferences.NightMode, preferences.ThemeColor);
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
                    if (dialog.AutoStart != StartupSettings.IsEnabled()) StartupSettings.SetEnabled(dialog.AutoStart);
                    UserPreferences.Save(UserPreferences.DefaultPath, dialog.TrayEnabled, dialog.RetentionDays, dialog.NightMode, dialog.ThemeColor);
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
            AppTheme.SetForeground(statusLabel, message.StartsWith("已停止") ? Color.FromArgb(191, 72, 57) :
                message.StartsWith("完成") ? Color.FromArgb(24, 137, 110) : Color.FromArgb(28, 39, 60));

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
            ProxySession.VerifyAndLaunch(ProxySettings.CheckDirect, () =>
            {
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
            AdvanceStep(5, "正在清理过期临时文件…");
            try { var cleanup = TempCleaner.CleanDefault();
                Log(cleanup.Describe() + cleanup.Details());
                RefreshCleanupStats(); }
            catch (Exception ex) { Log("过期临时文件清理未完成：" + ex.Message + "；Steam 启动不受影响。"); }
        }

        private async void RefreshCleanupStats()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(RefreshCleanupStats)); return; }
            await cleanupPanel.RefreshEstimateAsync();
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
            {
                Log("等待 Steam 正常退出超时，再尝试关闭其窗口。");
                foreach (Process p in Process.GetProcessesByName("steam"))
                {
                    try { p.CloseMainWindow(); } catch { }
                }
                WaitForProcessesToExit("steam", 5000);
            }
            if (IsProcessRunning("steam"))
                throw new InvalidOperationException("Steam 未能正常退出。请在任务栏托盘中右键 Steam → 退出，然后重新点击按钮（不会强杀 Steam 或游戏）。");
            Thread.Sleep(1200);
            Log("Steam 已退出。");
        }

        private static string FindSteamExecutable()
        {
            foreach (Process p in Process.GetProcessesByName("steam"))
            {
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

    internal static class ProxySettings
    {
        // Proxy environment variables commonly honored by Steam/embedded libraries.
        internal static readonly string[] ProxyVariableNames = {
            "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "FTP_PROXY", "WS_PROXY", "WSS_PROXY"
        };

        [StructLayout(LayoutKind.Sequential)]
        private struct ConnOption { public int Option; public IntPtr Value; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ConnOptionList
        {
            public int Size;
            public IntPtr Connection;
            public int OptionCount;
            public int OptionError;
            public IntPtr Options;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct WinHttpProxyInfo
        {
            public int AccessType;
            public IntPtr Proxy;
            public IntPtr ProxyBypass;
        }
        [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
        private static extern bool SetConnOption(IntPtr handle, int option, ref ConnOptionList data, int length);
        [DllImport("wininet.dll", EntryPoint = "InternetQueryOptionW", SetLastError = true)]
        private static extern bool QueryConnOption(IntPtr handle, int option, ref ConnOptionList data, ref int length);
        [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
        private static extern bool NotifyOption(IntPtr handle, int option, IntPtr data, int length);
        [DllImport("winhttp.dll", SetLastError = true)]
        private static extern bool WinHttpGetDefaultProxyConfiguration(out WinHttpProxyInfo info);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalFree(IntPtr memory);

        internal static void SetWinInetDirect()
        {
            // WinINet supported API; don't edit undocumented DefaultConnectionSettings bytes.
            ApplyConnFlags(1); // PROXY_TYPE_DIRECT; clears manual/PAC/WPAD active flags.
            RefreshConnection();
        }

        private static void RefreshConnection()
        {
            if (!NotifyOption(IntPtr.Zero, 39, IntPtr.Zero, 0) || !NotifyOption(IntPtr.Zero, 37, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 未能刷新系统代理设置。");
            Thread.Sleep(350);
        }

        private static void ApplyConnFlags(int flags)
        {
            int itemSize = Marshal.SizeOf(typeof(ConnOption));
            IntPtr optionPointer = Marshal.AllocHGlobal(itemSize);
            try
            {
                Marshal.StructureToPtr(new ConnOption { Option = 1, Value = new IntPtr(flags) }, optionPointer, false);
                ConnOptionList options = new ConnOptionList {
                    Size = Marshal.SizeOf(typeof(ConnOptionList)), Connection = IntPtr.Zero,
                    OptionCount = 1, OptionError = 0, Options = optionPointer
                };
                if (!SetConnOption(IntPtr.Zero, 75, ref options, options.Size))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法修改当前用户 Windows 系统代理（可能被组织策略管理）。");
            }
            finally { Marshal.FreeHGlobal(optionPointer); }
        }

        internal static ProxySnapshot CaptureConnection()
        {
            int itemSize = Marshal.SizeOf(typeof(ConnOption));
            IntPtr memory = Marshal.AllocHGlobal(itemSize * 4);
            int[] options = { 10, 2, 3, 4 };
            for (int i = 0; i < 4; i++) Marshal.StructureToPtr(new ConnOption { Option = options[i], Value = IntPtr.Zero }, IntPtr.Add(memory, i * itemSize), false);
            try
            {
                var list = new ConnOptionList { Size = Marshal.SizeOf(typeof(ConnOptionList)), OptionCount = 4, Options = memory };
                int length = list.Size;
                if (!QueryConnOption(IntPtr.Zero, 75, ref list, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法完整备份系统代理；未执行切换。");
                var result = new ProxySnapshot();
                for (int i = 0; i < 4; i++)
                {
                    var option = (ConnOption)Marshal.PtrToStructure(IntPtr.Add(memory, i * itemSize), typeof(ConnOption));
                    if (i == 0) { result.Flags = (int)option.Value.ToInt64(); continue; }
                    string value = option.Value == IntPtr.Zero ? "" : Marshal.PtrToStringUni(option.Value);
                    if (i == 1) result.Server = value;
                    if (i == 2) result.Bypass = value;
                    if (i == 3) result.AutoConfigUrl = value;
                }
                return result;
            }
            finally
            {
                for (int i = 1; i < 4; i++)
                {
                    var option = (ConnOption)Marshal.PtrToStructure(IntPtr.Add(memory, i * itemSize), typeof(ConnOption));
                    if (option.Value != IntPtr.Zero) GlobalFree(option.Value);
                }
                Marshal.FreeHGlobal(memory);
            }
        }

        internal static void RestoreConnection(ProxySnapshot snapshot)
        {
            snapshot.Validate();
            int itemSize = Marshal.SizeOf(typeof(ConnOption));
            IntPtr memory = Marshal.AllocHGlobal(itemSize * 4);
            IntPtr[] values = new IntPtr[3];
            try
            {
                string[] strings = { snapshot.Server, snapshot.Bypass, snapshot.AutoConfigUrl };
                Marshal.StructureToPtr(new ConnOption { Option = 1, Value = new IntPtr(snapshot.Flags) }, memory, false);
                for (int i = 0; i < 3; i++)
                {
                    values[i] = Marshal.StringToHGlobalUni(strings[i]);
                    Marshal.StructureToPtr(new ConnOption { Option = i + 2, Value = values[i] }, IntPtr.Add(memory, (i + 1) * itemSize), false);
                }
                var list = new ConnOptionList { Size = Marshal.SizeOf(typeof(ConnOptionList)), OptionCount = 4, Options = memory };
                if (!SetConnOption(IntPtr.Zero, 75, ref list, list.Size)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法恢复系统代理，备份已保留。");
                RefreshConnection();
            }
            finally
            {
                foreach (IntPtr value in values) if (value != IntPtr.Zero) Marshal.FreeHGlobal(value);
                Marshal.FreeHGlobal(memory);
            }
        }

        private static int ReadConnFlags()
        {
            int itemSize = Marshal.SizeOf(typeof(ConnOption));
            IntPtr optionPointer = Marshal.AllocHGlobal(itemSize);
            try
            {
                Marshal.StructureToPtr(new ConnOption { Option = 10, Value = IntPtr.Zero }, optionPointer, false);
                ConnOptionList options = new ConnOptionList {
                    Size = Marshal.SizeOf(typeof(ConnOptionList)), Connection = IntPtr.Zero,
                    OptionCount = 1, OptionError = 0, Options = optionPointer
                };
                int length = options.Size;
                if (!QueryConnOption(IntPtr.Zero, 75, ref options, ref length))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取系统代理有效状态。");
                ConnOption result = (ConnOption)Marshal.PtrToStructure(optionPointer, typeof(ConnOption));
                return unchecked((int)result.Value.ToInt64());
            }
            finally { Marshal.FreeHGlobal(optionPointer); }
        }

        internal static string ObserveHistoryMode(bool tunnel, bool route, bool knownProxy)
        {
            if (tunnel) return "代理";
            try
            {
                int flags = ReadConnFlags();
                if ((flags & 6) != 0) return "代理";
                foreach (string variable in ProxyVariableNames)
                {
                    foreach (var level in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
                        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable, level))) return "代理";
                }
                WinHttpProxyInfo info;
                if (!WinHttpGetDefaultProxyConfiguration(out info)) return "未知";
                try
                {
                    if (info.Proxy != IntPtr.Zero && !string.IsNullOrWhiteSpace(Marshal.PtrToStringUni(info.Proxy))) return "代理";
                    return route && flags == 1 && !knownProxy && info.AccessType == 1 ? "直连" : "未知";
                }
                finally
                {
                    if (info.Proxy != IntPtr.Zero) GlobalFree(info.Proxy);
                    if (info.ProxyBypass != IntPtr.Zero) GlobalFree(info.ProxyBypass);
                }
            }
            catch (Exception) { return "未知"; }
        }

        private static void CheckWinHttp()
        {
            WinHttpProxyInfo info;
            if (!WinHttpGetDefaultProxyConfiguration(out info))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取 WinHTTP 代理配置。");
            try
            {
                if (info.AccessType != 1 || info.Proxy != IntPtr.Zero)
                    throw new InvalidOperationException("检测到 WinHTTP 系统级代理仍生效。未修改系统级配置；请使用管理员终端核实 netsh winhttp show proxy 后手动处理。");
            }
            finally
            {
                if (info.Proxy != IntPtr.Zero) GlobalFree(info.Proxy);
                if (info.ProxyBypass != IntPtr.Zero) GlobalFree(info.ProxyBypass);
            }
            // Newer Windows versions also have advanced WinHTTP proxy settings.
            try
            {
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"), "winhttp show advproxy") {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using (Process p = Process.Start(psi))
                {
                    string result = ReadProcessOutput(p, 3500);
                    if (p.ExitCode != 0) throw new InvalidOperationException("高级代理查询未成功，无法确认直连；已停止检查。");
                    ValidateAdvancedProxy(result);
                }
            }
            catch (Win32Exception ex) { throw new InvalidOperationException("无法运行高级代理检查，不能确认直连状态。", ex); }
        }

        internal static void ValidateAdvancedProxy(string result)
        {
            Match proxy = Regex.Match(result, "\"Proxy\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
            Match pac = Regex.Match(result, "\"AutoconfigUrl\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
            Match proxyEnabled = Regex.Match(result, "\"ProxyIsEnabled\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
            Match pacEnabled = Regex.Match(result, "\"AutoConfigIsEnabled\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
            Match auto = Regex.Match(result, "\"AutoDetect\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
            bool activeProxy = proxyEnabled.Success ? string.Equals(proxyEnabled.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase) :
                proxy.Success && !string.IsNullOrWhiteSpace(proxy.Groups[1].Value);
            bool activePac = pacEnabled.Success ? string.Equals(pacEnabled.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase) :
                pac.Success && !string.IsNullOrWhiteSpace(pac.Groups[1].Value);
            bool activeAuto = auto.Success && string.Equals(auto.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
            if (activeProxy || activePac || activeAuto)
                throw new InvalidOperationException("WinHTTP 高级代理仍启用（代理/PAC/自动检测）。请检查其他代理程序或系统策略。");
            bool complete = (proxyEnabled.Success && pacEnabled.Success && auto.Success) || (proxy.Success && pac.Success && auto.Success);
            if (!complete &&
                !Regex.IsMatch(result, "Direct access|直接访问|直连|直接存取", RegexOptions.IgnoreCase))
                throw new InvalidOperationException("无法识别 WinHTTP 高级代理状态，请先核实配置。");
        }

        internal static string ReadProcessOutput(Process process, int timeoutMs)
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            var watch = Stopwatch.StartNew();
            if (!process.WaitForExit(timeoutMs) ||
                !Task.WaitAll(new Task[] { output, error }, Math.Max(0, timeoutMs - (int)watch.ElapsedMilliseconds)))
            {
                try { process.Kill(); } catch { }
                throw new System.TimeoutException("WinHTTP 高级代理查询超时，已停止查询。");
            }
            return output.Result + error.Result;
        }

        internal static void CheckPrerequisites()
        {
            // Proxy absence is verified after FlClash exits and user settings are cleared.
            foreach (string name in ProxyVariableNames)
            {
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine)))
                    throw new InvalidOperationException("机器级环境变量 " + name + " 设置了代理；未修改网络，请先核实。");
                string user = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
                if (!string.IsNullOrWhiteSpace(user) && !IsFlClashLocalProxy(user))
                    throw new InvalidOperationException("用户环境变量 " + name + " 指向其他代理；未修改网络，请先核实。");
            }
        }
        private static bool IsFlClashLocalProxy(string value)
        {
            // Never remove remote/corporate proxy values silently.
            return Regex.IsMatch(value.Trim(), @"^(?:(?:http|https|socks|socks5)://)?(?:127\.0\.0\.1|localhost|\[::1\]):\d+/?$", RegexOptions.IgnoreCase);
        }

        internal static void CleanLocalEnvironmentProxy(Action<string> log)
        {
            foreach (string name in ProxyVariableNames)
            {
                string machine = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
                if (!string.IsNullOrWhiteSpace(machine))
                    throw new InvalidOperationException("机器级环境变量 " + name + " 设置了代理。未覆盖其他配置，请先人工核实清除。");
                string user = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
                if (!string.IsNullOrWhiteSpace(user))
                {
                    if (!IsFlClashLocalProxy(user))
                        throw new InvalidOperationException("用户环境变量 " + name + " 仍指向其他代理。请检查该变量的配置。");
                    Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.User);
                    log("已清除用户级本地代理环境变量：" + name);
                }
                // This process may itself have inherited stale environment variables.
                Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process);
            }
        }

        private static void CheckEnvironment()
        {
            foreach (string name in ProxyVariableNames)
            {
                foreach (EnvironmentVariableTarget level in new[] {
                    EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
                {
                    string current = Environment.GetEnvironmentVariable(name, level);
                    if (!string.IsNullOrWhiteSpace(current))
                        throw new InvalidOperationException("检测到 " + level + " 环境变量 " + name + " 仍指向代理，已停止。");
                }
            }
        }

        private static void CheckTunnels()
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                string text = adapter.Name + " " + adapter.Description;
                if (adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Ppp ||
                    Regex.IsMatch(text, @"(?i)Wintun|WireGuard|Tailscale|OpenVPN|Clash|Mihomo|ZeroTier|SoftEther|TAP-Windows|NordLynx"))
                    throw new InvalidOperationException("发现活跃 VPN/TUN 网卡：" + adapter.Name + "。未自动禁用网卡，请先关闭对应 VPN/TUN，再重试。");
            }
        }

        internal static string DiagnoseProxyState()
        {
            var issues = new List<string>();
            Action[] checks = {
                () => { int flags = ReadConnFlags(); if (flags != 1) throw new InvalidOperationException("Windows 系统代理/PAC/自动检测仍启用（标志 " + flags + "）"); },
                CheckWinHttp, CheckEnvironment, CheckTunnels, CheckProxyProcesses, CheckProxyPorts
            };
            foreach (var check in checks)
            {
                try { check(); }
                catch (Exception ex) { issues.Add(ex.Message); }
            }
            return issues.Count == 0 ? null : string.Join("；", issues);
        }

        internal static void CheckDirect()
        {
            int flags = ReadConnFlags();
            if (flags != 1)
                throw new InvalidOperationException("Windows WinINet 当前未处于纯直连模式（标志：" + flags + "）。");
            CheckWinHttp();
            CheckEnvironment();
            CheckTunnels();
            CheckProxyProcesses();
            CheckProxyPorts();
        }

        private static readonly HashSet<string> ProxyComponents = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "FlClash", "FlClashCore", "clash", "clashmeta", "clashpremium", "clashwin64", "ClashforWindows",
            "clashverge", "clashvergeservice", "vergemihomo", "vergemihomoalpha", "mihomo", "mihomoparty", "mihomoservice",
            "singbox", "v2rayN", "v2ray", "xray", "Shadowsocks", "ShadowsocksR", "sslocal", "ssrlocal",
            "trojan", "hysteria", "hysteria2", "naive", "naiveproxy", "nekoray", "nekobox",
            "Proxifier", "ProxyCap", "tun2socks", "OpenVPN", "openvpnserv", "openvpnserv2", "OpenVPNService",
            "WireGuard", "Tailscale", "tailscaled", "ZeroTierOne", "vpnclient", "vpnclientx64"
        };

        internal static bool IsProxyComponent(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            return ProxyComponents.Contains(name.Replace("-", "").Replace("_", "").Replace(" ", ""));
        }

        internal static void CheckProxyProcesses()
        {
            var found = new List<string>();
            Process[] processes = Process.GetProcesses();
            try
            {
                foreach (Process process in processes)
                {
                    try
                    {
                        string name = process.ProcessName;
                        if (IsProxyComponent(name)) found.Add(name + " (PID " + process.Id + ")");
                    }
                    catch (InvalidOperationException) { } // Process exited during the snapshot.
                }
            }
            finally { foreach (Process process in processes) process.Dispose(); }
            if (found.Count > 0)
                throw new InvalidOperationException("仍有代理/VPN 程序或内核运行：" + string.Join("、", found) + "。请正常退出后重试。");
            CheckProxyServices();
        }

        private static void CheckProxyServices()
        {
            var found = new List<string>();
            ServiceController[] services = ServiceController.GetServices();
            try
            {
                foreach (ServiceController service in services)
                {
                    string name = service.ServiceName;
                    if (!IsProxyComponent(name) && !IsProxyComponent(service.DisplayName) &&
                        !name.StartsWith("WireGuardTunnel$", StringComparison.OrdinalIgnoreCase)) continue;
                    if (service.Status != ServiceControllerStatus.Stopped)
                        found.Add(service.DisplayName + " [" + name + "]");
                }
            }
            finally { foreach (ServiceController service in services) service.Dispose(); }
            if (found.Count > 0)
                throw new InvalidOperationException("代理/VPN 后台服务尚未停止：" + string.Join("、", found) + "。请通过对应软件退出服务模式。");
        }

        private static void CheckProxyPorts()
        {
            // A still-listening Clash core can remain after the Flutter GUI exits.
            // FlClash standard HTTP/mixed ports; do not kill whichever program owns the port.
            foreach (System.Net.IPEndPoint ep in System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            {
                if (ep.Port == 7890 || ep.Port == 7891)
                    throw new InvalidOperationException("本地端口 " + ep.Port + " 仍有程序监听，可能是代理程序。请检查 FlClash/其他代理程序后重试。");
            }
        }
    }
}

namespace SteamDirect
{
    internal sealed class RoundedPanel : Panel
    {
        internal RoundedPanel() { DoubleBuffered = true; }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Width < 2 || Height < 2) return;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent == null ? BackColor : Parent.BackColor);
            float radius = Math.Min(20F * e.Graphics.DpiX / 96F, (Math.Min(Width, Height) - 1) / 2F);
            var rect = new RectangleF(0, 0, Width - 1, Height - 1);
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                float d = radius * 2;
                path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
                path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
                path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
                path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                using (var brush = new SolidBrush(BackColor)) e.Graphics.FillPath(brush, path);
            }
        }
    }

    internal sealed class StepTimeline : Control
    {
        internal int ActiveStep = -1;
        internal bool Failed;
        internal int Angle;
        private readonly string[] titles = { "准备检查", "关闭应用与代理", "核验本机代理", "退出 Steam", "复检并启动", "清理临时文件" };
        private readonly string[] descriptions = { "安装路径与游戏状态", "关闭指定应用和 FlClash", "系统代理 / VPN / 内核", "等待客户端正常退出", "再次核验后提交启动", "仅过期文件，跳过占用" };
        internal int StepCount { get { return titles.Length; } }
        internal StepTimeline() { DoubleBuffered = true; BackColor = Color.White; TabStop = false; AccessibleName = "直连执行步骤"; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            float scale = g.DpiX / 96F;
            g.ScaleTransform(scale, scale);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            float gap = Math.Min(76, (Height / scale - 60) / titles.Length);
            using (var small = new Font("Microsoft YaHei UI", 11F, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var normal = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold, GraphicsUnit.Pixel))
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
                    Color accent = active && Failed ? Color.FromArgb(207, 78, 61) : done ? Color.FromArgb(27, 153, 121) : AppTheme.Accent;
                    if (i < titles.Length - 1)
                    {
                        using (var line = new Pen(done ? Color.FromArgb(90, 155, 133) : AppTheme.Edge, 2))
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
        internal bool TrayEnabled = true;
        internal int RetentionDays = 7;
        internal bool NightMode;
        internal string ThemeColor = "#6750A4";
        internal static UserPreferences Load(string path)
        {
            var result = new UserPreferences();
            if (!File.Exists(path)) return result;
            foreach (string line in File.ReadAllLines(path))
            {
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
        internal static void Save(string path, bool trayEnabled, int retentionDays, bool nightMode = false, string themeColor = "#6750A4")
        {
            if (retentionDays < 1 || retentionDays > 365) throw new ArgumentOutOfRangeException("retentionDays");
            Color color;
            if (!AppTheme.TryColor(themeColor, out color)) throw new ArgumentException("主题色格式应为 #RRGGBB。", "themeColor");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, new[] { "TrayEnabled=" + trayEnabled, "RetentionDays=" + retentionDays, "NightMode=" + nightMode,
                "ThemeColor=" + AppTheme.ColorCode(color) }, Encoding.UTF8);
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
        internal const string DirectoryPath = @"D:\SteamDirect\Logs";
        internal static string CurrentPath { get { return Path.Combine(DirectoryPath, DateTime.Today.ToString("yyyy-MM-dd") + ".log"); } }
        internal static int Cleanup(string directory, int retentionDays, DateTime today)
        {
            if (retentionDays < 1 || retentionDays > 365) throw new ArgumentOutOfRangeException("retentionDays");
            if (!Directory.Exists(directory)) return 0;
            int removed = 0;
            foreach (string path in Directory.GetFiles(directory, "*.log", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileNameWithoutExtension(path);
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
        private Color selectedColor;
        internal bool TrayEnabled { get { return tray.Checked; } }
        internal bool AutoStart { get { return startup.Checked; } }
        internal bool NightMode { get { return night.Checked; } }
        internal int RetentionDays { get { return (int)days.Value; } }
        internal string ThemeColor { get { return colorCode.Text.Trim().ToUpperInvariant(); } }
        internal SettingsWindow(bool trayEnabled, bool autoStart, int retentionDays, bool nightMode = false, string themeColor = "#6750A4")
        {
            SuspendLayout();
            Text = "设置";
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 10F);
            BackColor = AppTheme.Page;
            ForeColor = AppTheme.Ink;
            ClientSize = new Size(580, 652);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            var layout = new TableLayoutPanel { Dock = DockStyle.Top, Height = 652, Padding = new Padding(24, 20, 24, 20), ColumnCount = 1, RowCount = 7 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 64, 156, 124, 164, 44, 26 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.Controls.Add(new Label { Text = "设置", Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 22F) }, 0, 0);

            var appearance = Section(layout, 1, "外观", 28, 32, 42);
            night.Text = "启用夜间模式";
            night.Checked = nightMode;
            night.Dock = DockStyle.Fill;
            appearance.Controls.Add(night, 0, 1);
            appearance.Controls.Add(BuildColorPicker(themeColor), 0, 2);
            appearance.Controls.Add(new Label { Text = "主题色支持 #RRGGBB，保存后应用到整个界面。", Dock = DockStyle.Fill,
                ForeColor = AppTheme.Muted, AutoEllipsis = true, Font = new Font(Font.FontFamily, 9F) }, 0, 3);

            var behavior = Section(layout, 2, "启动与托盘", 29, 31);
            tray.Text = "显示托盘图标，关闭窗口时留在后台";
            tray.Checked = trayEnabled;
            tray.Dock = DockStyle.Fill;
            startup.Text = "登录 Windows 时自动启动";
            startup.Checked = autoStart;
            startup.Dock = DockStyle.Fill;
            behavior.Controls.Add(tray, 0, 1);
            behavior.Controls.Add(startup, 0, 2);

            var logs = Section(layout, 3, "日志管理", 27, 29, 32);
            logs.Controls.Add(new Label { Text = LogStore.DirectoryPath, AccessibleName = "日志目录 " + LogStore.DirectoryPath,
                Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = AppTheme.Muted }, 0, 1);
            var retention = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            retention.Controls.Add(new Label { Text = "保留天数（1–365 天）", Width = 170,
                Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleLeft });
            days.Minimum = 1; days.Maximum = 365; days.Value = retentionDays; days.Width = 100;
            days.Location = new Point(176, 3);
            days.AccessibleName = "日志保留天数";
            retention.Controls.Add(days);
            logs.Controls.Add(retention, 0, 2);
            var cleanup = new RoundedButton { Text = "清理过期日志", Dock = DockStyle.Left, Width = 160, Margin = Padding.Empty };
            AppTheme.ActionButton(cleanup, false);
            cleanup.Click += (sender, args) =>
            {
                try { int count = LogStore.Cleanup(LogStore.DirectoryPath, RetentionDays, DateTime.Today); NoticeDialog.Show(this, "已清理 " + count + " 个过期日志文件，保留最近 " + RetentionDays + " 天。", "日志清理"); }
                catch (Exception ex) { NoticeDialog.Show(this, "清理未完成：" + ex.Message, "日志清理", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            logs.Controls.Add(cleanup, 0, 3);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = Padding.Empty, WrapContents = false };
            var save = new RoundedButton { Text = "保存设置", Width = 116, Height = 40 };
            var cancel = new RoundedButton { Text = "返回概览", DialogResult = DialogResult.Cancel, Width = 116, Height = 40 };
            save.Click += (sender, args) =>
            {
                if (!ValidateThemeColor()) return;
                if (TopLevel) DialogResult = DialogResult.OK;
                else if (SaveRequested != null) SaveRequested();
            };
            cancel.Click += (sender, args) => { if (!TopLevel && CancelRequested != null) CancelRequested(); };
            AppTheme.ActionButton(save, true);
            AppTheme.ActionButton(cancel, false);
            buttons.Controls.Add(save); buttons.Controls.Add(cancel); layout.Controls.Add(buttons, 0, 4);
            feedback.Text = "更改后点击保存设置";
            feedback.Dock = DockStyle.Fill;
            feedback.ForeColor = AppTheme.Muted;
            feedback.TextAlign = ContentAlignment.MiddleRight;
            layout.Controls.Add(feedback, 0, 5);
            var about = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            about.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            about.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            about.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            about.Controls.Add(new Label { Text = "开发者 xiong1915 · v2.5.3", Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft, ForeColor = AppTheme.Muted, AutoEllipsis = true,
                Font = new Font(Font.FontFamily, 9F) }, 0, 0);
            const string repositoryUrl = "https://github.com/xiong1915/SteamDirect";
            var github = new RoundedButton { Text = "GitHub 项目", Dock = DockStyle.Fill, Margin = Padding.Empty,
                Tag = repositoryUrl, AccessibleDescription = repositoryUrl };
            AppTheme.ActionButton(github, false);
            github.Click += (sender, args) =>
            {
                try { Process.Start(new ProcessStartInfo(repositoryUrl) { UseShellExecute = true }); }
                catch (Exception ex) { NoticeDialog.Show(this, "无法打开浏览器，请访问 " + repositoryUrl + Environment.NewLine + ex.Message, "GitHub 项目"); }
            };
            about.Controls.Add(github, 1, 0);
            layout.Controls.Add(about, 0, 6);
            foreach (var toggle in new[] { night, tray, startup })
                toggle.CheckedChanged += (sender, args) => feedback.Text = "有更改尚未保存";
            days.ValueChanged += (sender, args) => feedback.Text = "有更改尚未保存";
            AcceptButton = save; CancelButton = cancel;
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
            colorCode.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            colorCode.BorderStyle = BorderStyle.FixedSingle;
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
            row.Controls.Add(colorCode, 1, 0);
            row.Controls.Add(choose, 2, 0);
            row.Controls.Add(reset, 3, 0);
            return row;
        }

        private TableLayoutPanel Section(TableLayoutPanel parent, int row, string title, params int[] heights)
        {
            var card = new RoundedPanel { Dock = DockStyle.Fill, BackColor = Color.White,
                Margin = new Padding(0, 0, 0, 8), Padding = new Padding(14, 8, 14, 8) };
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = heights.Length + 1 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in heights) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold) }, 0, 0);
            card.Controls.Add(grid);
            parent.Controls.Add(card, 0, row);
            return grid;
        }
    }
}







