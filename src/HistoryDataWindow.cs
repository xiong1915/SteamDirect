using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SteamDirect
{
    internal sealed class HistorySections : UserControl
    {
        private readonly FlowLayoutPanel navigation = new FlowLayoutPanel { Dock = DockStyle.Top, Height = UiStyle.ButtonHeight + UiStyle.SmallGap, WrapContents = true, Margin = Padding.Empty };
        private readonly Panel content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, UiStyle.SmallGap, 0, 0) };
        private readonly List<Control> pages = new List<Control>();
        private readonly List<Button> buttons = new List<Button>();
        internal int SelectedIndex { get; private set; }
        internal int PageCount { get { return pages.Count; } }
        internal HistorySections()
        {
            Dock = DockStyle.Fill;
            BackColor = AppTheme.Surface;
            Controls.Add(content);
            Controls.Add(navigation);
        }
        internal void Add(string title, Control page)
        {
            int index = pages.Count;
            var button = new RoundedButton { HorizontalIndicator = true, Text = title, AutoSize = false, Width = 116, Height = UiStyle.ButtonHeight, MinimumSize = new Size(116, UiStyle.ButtonHeight), Margin = new Padding(0, 0, 8, 0) };
            AppTheme.ActionButton(button, false);
            button.Font = UiStyle.Button;
            button.Click += (sender, args) => SelectPage(index);
            navigation.Controls.Add(button);
            buttons.Add(button);
            page.Dock = DockStyle.Fill;
            content.Controls.Add(page);
            pages.Add(page);
            button.FontChanged += (sender, args) => FitNavigation();
            FitNavigation();
            SelectPage(0);
        }
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            FitNavigation();
        }
        private void FitNavigation()
        {
            if (buttons == null || buttons.Count == 0 || Width <= 0) return;
            int required = UiStyle.Scale(this, 116), height = UiStyle.Scale(this, UiStyle.ButtonHeight);
            int margin = 0;
            foreach (var button in buttons) {
                required = Math.Max(required, TextRenderer.MeasureText(button.Text, button.Font).Width + UiStyle.Scale(this, 32));
                height = Math.Max(height, UiStyle.TextHeight(button) + UiStyle.Scale(this, 16));
                margin = Math.Max(margin, button.Margin.Horizontal);
            }
            int columns = Math.Max(1, Math.Min(buttons.Count, Width / (required + margin)));
            int width = Math.Max(1, Width / columns - margin);
            foreach (var button in buttons) { button.MinimumSize = Size.Empty; button.Size = new Size(width, height); }
            int total = ((buttons.Count + columns - 1) / columns) * (height + UiStyle.Scale(this, 8));
            if (navigation.Height != total) navigation.Height = total;
        }

        internal void SelectPage(int index)
        {
            if (index < 0 || index >= pages.Count) throw new ArgumentOutOfRangeException("index");
            SelectedIndex = index;
            for (int i = 0; i < pages.Count; i++)
            {
                pages[i].Visible = i == index;
                ((RoundedButton)buttons[i]).SelectedNavigation = i == index;
                AppTheme.SetBackground(buttons[i], i == index ? AppTheme.Pill : AppTheme.Surface);
                AppTheme.SetForeground(buttons[i], i == index ? AppTheme.Accent : AppTheme.Muted);
            }
        }
    }

    internal sealed class HistoryDataWindow : Form
    {
        private const int PageSize = 200;
        private readonly HistoryStore store;
        private readonly HistorySession session;
        private readonly Func<bool> isGaming;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly TextBox summary = new TextBox();
        private readonly Label state = new Label();
        private readonly DataGridView samples = new DataGridView(), events = new DataGridView();
        private readonly Button previous = new RoundedButton(), next = new RoundedButton();
        private readonly List<HistoryEvent> eventRows = new List<HistoryEvent>();
        private int page;
        private bool busy, closed, hasNext;
        internal HistoryDataWindow(HistoryStore store, HistorySession session, Func<bool> isGaming)
        {
            SuspendLayout();
            this.store = store; this.session = session; this.isGaming = isGaming;
            Text = session.Game + " · 详细数据";
            var ownedIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Icon = ownedIcon;
            Disposed += (sender, args) => { if (ownedIcon != null) ownedIcon.Dispose(); };
            Font = UiStyle.Body;
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(940, 660); MinimumSize = new Size(760, 540);
            StartPosition = FormStartPosition.CenterParent; BackColor = AppTheme.Page; ForeColor = AppTheme.Ink;
            var root = UiStyle.Grid(1, 4);
            root.Padding = UiStyle.PagePadding;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new[] { UiStyle.HeaderHeight, 132 }) root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiStyle.ButtonHeight));
            Controls.Add(root);
            root.Controls.Add(UiStyle.Header(session.Game + " · 详细数据", Time(session.Start) + " — " + Time(session.End) + "   ·   " + session.Mode), 0, 0);
            var card = UiStyle.Card(true);
            UiStyle.ReadOnlyText(summary, "本局网络统计");
            summary.Text = "正在读取本局统计…";
            card.Controls.Add(summary); root.Controls.Add(card, 0, 1);
            var sectionCard = UiStyle.Card(true);
            sectionCard.Margin = new Padding(0, UiStyle.Gap, 0, UiStyle.SmallGap);
            var sections = new HistorySections();
            Configure(samples, new[] { "采样时间", "模式", "网关延迟", "外部延迟", "网卡", "DNS", "网关地址" });
            Configure(events, new[] { "异常类型", "发生时间", "恢复时间", "持续时间" });
            sections.Add("原始采样", samples); sections.Add("异常记录", events); sectionCard.Controls.Add(sections); root.Controls.Add(sectionCard, 0, 2);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            state.Dock = DockStyle.Fill; state.TextAlign = ContentAlignment.MiddleLeft; state.ForeColor = AppTheme.Muted; state.AutoEllipsis = true;
            footer.Controls.Add(state, 0, 0);
            previous.Text = "上一页"; next.Text = "下一页";
            foreach (var button in new[] { previous, next }) { AppTheme.ActionButton(button, false); button.Font = UiStyle.Button; button.Dock = DockStyle.Fill; button.Enabled = false; }
            footer.Controls.Add(previous, 1, 0); footer.Controls.Add(next, 2, 0); root.Controls.Add(footer, 0, 3);
            previous.Click += async (sender, args) => await LoadPageAsync(page - 1);
            next.Click += async (sender, args) => await LoadPageAsync(page + 1);
            Shown += async (sender, args) => await InitializeAsync();
            AppTheme.Apply(this);
            ResumeLayout(true);
        }
        private void Guard(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (isGaming != null && isGaming()) throw new OperationCanceledException("游戏运行中，已暂停数据读取。");
        }
        private static void Configure(DataGridView view, string[] columns)
        {
            view.Font = UiStyle.Caption;
            view.Dock = DockStyle.Fill; view.ReadOnly = true; view.AllowUserToAddRows = false;
            view.AllowUserToDeleteRows = false; view.AllowUserToResizeRows = false;
            view.RowHeadersVisible = false; view.MultiSelect = false;
            view.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            view.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            view.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCellsExceptHeaders;
            view.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            view.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            view.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            view.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 10, 8, 10);
            view.DefaultCellStyle.Padding = new Padding(8, 8, 8, 8);
            view.BorderStyle = BorderStyle.None; view.BackgroundColor = AppTheme.Surface;
            view.EnableHeadersVisualStyles = false;
            view.ColumnHeadersDefaultCellStyle.BackColor = AppTheme.Background(AppTheme.Page);
            view.ColumnHeadersDefaultCellStyle.ForeColor = AppTheme.Text(AppTheme.Muted);
            view.DefaultCellStyle.BackColor = AppTheme.Background(AppTheme.Surface);
            view.DefaultCellStyle.ForeColor = AppTheme.Text(AppTheme.Ink);
            view.DefaultCellStyle.SelectionBackColor = AppTheme.Background(AppTheme.Pill);
            view.DefaultCellStyle.SelectionForeColor = AppTheme.Text(AppTheme.Ink);
            foreach (string column in columns)
                view.Columns.Add(new DataGridViewTextBoxColumn {
                    HeaderText = column, FillWeight = column.Contains("时间") ? 2.6F : 1F,
                    MinimumWidth = column.Contains("时间") ? 180 : 66,
                    SortMode = DataGridViewColumnSortMode.NotSortable
                });
            view.AccessibleName = columns.Length == 7 ? "本局原始采样，每页最多200条" : "本局异常记录，每页最多200条";
        }

        private async Task InitializeAsync()
        {
            try
            {
                var token = lifetime.Token;
                var result = await Task.Run(() =>
                {
                    Guard(token);
                    var all = store.ReadSamples(session.Id);
                    Guard(token);
                    var metrics = HistoryStatistics.Calculate(Checked(all, token));
                    var gateway = HistoryStatistics.Calculate(Gateway(all, token));
                    var found = store.ReadEvents(session.Id);
                    Guard(token);
                    return Tuple.Create(metrics, gateway, found);
                }, token);
                if (closed) return; Guard(token);
                summary.Text = "外部探测：" + result.Item1.Describe() + Environment.NewLine + "本地网关：" +
                    result.Item2.Describe().Replace("没有外部 ICMP 样本", "没有网关探测样本") +
                    Environment.NewLine + "目标：" + session.Target + "；探测丢包不代表游戏实际丢包。";
                eventRows.AddRange(result.Item3);
                await LoadPageAsync(0);
            }
            catch (OperationCanceledException) { if (!closed) { summary.Text = "已暂停，请退出游戏后重新打开。"; state.Text = "未进行游戏内报告计算"; } }
            catch (Exception ex) { if (!closed) { summary.Text = "无法读取本局数据"; state.Text = ex.Message; } }
        }
        private IEnumerable<HistorySample> Checked(List<HistorySample> rows, CancellationToken token)
        {
            for (int i = 0; i < rows.Count; i++) { if (i % 64 == 0) Guard(token); yield return rows[i]; }
        }
        private IEnumerable<HistorySample> Gateway(List<HistorySample> rows, CancellationToken token)
        {
            foreach (var row in Checked(rows, token)) yield return new HistorySample { At = row.At, Mode = row.Mode, ExternalMs = row.GatewayMs };
        }
        private async Task LoadPageAsync(int requested)
        {
            if (busy || closed || requested < 0) return;
            busy = true; previous.Enabled = next.Enabled = false;
            try
            {
                var token = lifetime.Token;
                var rows = await Task.Run(() => { Guard(token); var loaded = store.ReadSamplePage(session.Id, requested * PageSize, PageSize + 1); Guard(token); return loaded; }, token);
                if (closed) return; Guard(token);
                samples.SuspendLayout(); events.SuspendLayout();
                try
                {
                    samples.Rows.Clear(); events.Rows.Clear();
                    for (int i = 0; i < Math.Min(PageSize, rows.Count); i++)
                    {
                        var row = rows[i];
                        samples.Rows.Add(new object[] { Time(row.At), row.Mode ?? "未知", Delay(row.GatewayMs), Delay(row.ExternalMs), row.AdapterUp ? "已连接" : "未连接", row.Dns == 1 ? "正常" : row.Dns == 0 ? "失败" : "未检测", row.Gateway ?? "—" });
                    }
                    int first = requested * PageSize;
                    for (int i = first; i < Math.Min(first + PageSize, eventRows.Count); i++)
                    {
                        var row = eventRows[i];
                        events.Rows.Add(new object[] { row.Kind, Time(row.Start), row.End == 0 ? "未观测到恢复" : Time(row.End), row.End == 0 ? "—" : TimeSpan.FromMilliseconds(Math.Max(0, row.End - row.Start)).ToString() });
                    }
                }
                finally { samples.ResumeLayout(); events.ResumeLayout(); }
                page = requested; hasNext = rows.Count > PageSize || eventRows.Count > (page + 1) * PageSize;
                state.Text = "第 " + (page + 1) + " 页 · 每页最多 200 条 · 本地只读数据";
            }
            catch (OperationCanceledException) { if (!closed) state.Text = "游戏运行中，已暂停读取，请退出游戏后重新打开。"; }
            catch (Exception ex) { if (!closed) state.Text = "读取失败：" + ex.Message; }
            finally { busy = false; if (!closed) { previous.Enabled = page > 0; next.Enabled = hasNext; } }
        }
        private static string Delay(int value) { return value == -1 ? "超时" : value < 0 ? "未检测" : value + " ms"; }
        private static string Time(long value) { return value <= 0 ? "—" : value > 253402300799999L ? "时间无效" : new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(value).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !closed) { closed = true; lifetime.Cancel(); lifetime.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
