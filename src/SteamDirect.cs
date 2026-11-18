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
            HighDpi.InitializeUi();
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

    internal sealed class MainWindow : DesktopForm
    {
        private readonly Button runButton = new RoundedButton();
        private readonly Button logButton = new RoundedButton();
        private readonly Button flowToggle, logToggle;
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

        private readonly Icon ownedMainIcon;
        private readonly NotifyIcon trayIcon = new NotifyIcon();
        private readonly ContextMenuStrip trayMenu = new ContextMenuStrip();
        private readonly UserPreferences preferences;
        private bool exitRequested;
        private bool restoring;
        private FormWindowState lastVisibleState = FormWindowState.Normal;
        private readonly WindowDiagnostics windowDiagnostics;
        private HistoryMonitor history;
        private bool savingSettings;
        private readonly Panel pageHost = new PageHost();
        private readonly Dictionary<string, Button> navigation = new Dictionary<string, Button>();
        private PageManager pages;

        internal MainWindow(bool background = false, HistoryMonitor historyOverride = null, UserPreferences preferencesOverride = null)
        {
            preferences = preferencesOverride ?? UserPreferences.Load(UserPreferences.DefaultPath);
            windowDiagnostics = new WindowDiagnostics(this) { Enabled = preferences.WindowDiagnostics };
            history = historyOverride ?? new HistoryMonitor(HistoryMonitor.DefaultPath);
            Motion.Reduced = preferences.ReduceMotion; Motion.IsGaming = () => history.IsPlaying;
            SuspendLayout();
            AppTheme.IsDark = preferences.NightMode;
            AppTheme.TryColor(preferences.ThemeColor, out AppTheme.ThemeColor);
            Text = "Steam 直连助手 v3.0.14";
            ownedMainIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); Icon = ownedMainIcon;
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
            const int overviewHeight = 440;
            var root = UiStyle.Grid(1, 8);
            root.Padding = UiStyle.PagePadding;
            root.BackColor = AppTheme.Page;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 64, 160, 80, 44, 0, 44, 0 })
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var overview = new ContentViewport(root, overviewHeight);
            pageHost.Controls.Add(overview);
            pages = new PageManager(pageHost, overview);
            root.Controls.Add(UiStyle.Header("Steam 直连", ""), 0, 0);

            var action = UiStyle.Card();
            action.ShowArtwork = true;
            action.Margin = new Padding(0, 0, 0, UiStyle.Space16);
            var actionLayout = UiStyle.Grid(1, 3);
            actionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiStyle.Space48));
            actionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiStyle.Space32));
            actionLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            runButton.Text = "严格直连并重启 Steam";
            runButton.Dock = DockStyle.Left;
            runButton.Width = 280;
            runButton.Margin = new Padding(0, 0, 0, UiStyle.Space8);
            AppTheme.ActionButton(runButton, true);
            runButton.Font = UiStyle.SectionTitle;
            runButton.Click += async (sender, args) => await RunAsync();
            actionLayout.Controls.Add(runButton, 0, 0);

            statusLabel.Text = "退出游戏后开始";
            statusLabel.Dock = DockStyle.Fill;
            statusLabel.Margin = Padding.Empty;
            statusLabel.AutoEllipsis = true;
            statusLabel.Font = UiStyle.SectionTitle;
            actionLayout.Controls.Add(statusLabel, 0, 1);
            elapsedLabel.Text = "就绪";
            elapsedLabel.ForeColor = AppTheme.Muted;
            elapsedLabel.Font = UiStyle.Caption;
            elapsedLabel.Dock = DockStyle.Fill;
            elapsedLabel.Margin = Padding.Empty;
            actionLayout.Controls.Add(elapsedLabel, 0, 2);
            action.Controls.Add(actionLayout);
            root.Controls.Add(action, 0, 1);

            cleanupPanel = new CleanupPanel(Log) { Margin = Padding.Empty };
            root.Controls.Add(cleanupPanel, 0, 2);

            var flowHeader = CreateDisclosure("直连流程", out flowToggle);
            var logHeader = CreateDisclosure("操作日志", out logToggle);
            root.Controls.Add(flowHeader, 0, 3);
            root.Controls.Add(logHeader, 0, 5);
            timeline.Dock = DockStyle.Fill;
            timeline.BackColor = AppTheme.Page;
            timeline.Visible = false;
            root.Controls.Add(timeline, 0, 4);

            var logArea = UiStyle.Grid(1, 2);
            logArea.RowStyles.Add(new RowStyle(SizeType.Absolute, UiStyle.Space40));
            logArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            logArea.Visible = false;
            root.Controls.Add(logArea, 0, 6);
            logButton.Text = "打开日志文件";
            logButton.Dock = DockStyle.Left;
            logButton.Width = 132;
            logButton.Margin = Padding.Empty;
            AppTheme.ActionButton(logButton, false);
            ((RoundedButton)logButton).TextOnly = true;
            logArea.Controls.Add(logButton, 0, 0);

            Action<string, Button, Control, int, int> toggleSection = (title, button, section, row, height) =>
            {
                bool expand = !section.Visible;
                root.SuspendLayout();
                try
                {
                    section.Visible = expand;
                    root.RowStyles[row].Height = expand ? UiStyle.Scale(root, height) : 0;
                    button.Text = (expand ? "−  " : "+  ") + title;
                    button.AccessibleDescription = expand ? "已展开" : "已收起";
                    overview.SetMinimumHeight(overviewHeight + (timeline.Visible ? 300 : 0) + (logArea.Visible ? 192 : 0));
                }
                finally { root.ResumeLayout(true); }
                if (expand) overview.RevealTop(section, UiStyle.Scale(root, 125));
            };
            flowToggle.Click += (sender, args) => toggleSection("直连流程", flowToggle, timeline, 4, 300);
            logToggle.Click += (sender, args) => toggleSection("操作日志", logToggle, logArea, 6, 192);

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

            logBox.Dock = DockStyle.Fill;
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.BackColor = AppTheme.Page;
            logBox.ForeColor = AppTheme.Muted;
            logBox.Font = UiStyle.Body;
            logBox.BorderStyle = BorderStyle.None;
            logBox.AccessibleName = "操作记录";
            logArea.Controls.Add(new NativeScrollFrame(logBox) { BackColor = AppTheme.Page }, 0, 1);
            animationTimer.Interval = UiStyle.MotionTick;
            animationTimer.Tick += (sender, args) =>
            {
                if (!Visible || WindowState == FormWindowState.Minimized || !Motion.Allowed) { animationTimer.Stop(); return; }
                timeline.Angle = (timeline.Angle + UiStyle.SpinnerStep) % 360;
                if (timeline.Visible) timeline.Invalidate();
                if (timeline.ActiveStep >= 0 && timeline.ActiveStep < timeline.StepCount)
                    elapsedLabel.Text = "第 " + (timeline.ActiveStep + 1) + " / " + timeline.StepCount + " 步  ·  " + operationWatch.Elapsed.ToString(@"mm\:ss");
            };
            trayMenu.Items.Add("打开主窗口", null, (sender, args) => ShowMainWindow());
            trayMenu.Items.Add("设置", null, (sender, args) => OpenSettings());
            trayMenu.Opening += (sender, args) =>
            {
                AppTheme.ApplyTray(trayMenu);
            };
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("退出 Steam 直连助手", null, (sender, args) => RequestExit());
            trayIcon.Text = "Steam 直连助手";
            trayIcon.Icon = Icon;
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.DoubleClick += (sender, args) => ShowMainWindow();
            trayIcon.Visible = preferences.TrayEnabled && !WindowEnvironment.OffscreenTests;
            FormClosing += (sender, args) =>
            {
                if (savingSettings && args.CloseReason == CloseReason.UserClosing) { args.Cancel = true; exitRequested = false; return; }
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
                    Status("操作正在进行，请等待完成后退出。");
                }
            };
            Shown += (sender, args) => { if (background && preferences.TrayEnabled) Hide(); };
            Log("就绪，请先退出游戏。");
            Shown += (sender, args) => { if (preferences.NetworkRecording) history.Start(); };
            AppTheme.Apply(this);
            ResumeLayout(true);
        }

        private static Control CreateDisclosure(string title, out Button toggle)
        {
            var header = new SectionRule { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(0, 1, 0, 0) };
            toggle = new RoundedButton { Text = "+  " + title, Dock = DockStyle.Fill, Margin = Padding.Empty,
                CaptionInset = 0, TextOnly = true, AccessibleName = title, AccessibleDescription = "已收起" };
            AppTheme.ActionButton(toggle, false);
            header.Controls.Add(toggle);
            return header;
        }

        private void BuildNavigation()
        {
            var shell = UiStyle.Grid(2, 1);
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 168));
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var rail = UiStyle.Grid(1, 7); rail.Padding = new Padding(12, 24, 12, 16);
            rail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
            for (int i = 0; i < 4; i++) rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            rail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            var brand = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            brand.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            brand.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            brand.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var mascot = new PictureBox { Image = Icon.ToBitmap(), Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom, TabStop = false, Margin = new Padding(32, 0, 32, 0) };
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
            rail.Controls.Add(new Label { Text = "v3.0.14", ForeColor = AppTheme.Muted,
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomCenter, Font = UiStyle.Caption }, 0, 6);
            var sidebar = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.Page };
            sidebar.Padding = new Padding(8);
            sidebar.Margin = new Padding(12, 12, 0, 12);
            sidebar.Controls.Add(rail);
            rail.Padding = new Padding(0, 8, 0, 0);
            shell.Controls.Add(sidebar, 0, 0);
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
            if (savingSettings) return;
            if (page != "概览" && running)
            {
                Status("请等待直连流程完成后再切换页面。");
                return;
            }
            if (page == "游戏记录" && (history.IsPlaying || SteamGameDiscovery.IsSteamGameActive()))
            {
                Status("游戏进行中，报告已暂停。");
                return;
            }
            pages.DiagnosticsEnabled = preferences.WindowDiagnostics;
            if (pages.Navigate(page, () => CreatePage(page)))
            {
                SelectNavigation(page);
            }
        }

        private Form CreatePage(string page)
        {
            if (page == "网络诊断") return new NetworkWindow(Log);
            if (page == "游戏记录") return new HistoryWindow(history.Store, () => history.IsPlaying,
                () => !preferences.NetworkRecording ? "自动记录已关闭，可在设置中开启。" :
                    string.IsNullOrEmpty(history.Error) ? "自动记录已开启。" : "自动记录遇到错误，请查看日志或重启应用。");
            var settings = new SettingsWindow(preferences.TrayEnabled, StartupSettings.IsEnabled(),
                preferences.RetentionDays, preferences.NightMode, preferences.ThemeColor, preferences.LogDirectory, preferences.ReduceMotion, preferences.WindowDiagnostics, preferences.NetworkRecording);
            settings.Synchronize = () => settings.Reload(preferences, StartupSettings.IsEnabled());
            settings.SaveRequested += () => SaveSettings(settings);
            settings.CancelRequested += () => NavigateTo("概览");
            return settings;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (pages != null) pages.Dispose();
                history.Dispose();
                windowDiagnostics.Dispose();
                animationTimer.Dispose();
                trayIcon.Dispose();
                trayMenu.Dispose();
                if (ownedMainIcon != null) ownedMainIcon.Dispose();
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
            if (IsDisposed || restoring) return;
            restoring = true;
            try {
                windowDiagnostics.Begin();
                if (!Visible) Show();
                if (WindowState == FormWindowState.Minimized) WindowState = lastVisibleState;
                CheckWorkArea();
                windowDiagnostics.Displayed();
                if (!WindowEnvironment.OffscreenTests) Activate();
            } finally { restoring = false; }
        }

        internal void RequestExit()
        {
            exitRequested = true;
            Close();
        }

        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); UpdateOperationAnimation(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); if (Visible && WindowState != FormWindowState.Minimized) lastVisibleState = WindowState; UpdateOperationAnimation(); }
        private void UpdateOperationAnimation()
        {
            if (animationTimer == null) return;
            if (running && Visible && WindowState != FormWindowState.Minimized && Motion.Allowed) animationTimer.Start(); else animationTimer.Stop();
        }

        private void OpenSettings()
        {
            ShowMainWindow();
            NavigateTo("设置");
        }

        private async void SaveSettings(SettingsWindow dialog)
        {
                if (savingSettings) return;
                savingSettings = true; dialog.SetSaving(true);
                try
                {
                    if (!dialog.ValidateThemeColor()) return;
                    Directory.CreateDirectory(dialog.LogDirectory);
                    if (dialog.AutoStart != StartupSettings.IsEnabled()) StartupSettings.SetEnabled(dialog.AutoStart);
                    UserPreferences.Save(UserPreferences.DefaultPath, dialog.TrayEnabled, dialog.RetentionDays, dialog.NightMode, dialog.ThemeColor, dialog.LogDirectory, dialog.ReduceMotion, dialog.DiagnosticsEnabled, dialog.RecordingEnabled);
                    if (preferences.NetworkRecording != dialog.RecordingEnabled)
                    {
                        var previous = history;
                        await System.Threading.Tasks.Task.Run(() => previous.Dispose());
                        await previous.Completion;
                        if (IsDisposed || Disposing) return;
                        if (dialog.RecordingEnabled) { history = previous.CreateRestart(); history.Start(); }
                        preferences.NetworkRecording = dialog.RecordingEnabled;
                    }
                    preferences.LogDirectory = dialog.LogDirectory; LogStore.DirectoryPath = preferences.LogDirectory;
                    preferences.WindowDiagnostics = dialog.DiagnosticsEnabled; windowDiagnostics.Enabled = preferences.WindowDiagnostics;
                    preferences.ReduceMotion = dialog.ReduceMotion; Motion.Reduced = preferences.ReduceMotion;
                    preferences.NightMode = dialog.NightMode;
                    preferences.ThemeColor = dialog.ThemeColor;
                    AppTheme.SetTheme(preferences.NightMode, preferences.ThemeColor);
                    preferences.RetentionDays = dialog.RetentionDays;
                    lastCleanupDate = DateTime.MinValue;
                    preferences.TrayEnabled = dialog.TrayEnabled;
                    trayIcon.Visible = preferences.TrayEnabled && !WindowEnvironment.OffscreenTests;
                    Log("设置已保存：托盘" + (preferences.TrayEnabled ? "开启" : "关闭") + "，开机自启" + (dialog.AutoStart ? "开启" : "关闭") + "。");
                    dialog.ShowSaved();
                }
                catch (Exception ex) { NoticeDialog.Show(this, "设置未能全部保存：" + ex.Message, "设置", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                finally { savingSettings = false; if (!dialog.IsDisposed) dialog.SetSaving(false); }
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
            if (timeline.Visible) timeline.Invalidate();
            elapsedLabel.Text = "第 " + (index + 1) + " / " + timeline.StepCount + " 步";
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

        private async Task RunAsync()
        {
            if (running) return;
            running = true;
            timeline.ActiveStep = 0;
            timeline.Failed = false;
            operationWatch.Restart();
            UpdateOperationAnimation();
            runButton.Enabled = false;
            cleanupPanel.AllowCleanup(false);
            runButton.Text = "正在执行，请稍候…";
            Log("开始直连流程。");
            try
            {
                await Task.Run(() => RunWorkflow());
                FinishProgress(true);
                Status("完成 · 已提交 Steam 启动请求");
                Log("成功：本机代理检查通过，已提交 Steam 启动请求。");
            }
            catch (Exception ex)
            {
                FinishProgress(false);
                Status("已停止 · 请查看操作记录");
                Log("停止：" + ex.Message);
            }
            finally
            {
                running = false;
                runButton.Enabled = true;
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
                    throw new InvalidOperationException("流程停止：" + failure.Message + "\n自动回滚未完成：" + recovery.Message + "\n备份已保留，请查看日志并重新打开代理软件。", new AggregateException(failure, recovery));
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
        internal bool ShowArtwork;
        internal RoundedPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(UiStyle.ParentSurface(this));
            if (Width < 2 || Height < 2) return;

            using (var outline = UiStyle.RoundRect(new RectangleF(.5F, .5F, Width - 1, Height - 1), CornerRadius * UiStyle.Dpi(this) / 96F))
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var fill = new SolidBrush(BackColor)) e.Graphics.FillPath(fill, outline);
                if (ShowArtwork) Wallpaper.PaintBanner(e.Graphics, ClientRectangle, outline, BackColor);
                using (var border = new Pen(AppTheme.Edge)) e.Graphics.DrawPath(border, outline);
            }
        }
    }

    internal sealed class StepTimeline : Control
    {
        internal int ActiveStep = -1;
        internal bool Failed;
        internal int Angle;
        private readonly string[] titles = { "准备检查", "关闭应用与代理", "核验本机代理", "退出 Steam", "复检并启动", "完成检查" };
        private readonly string[] descriptions = { "路径与游戏状态", "关闭应用和代理", "系统代理与内核", "等待正常退出", "再次核验后启动", "临时文件单独清理" };
        internal int StepCount { get { return titles.Length; } }
        internal StepTimeline() { DoubleBuffered = true; BackColor = AppTheme.Page; TabStop = false; AccessibleName = "直连执行步骤"; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            float scale = DeviceDpi / 96F;
            g.ScaleTransform(scale, scale);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            float gap = Math.Min(76, (Height / scale - 16) / titles.Length);
            var small = UiStyle.DiagramCaption;
            var normal = UiStyle.DiagramTitle;
            using (var muted = new SolidBrush(AppTheme.Text(AppTheme.Muted)))
            {
                for (int i = 0; i < titles.Length; i++)
                {
                    float y = 18 + i * gap;
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
                    g.DrawString(active ? (Failed ? "已停止 · 查看原因" : "正在处理…") : done ? (i == titles.Length - 1 ? "已结束，结果见日志" : "已完成") : descriptions[i], small, muted, 42, y + 7);
                }
            }
        }
    }
}




