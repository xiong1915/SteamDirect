using System;
using System.Diagnostics;
using System.Drawing;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SteamDirect
{
    internal sealed class OverviewStatus : TableLayoutPanel
    {
        private readonly Label[] values = new Label[3];
        private bool reading;

        internal OverviewStatus()
        {
            ColumnCount = values.Length;
            RowCount = 1;
            Dock = DockStyle.Fill;
            Margin = Padding.Empty;
            RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            string[] titles = { "Windows 代理", "Steam 客户端", "网络接口" };
            for (int i = 0; i < values.Length; i++)
            {
                ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / values.Length));
                var panel = UiStyle.Card(true);
                panel.Padding = new Padding(UiStyle.Space12, UiStyle.Space4, UiStyle.Space12, UiStyle.Space4);
                panel.Margin = new Padding(0, 0, i == values.Length - 1 ? 0 : UiStyle.Space8, UiStyle.Space8);
                var metric = UiStyle.Grid(1, 2);
                metric.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                metric.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
                metric.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
                metric.Controls.Add(Caption(titles[i], UiStyle.Caption, AppTheme.Muted), 0, 0);
                values[i] = Caption("未检查", UiStyle.Subtitle, AppTheme.Ink);
                values[i].AccessibleName = titles[i] + "状态";
                metric.Controls.Add(values[i], 0, 1);
                panel.Controls.Add(metric);
                Controls.Add(panel, i, 0);
            }
        }

        internal async void RequestRefresh()
        {
            if (!Visible || !IsHandleCreated || reading || WindowEnvironment.OffscreenTests) return;
            reading = true;
            var snapshot = await Task.Run(() => new[]
            {
                Observe(ProxySettings.ObserveWindowsProxy),
                Observe(() =>
                {
                    var steam = Process.GetProcessesByName("steam");
                    try { return steam.Length == 0 ? "未运行" : "运行中"; }
                    finally { foreach (var process in steam) process.Dispose(); }
                }),
                Observe(() => NetworkInterface.GetIsNetworkAvailable() ? "可用" : "无可用接口")
            }).ConfigureAwait(false);
            try
            {
                BeginInvoke((Action)(() =>
                {
                    reading = false;
                    if (!IsDisposed && !Disposing)
                        for (int i = 0; i < values.Length; i++) values[i].Text = snapshot[i];
                }));
            }
            catch (InvalidOperationException) { /* The window closed before delivery. */ }
        }

        private static string Observe(Func<string> read)
        {
            try { return read(); }
            catch (Exception) { return "未知"; }
        }

        private static Label Caption(string text, Font font, Color color)
        {
            return new Label { Text = text, Font = font, ForeColor = color, Dock = DockStyle.Fill,
                Margin = Padding.Empty, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        }
    }
}
