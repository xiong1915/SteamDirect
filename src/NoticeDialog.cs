using System.Drawing;
using System.Windows.Forms;

namespace SteamDirect
{
    internal static class NoticeDialog
    {
        internal static DialogResult Show(IWin32Window owner, string message, string title,
            MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None)
        {
            if (buttons != MessageBoxButtons.OK) return MessageBox.Show(owner, message, title, buttons, icon);
            using (var window = new Form())
            {
                window.SuspendLayout();
                window.Text = title;
                window.AccessibleDescription = icon == MessageBoxIcon.Warning ? "需要处理的提示" : title;
                window.AutoScaleDimensions = new SizeF(96, 96);
                window.AutoScaleMode = AutoScaleMode.Dpi;
                window.Font = UiStyle.Body;
                window.BackColor = AppTheme.Page;
                window.ForeColor = AppTheme.Ink;
                window.FormBorderStyle = FormBorderStyle.FixedDialog;
                window.MaximizeBox = window.MinimizeBox = false;
                window.ShowInTaskbar = false;
                window.StartPosition = FormStartPosition.CenterParent;
                window.ClientSize = new Size(480, 310);
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = UiStyle.PagePadding,
                    ColumnCount = 1, RowCount = 3 };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiStyle.Space32 + UiStyle.Space24));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
                layout.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill,
                    Font = UiStyle.HeroTitle, AutoEllipsis = true,
                    ForeColor = icon == MessageBoxIcon.Error ? AppTheme.Error :
                        icon == MessageBoxIcon.Warning ? AppTheme.Warning : AppTheme.Ink }, 0, 0);
                var card = UiStyle.Card(true);
                card.Margin = new Padding(0, 0, 0, UiStyle.Gap);
                var body = new TextBox { Text = message, TabStop = false };
                UiStyle.ReadOnlyText(body, title + "提示内容");
                card.Controls.Add(body);
                layout.Controls.Add(card, 0, 1);
                var close = new RoundedButton { Text = "知道了", DialogResult = DialogResult.OK,
                    Dock = DockStyle.Right, Width = 120, Margin = new Padding(0, UiStyle.Space4, 0, UiStyle.Space4) };
                AppTheme.ActionButton(close, true);
                layout.Controls.Add(close, 0, 2);
                window.AcceptButton = window.CancelButton = close;
                window.Controls.Add(layout);
                AppTheme.Apply(window);
                window.ResumeLayout(true);
                var control = owner as Control;
                return window.ShowDialog(control == null ? owner : control.TopLevelControl);
            }
        }
    }
}
