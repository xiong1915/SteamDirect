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
        private const int EntryHeight = UiStyle.Space48;
        private readonly Entry[] entries = new Entry[5];
        private readonly Label state = Caption("", UiStyle.Body, AppTheme.Muted);
        private long loadedRevision = -1;
        private bool loading;
        internal int RecordCount { get; private set; }
        internal int NaturalHeight { get { return HeaderHeight + Math.Max(1, RecordCount) * EntryHeight; } }
        internal event Action HeightChanged;

        internal OverviewHistory(Func<string, Task> open)
        {
            ColumnCount = 2;
            RowCount = entries.Length + 2;
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
            RowStyles.Add(new RowStyle(SizeType.Absolute, EntryHeight));
            state.Text = "正在读取游戏记录…";
            Controls.Add(state, 0, 1);
            SetColumnSpan(state, 2);
            for (int i = 0; i < entries.Length; i++)
            {
                RowStyles.Add(new RowStyle(SizeType.Absolute, EntryHeight));
                var entry = entries[i] = new Entry(open);
                Controls.Add(entry, 0, i + 2);
                SetColumnSpan(entry, 2);
            }
        }

        internal async void RequestRefresh(HistoryMonitor monitor, bool recording)
        {
            var store = monitor.Store;
            long revision = store.Revision;
            if (!IsHandleCreated || !Visible || loading) return;
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
                RowStyles[1].Height = RecordCount == 0 ? UiStyle.Scale(this, EntryHeight) : 0;
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

        private sealed class Entry : SectionRule
        {
            private readonly RoundedButton game = Link("", AppTheme.Ink);
            private readonly Label timing = Caption("", UiStyle.Caption, AppTheme.Muted);
            private readonly Label status = Caption("", UiStyle.Caption, AppTheme.Muted);
            private readonly TableLayoutPanel row = UiStyle.Grid(3, 2);
            private bool wide;
            private HistorySession current;

            internal Entry(Func<string, Task> open)
            {
                Dock = DockStyle.Fill;
                Margin = Padding.Empty;
                Padding = new Padding(0, 1, 0, 0);
                BackColor = AppTheme.Page;
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 0));
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
                row.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
                row.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
                status.TextAlign = ContentAlignment.MiddleLeft;
                row.Controls.Add(game, 1, 0);
                row.Controls.Add(timing, 1, 1);
                row.Controls.Add(status, 2, 0);
                row.SetRowSpan(status, 2);
                game.Click += async (sender, args) =>
                {
                    if (current != null) await open(current.Id);
                };
                Controls.Add(row);
                Visible = false;
            }

            protected override void OnSizeChanged(EventArgs args)
            {
                base.OnSizeChanged(args);
                if (row == null) return;
                bool split = Width >= UiStyle.Scale(this, 760);
                if (wide == split) return;
                wide = split;
                row.SuspendLayout();
                try
                {
                    row.ColumnStyles[0].Width = wide ? 42 : 0;
                    row.ColumnStyles[1].Width = wide ? 58 : 100;
                    row.SetCellPosition(game, new TableLayoutPanelCellPosition(wide ? 0 : 1, 0));
                    row.SetRowSpan(game, wide ? 2 : 1);
                    row.SetCellPosition(timing, new TableLayoutPanelCellPosition(1, wide ? 0 : 1));
                    row.SetRowSpan(timing, wide ? 2 : 1);
                }
                finally { row.ResumeLayout(true); }
            }

            internal void Display(HistorySession session)
            {
                current = session;
                Visible = session != null;
                if (session == null) return;
                game.Text = string.IsNullOrWhiteSpace(session.Game) ? "未知游戏" : session.Game;
                game.AccessibleDescription = "查看网络报告";
                string played = session.End > 0 ? HistoryWindow.SpanText(session.End - session.Start) : "游玩中";
                timing.Text = HistoryWindow.LocalTime(session.Start, "yyyy-MM-dd HH:mm") + "  ·  " + played + "  ·  " + (session.Mode ?? "未知");
                status.Text = session.HasAnomalies ? "●  有异常" : session.Interrupted ? "记录中断" :
                    session.End == 0 ? "记录中" : "未记录异常";
                AppTheme.SetForeground(status, session.HasAnomalies ? AppTheme.Error : AppTheme.Muted);
            }
        }
    }
}
