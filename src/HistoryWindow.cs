using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SteamDirect
{
    internal sealed class HistoryWindow : Form
    {
        private const int RecordLimit = 200;
        private const int ComparisonLimit = 20;
        private const int MinimumAttempts = 12;
        private static readonly Color Ink = AppTheme.Ink;
        private static readonly Color Muted = AppTheme.Muted;
        private static readonly Color Accent = AppTheme.Accent;
        private static readonly Color Page = AppTheme.Page;
        private static readonly DateTimeOffset Epoch = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private readonly HistoryStore store;
        private readonly Func<bool> isGaming;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly ListView records = new HistoryRecordList();
        private readonly ListView incidents = new ListView();
        private readonly TextBox gameFilter = new TextBox();
        private readonly ComboBox modeFilter = new ComboBox();
        private readonly ComboBox recordScope = new ComboBox();
        private readonly HistoryNetworkChart networkChart = new HistoryNetworkChart();
        private readonly TextBox comparisonText = new TextBox();
        private readonly Label overviewTitle = new Label();
        private readonly Label overviewNote = new Label();
        private readonly TextBox overviewText = new TextBox();
        private readonly Label status = new Label();
        private readonly Label detailTitle = new Label();
        private readonly Label detailMeta = new Label();
        private readonly Button refresh = new RoundedButton();
        private readonly Button previousPage = new RoundedButton(), nextPage = new RoundedButton();
        private readonly Label pageLabel = new Label();
        private int page;
        private bool hasNext;
        private readonly Button export = new RoundedButton();
        private List<HistorySession> sessions = new List<HistorySession>();
        private CancellationTokenSource selectionWork;
        private bool fillingRecords, exporting, loadingRecords, shuttingDown;

        internal HistoryWindow(HistoryStore store, Func<bool> isGaming = null)
        {
            this.store = store;
            this.isGaming = isGaming;
            SuspendLayout();
            Text = "游戏网络报告";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1100, 720);
            MinimumSize = new Size(980, 650);
            StartPosition = FormStartPosition.CenterParent;
            Font = UiStyle.Body;
            BackColor = Page;
            ForeColor = Ink;

            var root = Grid(1, 4);
            root.Padding = UiStyle.PagePadding;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiStyle.HeaderHeight));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            Controls.Add(root);

            root.Controls.Add(UiStyle.Header("游戏网络报告", "选一局，直接查看网络情况、问题位置和处理建议。"), 0, 0);

            var filterCard = UiStyle.Card(true);
            filterCard.Margin = new Padding(0, 0, 0, UiStyle.Gap);
            var filters = Grid(6, 2);
            filters.RowStyles.Add(new RowStyle(SizeType.Absolute, UiStyle.Space24));
            filters.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int width in new[] { 126, 180, 12, 88, 106 }) filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
            filters.Controls.Add(Caption("游戏名称"), 0, 0);
            gameFilter.Dock = DockStyle.Fill;
            gameFilter.Margin = new Padding(3, 1, 12, 0);
            gameFilter.BorderStyle = BorderStyle.FixedSingle;
            gameFilter.AccessibleName = "按游戏名称筛选";
            filters.Controls.Add(UiStyle.Entry(gameFilter, "按游戏名称筛选"), 0, 1);
            filters.Controls.Add(Caption("网络模式"), 1, 0);
            modeFilter.Dock = DockStyle.Fill;
            modeFilter.Margin = new Padding(3, 1, 0, 0);
            modeFilter.AccessibleName = "按网络模式筛选";
            modeFilter.DropDownStyle = ComboBoxStyle.DropDownList;
            modeFilter.Items.AddRange(new object[] { "全部", "直连", "代理", "未知", "混合" });
            modeFilter.SelectedIndex = 0;
            UiStyle.ConfigureChoice(modeFilter);
            filters.Controls.Add(UiStyle.Entry(modeFilter, "按网络模式筛选"), 1, 1);
            filters.Controls.Add(Caption("记录范围"), 2, 0);
            recordScope.DropDownStyle = ComboBoxStyle.DropDownList;
            recordScope.Items.AddRange(new object[] { "今天的对局", "查看历史对局" });
            recordScope.SelectedIndex = 0;
            UiStyle.ConfigureChoice(recordScope);
            filters.Controls.Add(UiStyle.Entry(recordScope, "查看当天或历史对局记录"), 2, 1);
            recordScope.SelectedIndexChanged += async (sender, args) => { if (IsHandleCreated && !loadingRecords) await RefreshAsync(); };
            StyleButton(refresh, "查询", false);
            StyleButton(export, "导出记录", false);
            refresh.Margin = export.Margin = new Padding(3, 0, 0, 1);
            export.Enabled = false;
            filters.Controls.Add(refresh, 4, 1);
            filters.Controls.Add(export, 5, 1);
            filterCard.Controls.Add(filters);
            root.Controls.Add(filterCard, 0, 1);

            var body = Grid(2, 1);
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.Controls.Add(body, 0, 2);

            var listCard = UiStyle.Card(true);
            listCard.Margin = new Padding(0, 0, UiStyle.Gap, 0);
            var listLayout = Grid(1, 3);
            listLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            listLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            listLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            listLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiStyle.ButtonHeight));
            var listHeader = Grid(2, 1);
            listHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            listHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 122));
            listHeader.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            listHeader.Controls.Add(new Label { Text = "选择一局", Font = UiStyle.SectionTitle,
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            pageLabel.Text = "第 1 页";
            pageLabel.Dock = DockStyle.Fill;
            pageLabel.ForeColor = Muted;
            pageLabel.TextAlign = ContentAlignment.MiddleRight;
            pageLabel.AutoEllipsis = true;
            listHeader.Controls.Add(pageLabel, 1, 0);
            listLayout.Controls.Add(listHeader, 0, 0);
            SetupList(records, "游戏历史记录");
            records.Font = UiStyle.Caption;
            records.MultiSelect = false;
            records.HeaderStyle = ColumnHeaderStyle.None;
            records.Columns.Add("游戏", 142);
            records.Columns.Add("开始时间", 122);
            records.Columns.Add("时长", 68);
            records.Columns.Add("模式", 48);
            records.Columns.Add("", 84);
            SetColumnWeights(records, 100, 0, 0, 0, 0);
            records.FontChanged += (sender, args) => FitRecordRows();
            records.SizeChanged += (sender, args) => FitRecordRows();
            records.ShowItemToolTips = true;
            records.MouseClick += (sender, args) =>
            {
                var session = DetailedRecordAt(args.Location);
                if (session != null) OpenDetailedData(session);
            };
            records.KeyDown += (sender, args) =>
            {
                if (args.KeyCode != Keys.Enter || records.SelectedItems.Count != 1) return;
                args.SuppressKeyPress = true;
                OpenDetailedData(records.SelectedItems[0].Tag as HistorySession);
            };
            var recordMenu = new ContextMenuStrip();
            recordMenu.Items.Add("详细数据", null, (sender, args) =>
            {
                if (records.SelectedItems.Count == 1) OpenDetailedData(records.SelectedItems[0].Tag as HistorySession);
            });
            recordMenu.Opening += (sender, args) => AppTheme.ApplyTray(recordMenu);
            records.ContextMenuStrip = recordMenu;
            records.MouseMove += (sender, args) =>
            {
                records.Cursor = DetailedRecordAt(args.Location) != null ? Cursors.Hand : Cursors.Default;
            };
            listLayout.Controls.Add(records, 0, 1);
            var pages = Grid(2, 1);
            pages.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            pages.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            pages.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            StyleButton(previousPage, "上一页", false);
            StyleButton(nextPage, "下一页", false);
            previousPage.Enabled = nextPage.Enabled = false;
            pages.Controls.Add(previousPage, 0, 0);
            pages.Controls.Add(nextPage, 1, 0);
            listLayout.Controls.Add(pages, 0, 2);
            listCard.Controls.Add(listLayout);
            body.Controls.Add(listCard, 0, 0);

            var detailCard = UiStyle.Card(true);
            detailCard.Margin = Padding.Empty;
            var detail = Grid(1, 3);
            detail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            detail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            detailTitle.Text = "会话详情";
            detailTitle.Font = UiStyle.SectionTitle;
            detailTitle.Dock = DockStyle.Fill;
            detailTitle.Margin = Padding.Empty;
            detailTitle.TextAlign = ContentAlignment.MiddleLeft;
            detailTitle.AutoEllipsis = true;
            detail.Controls.Add(detailTitle, 0, 0);
            detailMeta.Text = "从左侧选择一条游戏记录";
            detailMeta.Dock = DockStyle.Fill;
            detailMeta.Margin = Padding.Empty;
            detailMeta.TextAlign = ContentAlignment.TopLeft;
            detailMeta.ForeColor = Muted;
            detailMeta.AutoEllipsis = true;
            detail.Controls.Add(detailMeta, 0, 1);
            var tabs = new HistorySections();
            SetupText(comparisonText, "模式对比结果");
            SetupList(incidents, "网络异常时间线");
            incidents.Columns.Add("异常", 146);
            incidents.Columns.Add("发生", 116);
            incidents.Columns.Add("恢复", 116);
            incidents.Columns.Add("恢复用时", 94);
            SetColumnWeights(incidents, 31, 25, 25, 19);
            tabs.Add("本局报告", BuildOverview());
            var chartScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = AppTheme.Surface };
            networkChart.Dock = DockStyle.Top;
            networkChart.Height = 240;
            chartScroll.Controls.Add(networkChart);
            chartScroll.SizeChanged += (sender, args) => networkChart.Height = Math.Max(chartScroll.ClientSize.Height, (UiStyle.TextHeight(networkChart) + UiStyle.Scale(networkChart, 8)) * 6 + UiStyle.Scale(networkChart, 80));
            tabs.Add("网络波动", chartScroll);
            tabs.Add("异常时间线", incidents);
            tabs.Add("历史参考", comparisonText);
            detail.Controls.Add(tabs, 0, 2);
            detailCard.Controls.Add(detail);
            body.Controls.Add(detailCard, 1, 0);

            status.Dock = DockStyle.Fill;
            status.Margin = Padding.Empty;
            status.Font = UiStyle.Caption;
            status.ForeColor = Muted;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.AutoEllipsis = true;
            root.Controls.Add(status, 0, 3);
            gameFilter.TextChanged += (sender, args) => { previousPage.Enabled = nextPage.Enabled = false; };
            modeFilter.SelectedIndexChanged += (sender, args) => { previousPage.Enabled = nextPage.Enabled = false; };
            previousPage.Click += async (sender, args) => await RefreshAsync(page - 1);
            nextPage.Click += async (sender, args) => await RefreshAsync(page + 1);
            gameFilter.KeyDown += async (sender, args) =>
            {
                if (args.KeyCode != Keys.Enter) return;
                args.SuppressKeyPress = true;
                await RefreshAsync();
            };
            records.SelectedIndexChanged += async (sender, args) => { if (!fillingRecords) await LoadSelectionAsync(); };
            refresh.Click += async (sender, args) => await RefreshAsync();
            export.Click += async (sender, args) => await ExportAsync();
            Shown += async (sender, args) => await RefreshAsync();
            ParentChanged += (sender, args) => { if (!TopLevel) MinimumSize = Size.Empty; };
            AppTheme.Apply(this);
            ResumeLayout(true);
        }

        private static TableLayoutPanel Grid(int columns, int rows)
        {
            return UiStyle.Grid(columns, rows);
        }

        private Control BuildOverview()
        {
            var layout = Grid(1, 3);
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            overviewTitle.Text = "选择记录后显示本局结果";
            overviewTitle.Dock = DockStyle.Fill;
            overviewTitle.Font = UiStyle.HeroTitle;
            layout.Controls.Add(overviewTitle, 0, 0);
            overviewNote.Dock = DockStyle.Fill;
            overviewNote.ForeColor = Muted;
            overviewNote.AutoEllipsis = true;
            layout.Controls.Add(overviewNote, 0, 1);
            SetupText(overviewText, "本局结果说明与建议");
            overviewText.Font = UiStyle.Body;
            layout.Controls.Add(overviewText, 0, 2);
            return layout;
        }

        private static Label Caption(string text)
        {
            return new Label { Font = UiStyle.Caption, Text = text, Dock = DockStyle.Fill, Margin = Padding.Empty,
                TextAlign = ContentAlignment.MiddleLeft, ForeColor = Muted, AutoEllipsis = true };
        }

        private static void StyleButton(Button button, string text, bool primary)
        {
            button.Font = UiStyle.Button;
            button.Text = text;
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(0, 0, UiStyle.SmallGap, 0);
            AppTheme.ActionButton(button, primary);
        }

        private static void SetupList(ListView view, string label)
        {
            view.AccessibleName = label;
            view.Dock = DockStyle.Fill;
            view.View = View.Details;
            view.FullRowSelect = true;
            view.HideSelection = false;
            view.BackColor = AppTheme.Surface;
            view.ForeColor = Ink;
            view.BorderStyle = BorderStyle.None;
            view.OwnerDraw = true;
            view.DrawColumnHeader += DrawListHeader;
            view.DrawSubItem += DrawListCell;
        }

        private static void SetColumnWeights(ListView view, params int[] weights)
        {
            Action fit = () =>
            {
                if (!view.IsHandleCreated || view.ClientSize.Width <= SystemInformation.VerticalScrollBarWidth) return;
                int remaining = view.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2;
                int space = remaining;
                for (int i = 0; i < weights.Length; i++)
                {
                    int width = i == weights.Length - 1 ? remaining : space * weights[i] / 100;
                    if (view.Columns[i].Width != width) view.Columns[i].Width = width;
                    remaining -= width;
                }
            };
            view.HandleCreated += (sender, args) => fit();
            view.SizeChanged += (sender, args) => fit();
        }

        private static void DrawListHeader(object sender, DrawListViewColumnHeaderEventArgs args)
        {
            using (var brush = new SolidBrush(AppTheme.Background(Page))) args.Graphics.FillRectangle(brush, args.Bounds);
            TextRenderer.DrawText(args.Graphics, args.Header.Text, ((ListView)sender).Font, Rectangle.Inflate(args.Bounds, -6, 0), AppTheme.Text(Muted),
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }

        private static void DrawListCell(object sender, DrawListViewSubItemEventArgs args)
        {
            var view = (ListView)sender;
            var bounds = args.Bounds;
            bounds.Width = view.Columns[args.ColumnIndex].Width;
            if (bounds.Width <= 0) return;
            var background = args.Item.Selected
                ? AppTheme.Background(AppTheme.Pill)
                : args.ItemIndex % 2 == 0 ? AppTheme.Background(AppTheme.Surface) : AppTheme.Background(Page);
            using (var brush = new SolidBrush(background)) args.Graphics.FillRectangle(brush, bounds);
            if (args.Item.Selected && args.ColumnIndex == 0)
            {
                int stripe = Math.Max(2, (int)Math.Round(args.Graphics.DpiX / 48f));
                using (var brush = new SolidBrush(AppTheme.Text(Accent)))
                    args.Graphics.FillRectangle(brush, bounds.X, bounds.Y, stripe, bounds.Height);
            }
            if (args.Item.Focused && view.Focused)
                ControlPaint.DrawFocusRectangle(args.Graphics, Rectangle.Inflate(bounds, -1, -1));
            var color = args.ColumnIndex == 0 ? Ink : Muted;
            if (args.Item.Tag is HistorySession && args.ColumnIndex == 0)
            {
                var area = Rectangle.Inflate(bounds, -8, -6);
                var session = (HistorySession)args.Item.Tag;
                int[] heights = RecordTextHeights(args.Item, area.Width);
                string title = (session.HasAnomalies ? "● 异常 · " : "") + args.Item.Text;
                using (var font = new Font(view.Font, FontStyle.Bold))
                    TextRenderer.DrawText(args.Graphics, title, font,
                        new Rectangle(area.X, area.Y, area.Width, heights[0]),
                        AppTheme.Text(session.HasAnomalies ? AppTheme.Error : Ink),
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(args.Graphics, args.Item.SubItems[1].Text, view.Font,
                    new Rectangle(area.X, area.Y + heights[0], area.Width, heights[1]), AppTheme.Text(Muted), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(args.Graphics, args.Item.SubItems[2].Text + " · " + args.Item.SubItems[3].Text, view.Font,
                    new Rectangle(area.X, area.Y + heights[0] + heights[1], area.Width, heights[2]), AppTheme.Text(Ink), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                var action = RecordActionBounds(args.Item);
                using (var brush = new SolidBrush(AppTheme.Background(AppTheme.Pill)))
                using (var path = UiStyle.RoundRect(action, UiStyle.ControlRadius)) args.Graphics.FillPath(brush, path);
                TextRenderer.DrawText(args.Graphics, "详细数据", view.Font, action, AppTheme.Text(Accent),
                    TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine);
                return;
            }
            TextRenderer.DrawText(args.Graphics, args.SubItem.Text, view.Font, Rectangle.Inflate(bounds, -6, 0),
                AppTheme.Text(color), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }

        private static void SetupText(TextBox text, string label)
        {
            UiStyle.ReadOnlyText(text, label);
        }

        private static Rectangle RecordActionBounds(ListViewItem item)
        {
            var view = item.ListView;
            int height = UiStyle.TextHeight(view) + UiStyle.Scale(view, 12);
            int width = TextRenderer.MeasureText("详细数据", view.Font).Width + 24;
            var bounds = item.Bounds;
            return new Rectangle(bounds.Right - width - 8, bounds.Bottom - height - 6, width, height);
        }

        private static int[] RecordTextHeights(ListViewItem item, int width)
        {
            var font = item.ListView.Font;
            var size = new Size(Math.Max(1, width), 1000);
            var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
            string prefix = ((HistorySession)item.Tag).HasAnomalies ? "● 异常 · " : "";
            int title;
            using (var bold = new Font(font, FontStyle.Bold))
                title = TextRenderer.MeasureText(prefix + item.Text, bold, size, flags).Height + 6;
            return new[] { title, TextRenderer.MeasureText(item.SubItems[1].Text, font, size, flags).Height + 6,
                TextRenderer.MeasureText(item.SubItems[2].Text + " · " + item.SubItems[3].Text, font, size, flags).Height + 6 };
        }

        private void FitRecordRows()
        {
            if (records.ClientSize.Width < 1) return;
            int width = Math.Max(80, records.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 20);
            int height = (UiStyle.TextHeight(records) + UiStyle.Scale(records, 6)) * 4 + UiStyle.Scale(records, 24);
            foreach (ListViewItem row in records.Items)
            {
                int[] blocks = RecordTextHeights(row, width);
                height = Math.Max(height, blocks[0] + blocks[1] + blocks[2] + UiStyle.TextHeight(records) + UiStyle.Scale(records, 28));
            }
            ((HistoryRecordList)records).SetRowHeight(height);
            records.Invalidate();
        }

        private HistorySession DetailedRecordAt(Point point)
        {
            if (!records.ClientRectangle.Contains(point)) return null;
            var item = records.GetItemAt(point.X, point.Y);
            if (item == null || item.SubItems.Count <= 4 || !RecordActionBounds(item).Contains(point)) return null;
            return item.Tag as HistorySession;
        }

        private void OpenDetailedData(HistorySession session)
        {
            if (session == null || Gaming || session.End == 0) { status.Text = "请在游戏结束后查看详细数据。"; return; }
            new HistoryDataWindow(store, session, isGaming).Show(TopLevelControl as Form ?? this);
        }
        private bool Gaming { get { return isGaming != null && isGaming(); } }
        private bool Available { get { return !shuttingDown && !IsDisposed && !Disposing; } }

        private void CheckAnalysis(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Gaming) throw new OperationCanceledException("游戏运行中，已暂停报告计算。");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !shuttingDown)
            {
                shuttingDown = true;
                lifetime.Cancel();
                if (selectionWork != null) selectionWork.Cancel();
                lifetime.Dispose();

                if (records.ContextMenuStrip != null) records.ContextMenuStrip.Dispose();
            }
            base.Dispose(disposing);
        }

        private async Task RefreshAsync(int requestedPage = 0)
        {
            if (!Available || loadingRecords) return;
            if (Gaming) { status.Text = "游戏运行中，请在退出游戏后刷新。"; return; }
            loadingRecords = true;
            refresh.Enabled = gameFilter.Enabled = modeFilter.Enabled = recordScope.Enabled = previousPage.Enabled = nextPage.Enabled = false;
            string name = gameFilter.Text.Trim();
            string mode = modeFilter.SelectedItem as string;
            long from = recordScope.SelectedIndex == 0 ? (long)(new DateTimeOffset(DateTime.Today).ToUniversalTime() - Epoch).TotalMilliseconds : 0;
            long before = recordScope.SelectedIndex == 0 ? (long)(new DateTimeOffset(DateTime.Today.AddDays(1)).ToUniversalTime() - Epoch).TotalMilliseconds : 0;
            status.Text = "正在读取本地记录…";
            try
            {
                var token = lifetime.Token;
                var result = await Task.Run(() =>
                {
                    CheckAnalysis(token);
                    var loaded = store.ListSessions(RecordLimit + 1, requestedPage * RecordLimit, name, mode == "全部" ? null : mode, from, before);
                    CheckAnalysis(token);
                    return loaded;
                }, token);
                if (Available)
                {
                    page = requestedPage;
                    hasNext = result.Count > RecordLimit;
                    if (hasNext) result.RemoveAt(RecordLimit);
                    sessions = result;
                    pageLabel.Text = "第 " + (page + 1) + " 页 · " + sessions.Count + " 条";
                    await ShowSessionsAsync();
                }
            }
            catch (OperationCanceledException) { if (Available) status.Text = "已暂停读取。游戏退出后可刷新。"; }
            catch (Exception ex)
            {
                if (Available)
                {
                    ClearDetails();
                    UiStyle.SetState(overviewTitle, overviewNote, "历史记录暂时无法读取", "点击查询重试，错误详情见下方。", true);
                    status.Text = "无法读取历史记录：" + ex.Message;
                    overviewText.Text = status.Text;
                }
            }
            finally
            {
                loadingRecords = false;
                if (Available)
                {
                    refresh.Enabled = gameFilter.Enabled = modeFilter.Enabled = recordScope.Enabled = true;
                    previousPage.Enabled = page > 0;
                    nextPage.Enabled = hasNext;
                }
            }
        }

        private HistorySession Selected()
        {
            return records.SelectedItems.Count == 0 ? null : records.SelectedItems[0].Tag as HistorySession;
        }

        private async Task ShowSessionsAsync()
        {
            if (!Available) return;
            var previous = Selected();
            ListViewItem preferred = null;
            fillingRecords = true;
            records.BeginUpdate();
            try
            {
                records.Items.Clear();
                foreach (var session in sessions)
                {
                    var row = new ListViewItem(session.Game ?? "未知游戏");
                    row.ToolTipText = SessionTiming(session) + (session.HasAnomalies ? " · 网络异常，选择记录查看原因和建议" : "");
                    row.SubItems.Add(LocalTime(session.Start, "yyyy-MM-dd HH:mm:ss"));
                    row.SubItems.Add((session.End == 0 ? "已玩 " : "游玩 ") + SpanText((session.End == 0 ? (long)(DateTimeOffset.UtcNow - Epoch).TotalMilliseconds : session.End) - session.Start));
                    row.SubItems.Add(session.Mode ?? "未知");
                    row.SubItems.Add("详细数据");
                    row.Tag = session;
                    records.Items.Add(row);
                    if (previous != null && session.Id == previous.Id) preferred = row;
                }
                if (records.Items.Count > 0)
                {
                    var row = preferred ?? records.Items[0];
                    row.Selected = true;
                    row.EnsureVisible();
                }
            }
            finally { records.EndUpdate(); fillingRecords = false; }
            FitRecordRows();
            await LoadSelectionAsync();
        }

        private async Task LoadSelectionAsync()
        {
            if (selectionWork != null) selectionWork.Cancel();
            if (!Available) return;
            var selected = Selected();
            ClearDetails();
            export.Enabled = selected != null && selected.End != 0 && !exporting && !Gaming;
            if (selected == null)
            {
                status.Text = recordScope.SelectedIndex == 0 ? "今天没有匹配的对局。可切换“查看历史对局”读取以前的记录。" : "没有匹配的历史对局记录。";
                UiStyle.SetState(overviewTitle, overviewNote, "暂无可用报告", "完成一局后，点击查询查看网络报告。");
                overviewText.Text = status.Text;
                return;
            }
            detailTitle.Text = selected.Game ?? "未知游戏";
            detailMeta.Text = (selected.Mode ?? "未知") + " · " + (selected.End == 0 ? "已玩 " : "共玩 ") + SpanText((selected.End == 0 ? (long)(DateTimeOffset.UtcNow - Epoch).TotalMilliseconds : selected.End) - selected.Start) + Environment.NewLine + LocalTime(selected.Start, "yyyy-MM-dd HH:mm:ss") + " → " + (selected.End == 0 ? "记录中" : LocalTime(selected.End, "yyyy-MM-dd HH:mm:ss"));
            if (selected.End == 0 || Gaming)
            {
                overviewTitle.Text = "本局尚未生成报告";
                overviewNote.Text = "游戏结束后，点击查询读取结果。";
                status.Text = "游戏运行中，报告计算已暂停。";
                return;
            }

            var work = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            selectionWork = work;
            var token = work.Token;
            status.Text = "正在整理已结束会话的探测记录…";
            UiStyle.SetState(overviewTitle, overviewNote, "正在整理本局结果…", "正在读取本地记录，请稍候。");
            try
            {
                var result = await Task.Run(() =>
                {
                    CheckAnalysis(token);
                    var previous = store.ListComparisonSessions(selected, "直连", ComparisonLimit);
                    CheckAnalysis(token);
                    previous.AddRange(store.ListComparisonSessions(selected, "代理", ComparisonLimit));
                    previous.Sort((a, b) =>
                    {
                        int order = b.Start.CompareTo(a.Start);
                        return order != 0 ? order : string.Compare(b.Id, a.Id, StringComparison.Ordinal);
                    });
                    return BuildDetails(selected, previous, token);
                }, token);
                if (!Available || token.IsCancellationRequested) return;
                if (Gaming) { status.Text = "游戏运行中，报告计算已暂停。"; return; }
                ShowDetails(result);
                status.Text = "报告已生成 · " + (selected.Interrupted ? "本局记录曾中断，详见报告" : "左侧可切换其他对局");
            }
            catch (OperationCanceledException)
            {
                if (Available && !token.IsCancellationRequested) status.Text = "游戏运行中，报告计算已暂停。";
            }
            catch (Exception ex)
            {
                if (Available && !token.IsCancellationRequested)
                {
                    status.Text = "无法生成报告：" + ex.Message;
                    UiStyle.SetState(overviewTitle, overviewNote, "本局报告暂时无法生成", "点击查询重试，错误原因见窗口底部。", true);
                }
            }
            finally
            {
                if (selectionWork == work) selectionWork = null;
                work.Dispose();
            }
        }

        private void ClearDetails()
        {
            detailTitle.Text = "会话详情";
            detailMeta.Text = "从左侧选择一条游戏记录";
            comparisonText.Clear();
            incidents.Items.Clear();
            UiStyle.SetState(overviewTitle, overviewNote, "选择记录后显示本局结果", "左侧列出了自动保存的游戏记录。");
            overviewText.Clear();
            networkChart.SetData(null, null, null);
        }

        private sealed class Detail
        {
            internal string Comparison;
            internal string OverviewTitle, OverviewNote, OverviewText;
            internal HistoryMetrics Metrics;
            internal List<HistoryEvent> Events;
            internal List<HistorySample> Samples;
            internal HistorySession Session;
            internal HistoryMetrics Direct, Proxy;
        }

        private Detail BuildDetails(HistorySession selected, List<HistorySession> recent, CancellationToken token)
        {
            CheckAnalysis(token);
            var samples = store.ReadSamples(selected.Id);
            CheckAnalysis(token);
            var events = store.ReadEvents(selected.Id);
            CheckAnalysis(token);
            var metrics = HistoryStatistics.Calculate(Checked(samples, token));
            var result = new Detail { Events = events, Metrics = metrics, Samples = samples, Session = selected };
            BuildOverviewText(result, selected, samples, token);
            if (!string.IsNullOrWhiteSpace(selected.Target))
            {
                result.Direct = HistoryStatistics.Calculate(ComparableSamples(selected, samples, recent, "直连", token));
                result.Proxy = HistoryStatistics.Calculate(ComparableSamples(selected, samples, recent, "代理", token));
            }
            result.Comparison = FormatComparison(selected.Target, result.Direct, result.Proxy);
            CheckAnalysis(token);
            return result;
        }

        private void BuildOverviewText(Detail detail, HistorySession session, List<HistorySample> samples, CancellationToken token)
        {
            var metrics = detail.Metrics;
            bool adapterDown = false, localTrouble = false, externalTrouble = false, spike = false, proxyTrouble = false;
            bool dnsFailed = false, dnsTested = false;
            int gatewayReplies = 0;
            foreach (var sample in Checked(samples, token))
            {
                dnsTested |= sample.Dns >= 0;
                dnsFailed |= sample.Dns == 0;
                adapterDown |= !sample.AdapterUp;
                if (sample.GatewayMs >= 0) gatewayReplies++;
            }
            foreach (var item in detail.Events)
            {
                CheckAnalysis(token);
                adapterDown |= item.Kind == "网卡不可用";
                localTrouble |= item.Kind == "本地链路探测异常";
                externalTrouble |= item.Kind == "外部探测异常" || item.Kind == "外网异常" || item.Kind == "探测丢包异常";
                spike |= item.Kind == "延迟突增";
                dnsFailed |= item.Kind == "DNS 异常";
                proxyTrouble |= item.Kind == "代理模式下的异常";
            }
            if (metrics.Attempts > 0 && metrics.Lost == metrics.Attempts) externalTrouble = true;
            bool insufficient = metrics.Attempts - metrics.Lost < MinimumAttempts;
            detail.OverviewTitle = adapterDown ? "发现电脑网络连接中断" :
                localTrouble ? "发现本地网络异常线索" :
                dnsFailed ? "发现域名解析异常" :
                externalTrouble ? "发现外部连接异常线索" :
                spike ? "发现网络延迟突增" :
                proxyTrouble ? "代理模式下发现异常线索" :
                detail.Events.Count > 0 ? "记录到尚未定位的异常" :
                insufficient || session.Interrupted ? "记录不足，暂时无法完整判断" :
                metrics.Lost > 0 ? "有零星超时，未发现持续网络异常" : "未发现持续网络异常";
            detail.OverviewNote = "已按本局记录分析，故障位置和处理办法见下方。";
            var location = new StringBuilder();
            var advice = new StringBuilder();
            if (adapterDown)
            {
                location.AppendLine("电脑的网络接口曾不可用，优先检查电脑到路由器之间的连接。");
                advice.AppendLine("检查网线是否松动，或重新连接 Wi-Fi；恢复后再试。");
            }
            if (localTrouble)
            {
                location.AppendLine("本地网关和外部目标曾同时没有回复。本地链路可疑，也可能是目标限制探测。");
                advice.AppendLine("先检查电脑与路由器连接，Wi-Fi 信号弱时靠近路由器或改用网线。");
            }
            if (dnsFailed)
            {
                location.AppendLine("出现域名解析失败，可能影响 Steam 登录和网页访问。当前记录无法确定是 DNS 服务还是上游连接导致。");
                advice.AppendLine("重新连接网络后重试；反复出现时，用网络诊断检查 DNS，再核对系统 DNS 设置。");
            }
            if (externalTrouble)
            {
                location.AppendLine(gatewayReplies > 0 ? "外部探测曾连续异常，本地网关有过回复。路由器之后的链路或探测目标可疑，尚不能确定运营商或游戏服务器故障。" :
                    "外部探测出现异常，本地网关证据不足，暂时无法区分本地链路、外网和目标限制探测。");
                advice.AppendLine("打开“网络诊断”检查 Steam 服务和本地网络；若 Steam 与网页也同时失败，再联系网络服务商排查。");
            }
            if (spike)
            {
                location.AppendLine("记录到外部目标的延迟突然升高，当前证据无法确定拥堵发生在哪一段。");
                advice.AppendLine("暂停下载和云同步后复测；使用 Wi-Fi 时也检查信号强度。");
            }
            if (proxyTrouble)
            {
                location.AppendLine("部分异常发生在代理模式下，代理是否为原因仍需复测确认。");
                advice.AppendLine("退出游戏后切换直连，再用相同目标检测；比较“历史参考”中的两种模式。");
            }
            if (location.Length == 0)
            {
                location.AppendLine(detail.Events.Count > 0 ? "存在尚未分类的异常记录，现有信息不足以确定故障位置。请查看异常时间线并进一步检测。" :
                    insufficient || session.Interrupted ? "有效记录不足或记录曾中断，还不能据此排除网络故障。" :
                    metrics.Lost > 0 ? "只看到零星目标未回复，没有持续故障证据，暂时无法定位原因。" :
                    "已记录的外部探测未显示持续故障，尚无证据指向某个故障环节。");
                advice.AppendLine(insufficient || session.Interrupted ? "保持助手运行，完成一局后再查看报告。" :
                    "如果游戏体验正常，可以继续使用；若仍卡顿，打开“网络诊断”进一步检查 Steam 和游戏连接。");
            }
            var text = new StringBuilder();
            text.AppendLine("问题在哪");
            text.AppendLine(location.ToString().TrimEnd());
            text.AppendLine();
            text.AppendLine("怎么处理");
            text.AppendLine(advice.ToString().TrimEnd());
            if (detail.Events.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("恢复情况");
                text.AppendLine(DescribeRecovery(detail.Events, token) + "。发生和恢复时间见“异常时间线”。");
                foreach (var item in detail.Events)
                    if (item.End <= 0)
                    {
                        text.AppendLine("部分异常在本局结束前仍未确认恢复。");
                        break;
                    }
            }
            text.AppendLine();
            text.AppendLine("判断范围");
            text.Append("报告依据网卡、网关和外部目标的记录，游戏服务器的实际延迟与丢包仍需游戏内信息确认。");
            text.Append("直连或代理模式按电脑配置推断。");
            if (!dnsTested) text.Append("本局没有 DNS 检测结果。");
            if (session.Interrupted) text.Append("助手记录曾中断，部分时段未覆盖。");
            detail.OverviewText = text.ToString().TrimEnd();
        }

        private IEnumerable<HistorySample> Checked(List<HistorySample> samples, CancellationToken token)
        {
            for (int i = 0; i < samples.Count; i++)
            {
                if ((i & 63) == 0) CheckAnalysis(token);
                yield return samples[i];
            }
        }

        private IEnumerable<HistorySample> ModeSamples(List<HistorySample> samples, string mode, CancellationToken token)
        {
            var gap = new HistorySample { ExternalMs = -2 };
            foreach (var sample in Checked(samples, token)) yield return sample.Mode == mode ? sample : gap;
            yield return gap;
        }

        private IEnumerable<HistorySample> ComparableSamples(HistorySession selected, List<HistorySample> current, List<HistorySession> recent, string mode, CancellationToken token)
        {
            int included = 0;
            var used = new HashSet<string>(StringComparer.Ordinal);
            if (current != null && !selected.Interrupted && (selected.Mode == mode || selected.Mode == "混合"))
            {
                foreach (var sample in ModeSamples(current, mode, token)) yield return sample;
                included++;
            }
            foreach (var session in recent)
            {
                if (session.End == 0 || session.End > selected.Start || session.Interrupted || session.Id == selected.Id) continue;
                if (session.Mode != mode && session.Mode != "混合") continue;
                if (!used.Add(session.Id)) continue;
                if (!string.Equals(session.Game, selected.Game, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(session.Target, selected.Target, StringComparison.OrdinalIgnoreCase)) continue;
                CheckAnalysis(token);
                foreach (var sample in ModeSamples(store.ReadSamples(session.Id), mode, token)) yield return sample;
                if (++included >= ComparisonLimit) yield break;
            }
        }

        private string DescribeRecovery(List<HistoryEvent> events, CancellationToken token)
        {
            int recovered = 0;
            long total = 0;
            foreach (var item in events)
            {
                CheckAnalysis(token);
                if (item.End <= 0 || item.End < item.Start) continue;
                recovered++;
                total += item.End - item.Start;
            }
            return recovered == 0 ? "无可计算的恢复用时" : "已观测恢复 " + recovered + " 次，平均 " + SpanText(total / recovered);
        }

        private static string FormatComparison(string target, HistoryMetrics direct, HistoryMetrics proxy)
        {
            if (string.IsNullOrWhiteSpace(target) || direct == null || proxy == null) return "没有相同目标的历史记录，暂时无法比较两种模式。";
            var text = new StringBuilder();
            text.AppendLine("直连和代理，哪种表现更好");
            text.AppendLine();
            if (!Comparable(direct, proxy))
                text.AppendLine("样本不足，暂时无法判断。需要同一游戏、相同目标下两种模式都有足够的已结束记录。");
            else
            {
                bool directBetter = direct.Average < proxy.Average && direct.LossRate <= proxy.LossRate;
                bool proxyBetter = proxy.Average < direct.Average && proxy.LossRate <= direct.LossRate;
                text.AppendLine(directBetter ? "这批记录中，直连模式的探测回复更快，超时也没有更多。" :
                    proxyBetter ? "这批记录中，代理模式的探测回复更快，超时也没有更多。" :
                    "两种模式的结果没有一致优劣，暂时无法推荐某一种。");
                text.AppendLine();
                text.AppendLine("怎么使用这个结论");
                text.AppendLine("如果卡顿只在某一种模式出现，退出游戏后切换另一种模式复测。");
            }
            text.AppendLine();
            text.AppendLine("比较范围");
            text.AppendLine("只参考相同游戏、相同目标，以及本局之前结束的记录。不同时间的网络环境可能不同。");
            text.AppendLine("直连/代理标签按本机设置推断；外部探测无法证明游戏连接经过代理，也不能证明游戏服务器表现相同。");
            return text.ToString();
        }

        private static bool Comparable(HistoryMetrics direct, HistoryMetrics proxy)
        {
            return direct != null && proxy != null && direct.Attempts - direct.Lost >= MinimumAttempts && proxy.Attempts - proxy.Lost >= MinimumAttempts;
        }

        private void ShowDetails(Detail detail)
        {
            UiStyle.SetState(overviewTitle, overviewNote, detail.OverviewTitle, detail.OverviewNote);
            overviewText.Text = detail.OverviewText;
            overviewText.Select(0, 0);
            comparisonText.Text = detail.Comparison;
            comparisonText.Select(0, 0);
            networkChart.SetData(detail.Session, detail.Samples, detail.Events);
            incidents.BeginUpdate();
            try
            {
                incidents.Items.Clear();
                int count = Math.Min(500, detail.Events.Count);
                for (int i = 0; i < count; i++)
                {
                    var item = detail.Events[i];
                    var row = new ListViewItem(FriendlyEvent(item.Kind));
                    row.SubItems.Add(LocalTime(item.Start, "MM-dd HH:mm:ss"));
                    row.SubItems.Add(item.End > 0 ? LocalTime(item.End, "MM-dd HH:mm:ss") : "本局未确认恢复");
                    row.SubItems.Add(item.End > 0 ? SpanText(item.End - item.Start) : "—");
                    incidents.Items.Add(row);
                }
                if (count == 0) incidents.Items.Add(new ListViewItem("没有异常记录"));
                else if (count < detail.Events.Count) incidents.Items.Add(new ListViewItem("其余异常可导出 CSV 查看"));
            }
            finally { incidents.EndUpdate(); }
        }

        private static string FriendlyEvent(string kind)
        {
            switch (kind)
            {
                case "网卡不可用": return "电脑网络接口不可用";
                case "本地链路探测异常": return "本地链路疑似异常";
                case "外部探测异常": return "外部目标连续未回复";
                case "DNS 异常": return "域名解析失败";
                case "代理模式下的异常": return "代理模式下出现异常";
                case "探测丢包异常": return "探测连续超时";
                default: return kind ?? "尚未分类的异常";
            }
        }

        private async Task ExportAsync()
        {
            var session = Selected();
            if (session == null || session.End == 0 || exporting) return;
            if (Gaming) { status.Text = "游戏运行中，请在退出游戏后导出。"; return; }
            string destination;
            using (var dialog = new SaveFileDialog
            {
                Title = "导出游戏网络记录", Filter = "CSV 文件 (*.csv)|*.csv",
                FileName = "SteamDirect-" + LocalTime(session.Start, "yyyyMMdd-HHmmss") + ".csv",
                DefaultExt = "csv", AddExtension = true, OverwritePrompt = true
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                destination = dialog.FileName;
            }
            exporting = true;
            export.Enabled = false;
            status.Text = "正在导出…";
            try
            {
                var token = lifetime.Token;
                await Task.Run(() => WriteCsv(destination, session, token), token);
                if (Available) status.Text = "已导出会话、探测样本和异常时间线。";
            }
            catch (OperationCanceledException) { if (Available) status.Text = "导出已取消。"; }
            catch (Exception ex) { if (Available) status.Text = "导出失败：" + ex.Message; }
            finally
            {
                exporting = false;
                if (Available)
                {
                    var current = Selected();
                    export.Enabled = current != null && current.End != 0 && !Gaming;
                }
            }
        }

        private void WriteCsv(string destination, HistorySession session, CancellationToken token)
        {
            CheckAnalysis(token);
            var samples = store.ReadSamples(session.Id);
            CheckAnalysis(token);
            var events = store.ReadEvents(session.Id);
            CheckAnalysis(token);
            string temporary = Path.Combine(Path.GetDirectoryName(destination), ".steamdirect-" + Guid.NewGuid().ToString("N") + ".tmp");
            bool created = false;
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(true)))
                {
                    created = true;
                    CsvRow(writer, "row_type", "session_id", "game", "session_mode", "target", "start_utc", "end_utc", "interrupted",
                        "sample_utc", "sample_mode", "gateway", "gateway_result", "gateway_rtt_ms", "external_result", "external_rtt_ms",
                        "adapter_up", "dns_result", "incident", "incident_start_utc", "incident_end_utc", "report");
                    CsvRow(writer, "session", session.Id, session.Game, session.Mode, session.Target, UtcTime(session.Start), UtcTime(session.End),
                        session.Interrupted ? "yes" : "no", "", "", "", "", "", "", "", "", "", "", "", "", session.Report);
                    foreach (var sample in Checked(samples, token))
                    {
                        CsvRow(writer, "sample", session.Id, "", "", "", "", "", "", UtcTime(sample.At), sample.Mode, sample.Gateway,
                            ProbeResult(sample.GatewayMs), Number(sample.GatewayMs), ProbeResult(sample.ExternalMs), Number(sample.ExternalMs),
                            sample.AdapterUp ? "up" : "down", sample.Dns < 0 ? "not_tested" : sample.Dns == 0 ? "failed" : "ok", "", "", "", "");
                    }
                    foreach (var item in events)
                    {
                        CheckAnalysis(token);
                        CsvRow(writer, "incident", session.Id, "", "", "", "", "", "", "", "", "", "", "", "", "", "", "",
                            item.Kind, UtcTime(item.Start), UtcTime(item.End), "");
                    }
                }
                CheckAnalysis(token);
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally { if (created && File.Exists(temporary)) File.Delete(temporary); }
        }

        private static string Number(int value) { return value < 0 ? "" : value.ToString(CultureInfo.InvariantCulture); }
        private static string ProbeResult(int value) { return value >= 0 ? "reply" : value == -1 ? "timeout" : "not_tested"; }

        private static void CsvRow(TextWriter writer, params string[] fields)
        {
            for (int i = 0; i < fields.Length; i++)
            {
                if (i > 0) writer.Write(',');
                string field = fields[i] ?? "";
                string clean = field.TrimStart();
                if ((clean.Length > 0 && "=+-@".IndexOf(clean[0]) >= 0) ||
                    (field.Length > 0 && (field[0] == '\t' || field[0] == '\r' || field[0] == '\n')))
                    field = "'" + field;
                writer.Write('"');
                writer.Write(field.Replace("\"", "\"\""));
                writer.Write('"');
            }
            writer.WriteLine();
        }

        private static string LocalTime(long at, string format)
        {
            return at <= 0 ? "—" : Epoch.AddMilliseconds(at).ToLocalTime().ToString(format, CultureInfo.InvariantCulture);
        }

        private static string UtcTime(long at)
        {
            return at <= 0 ? "" : Epoch.AddMilliseconds(at).ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        private static string SessionTiming(HistorySession session)
        {
            long end = session.End == 0 ? (long)(DateTimeOffset.UtcNow - Epoch).TotalMilliseconds : session.End;
            return LocalTime(session.Start, "yyyy-MM-dd HH:mm:ss") + " → " +
                (session.End == 0 ? "记录中" : LocalTime(session.End, "yyyy-MM-dd HH:mm:ss")) +
                " · " + (session.End == 0 ? "已玩 " : "共玩 ") + SpanText(end - session.Start) +
                (session.Interrupted ? "（记录曾中断）" : "");
        }
        private static string SpanText(long milliseconds)
        {
            var elapsed = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
            return elapsed.TotalHours >= 1 ? ((long)elapsed.TotalHours) + " 小时 " + elapsed.Minutes + " 分 " + elapsed.Seconds + " 秒" :
                elapsed.TotalMinutes >= 1 ? elapsed.Minutes + " 分 " + elapsed.Seconds + " 秒" : elapsed.Seconds + " 秒";
        }

    }
}
