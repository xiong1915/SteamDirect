using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SteamDirect
{
    internal sealed class OverviewHistory : TableLayoutPanel
    {
        private const int HeaderHeight = UiStyle.Space48;
        private const int EntryHeight = UiStyle.Space32 + UiStyle.Space24;
        private readonly Entry[] entries = new Entry[5];
        private readonly Label state = Caption("", UiStyle.Body, AppTheme.Muted);
        private long loadedRevision = -1;
        private bool loading;
        internal int RecordCount { get; private set; }
        internal int NaturalHeight { get { return HeaderHeight + UiStyle.Space32 + Math.Max(1, RecordCount) * EntryHeight; } }
        internal event Action HeightChanged;

        internal OverviewHistory(Func<string, Task> open)
        {
            ColumnCount = 2;
            RowCount = entries.Length + 3;
            Dock = DockStyle.Fill;
            Margin = new Padding(0, UiStyle.Space12, 0, 0);
            BackColor = AppTheme.Page;
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            RowStyles.Add(new RowStyle(SizeType.Absolute, HeaderHeight));
            Controls.Add(Caption("最近游戏记录", UiStyle.SectionTitle, AppTheme.Ink), 0, 0);
            var all = Link("全部记录 →", AppTheme.Accent);
            all.Click += async (sender, args) => await open(null);
            Controls.Add(all, 1, 0);
            RowStyles.Add(new RowStyle(SizeType.Absolute, UiStyle.Space32));
            var columns = UiStyle.Grid(5, 1);
            SetColumnWidths(columns);
            string[] labels = { "游戏名称", "开始时间", "游戏时长", "网络模式", "记录状态" };
            for (int i = 0; i < labels.Length; i++)
                columns.Controls.Add(Caption(labels[i], UiStyle.Caption, AppTheme.Muted), i, 0);
            Controls.Add(columns, 0, 1);
            SetColumnSpan(columns, 2);
            RowStyles.Add(new RowStyle(SizeType.Absolute, EntryHeight));
            state.Text = "正在读取游戏记录…";
            Controls.Add(state, 0, 2);
            SetColumnSpan(state, 2);
            for (int i = 0; i < entries.Length; i++)
            {
                RowStyles.Add(new RowStyle(SizeType.Absolute, EntryHeight));
                var entry = entries[i] = new Entry(open);
                Controls.Add(entry, 0, i + 3);
                SetColumnSpan(entry, 2);
            }
        }

        internal async void RequestRefresh(HistoryMonitor monitor, bool recording, bool allowHidden = false)
        {
            var store = monitor.Store;
            long revision = store.Revision;
            if (loading || (!allowHidden && (!IsHandleCreated || !Visible))) return;
            if (RecordCount == 0 && (monitor.IsPlaying || loadedRevision == revision))
                state.Text = monitor.IsPlaying ? "退出游戏后可查看记录。" : EmptyMessage(recording);
            if (loadedRevision == revision || monitor.IsPlaying) return;
            loading = true;
            List<HistorySession> sessions = null;
            bool failed = false;
            try
            {
                sessions = await Task.Run(() => SteamGameDiscovery.IsSteamGameActive() ? null :
                    store.Exists ? store.ListSessions(entries.Length) : new List<HistorySession>()).ConfigureAwait(false);
            }
            catch (Exception) { failed = true; }
            try
            {
                BeginInvoke((Action)(() =>
                {
                    loading = false;
                    if (IsDisposed || Disposing) return;
                    if (sessions == null || monitor.IsPlaying)
                    {
                        if (RecordCount == 0) state.Text = failed ? "暂时无法读取记录，可在「全部记录」中重试。" : "退出游戏后可查看记录。";
                        return;
                    }
                    Display(sessions, recording);
                    loadedRevision = revision;
                }));
            }
            catch (InvalidOperationException) { /* The window closed before delivery. */ }
        }

        private void Display(List<HistorySession> sessions, bool recording)
        {
            SuspendLayout();
            try
            {
                int previousCount = RecordCount;
                RecordCount = sessions.Count;
                state.Text = EmptyMessage(recording);
                state.Visible = RecordCount == 0;
                RowStyles[2].Height = RecordCount == 0 ? UiStyle.Scale(this, EntryHeight) : 0;
                for (int i = 0; i < entries.Length; i++)
                    entries[i].Display(i < RecordCount ? sessions[i] : null);
                if (previousCount != RecordCount && HeightChanged != null) HeightChanged();
            }
            finally { ResumeLayout(true); }
        }

        private static string EmptyMessage(bool recording)
        {
            return recording ? "暂无游戏记录。退出游戏后可查看。" : "暂无游戏记录。可在设置中开启自动记录。";
        }

        private static Label Caption(string text, Font font, Color color)
        {
            return new Label { Text = text, Font = font, ForeColor = color, Dock = DockStyle.Fill,
                Margin = Padding.Empty, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        }

        private static RoundedButton Link(string text, Color color)
        {
            var button = new RoundedButton { Text = text, TextOnly = true, CaptionInset = 0, Dock = DockStyle.Fill,
                Margin = Padding.Empty, Font = UiStyle.BodyStrong };
            AppTheme.ActionButton(button, false);
            button.ForeColor = color;
            return button;
        }

        private static void SetColumnWidths(TableLayoutPanel grid)
        {
            foreach (int width in new[] { 34, 23, 16, 10, 17 })
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, width));
        }

        private sealed class Entry : SectionRule
        {
            private readonly RoundedButton game = Link("", AppTheme.Ink);
            private readonly Label timing = Caption("", UiStyle.Caption, AppTheme.Muted);
            private readonly Label duration = Caption("", UiStyle.Caption, AppTheme.Muted);
            private readonly Label mode = Caption("", UiStyle.Caption, AppTheme.Ink);
            private readonly Label status = Caption("", UiStyle.Caption, AppTheme.Muted);
            private HistorySession current;

            internal Entry(Func<string, Task> open)
            {
                Dock = DockStyle.Fill;
                Margin = Padding.Empty;
                Padding = new Padding(0, 1, 0, 0);
                BackColor = AppTheme.Page;
                var row = UiStyle.Grid(5, 1);
                SetColumnWidths(row);
                row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                row.Controls.Add(game, 0, 0);
                row.Controls.Add(timing, 1, 0);
                row.Controls.Add(duration, 2, 0);
                var tag = UiStyle.Card(true);
                tag.CornerRadius = UiStyle.SmallRadius;
                tag.BackColor = AppTheme.Pill;
                tag.Padding = Padding.Empty;
                tag.Margin = new Padding(0, UiStyle.Space16, UiStyle.Space8, UiStyle.Space16);
                mode.TextAlign = ContentAlignment.MiddleCenter;
                tag.Controls.Add(mode);
                row.Controls.Add(tag, 3, 0);
                row.Controls.Add(status, 4, 0);
                EventHandler openRecord = async (sender, args) =>
                {
                    if (current != null) await open(current.Id);
                };
                foreach (var target in new Control[] { game, timing, duration, mode, status })
                {
                    target.Cursor = Cursors.Hand;
                    target.Click += openRecord;
                }
                Controls.Add(row);
                Visible = false;
            }

            internal void Display(HistorySession session)
            {
                current = session;
                Visible = session != null;
                if (session == null) return;
                game.Text = string.IsNullOrWhiteSpace(session.Game) ? "未知游戏" : session.Game;
                game.AccessibleDescription = "查看网络报告";
                duration.Text = session.End > 0 ? HistoryWindow.SpanText(session.End - session.Start) : "游玩中";
                timing.Text = HistoryWindow.LocalTime(session.Start, "yyyy-MM-dd HH:mm");
                mode.Text = session.Mode ?? "未知";
                status.Text = session.HasAnomalies ? "●  有异常" : session.Interrupted ? "记录中断" :
                    session.End == 0 ? "记录中" : "未记录异常";
                AppTheme.SetForeground(status, session.HasAnomalies ? AppTheme.Error : AppTheme.Muted);
            }
        }
    }
}
