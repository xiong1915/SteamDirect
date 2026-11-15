using System;
using System.Drawing;
using System.Windows.Forms;
namespace SteamDirect
{
    internal sealed class ReportFilters : Panel
    {
        private readonly Control[] inputs;
        private readonly Label[] labels;
        private readonly Button query, export;
        internal event Action<int> HeightRequired;
        internal ReportFilters(Control search, Control mode, Control scope, Button query, Button export)
        {
            Dock = DockStyle.Fill; Margin = Padding.Empty;
            inputs = new[] { UiStyle.Entry(search, "按游戏名称筛选"), UiStyle.Entry(mode, "按网络模式筛选"), UiStyle.Entry(scope, "查看当天或历史对局记录") };
            labels = new[] { Label("游戏名称"), Label("网络模式"), Label("记录范围") };
            this.query = query; this.export = export;
            for (int i = 0; i < 3; i++) { Controls.Add(labels[i]); Controls.Add(inputs[i]); }
            Controls.Add(query); Controls.Add(export);
        }
        private static Label Label(string text) { return new Label { Text = text, Font = UiStyle.Caption, ForeColor = AppTheme.Muted, Margin = Padding.Empty }; }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e); if (inputs == null) return;
            int gap = UiStyle.Scale(this, 12), label = Math.Max(UiStyle.Scale(this, 24), UiStyle.TextHeight(labels[0]) + UiStyle.Scale(this, 4));
            int height = Math.Max(UiStyle.Scale(this, 40), UiStyle.TextHeight(query) + UiStyle.Scale(this, 16));
            bool compact = Width < UiStyle.Scale(this, 790);
            int mode = UiStyle.Scale(this, 116), scope = UiStyle.Scale(this, 160), q = UiStyle.Scale(this, 88), exp = UiStyle.Scale(this, 108);
            int search = Math.Max(1, Width - mode - scope - gap * (compact ? 2 : 4) - (compact ? 0 : q + exp));
            int x = 0;
            int[] widths = { search, mode, scope };
            for (int i = 0; i < inputs.Length; i++) { labels[i].Bounds = new Rectangle(x, 0, widths[i], label); inputs[i].Bounds = new Rectangle(x, label, widths[i], height); x += widths[i] + gap; }
            query.Bounds = new Rectangle(compact ? Math.Max(0, Width - q - exp - gap) : x, compact ? label + height + gap : label, q, height);
            export.Bounds = new Rectangle(Width - exp, query.Top, exp, height);
            if (HeightRequired != null) HeightRequired(label + height + (compact ? height + gap : 0));
        }
    }
}
