using System;
using System.Diagnostics;
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
            UiIcon[] icons = { UiIcon.Settings, UiIcon.Home, UiIcon.Network };
            for (int i = 0; i < values.Length; i++)
            {
                ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / values.Length));
                var panel = UiStyle.Card(true);
                panel.Padding = new Padding(UiStyle.Space12, UiStyle.Space4, UiStyle.Space12, UiStyle.Space4);
                panel.Margin = new Padding(0, 0, i == values.Length - 1 ? 0 : UiStyle.Space8, UiStyle.Space8);
                Label note;
                var metric = UiStyle.Metric(titles[i], icons[i], out values[i], out note, includeNote: false);
                values[i].Text = "未检查";
                panel.Controls.Add(metric);
                Controls.Add(panel, i, 0);
            }
        }

        internal async void RequestRefresh(bool allowHidden = false)
        {
            if (reading || WindowEnvironment.OffscreenTests || (!allowHidden && (!Visible || !IsHandleCreated))) return;
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
                        for (int i = 0; i < values.Length; i++)
                        {
                            string text = snapshot[i];
                            values[i].Text = text;
                            AppTheme.SetForeground(values[i], text == "代理已配置" || text == "已启用自动检测" || text == "无可用接口"
                                ? AppTheme.Warning : AppTheme.Ink);
                        }
                }));
            }
            catch (InvalidOperationException) { /* The window closed before delivery. */ }
        }

        private static string Observe(Func<string> read)
        {
            try { return read(); }
            catch (Exception) { return "未知"; }
        }

    }
}
