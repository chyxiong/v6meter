using V6Meter.Core.Campus;

namespace V6Meter.App.Ui;

/// <summary>Asks for the campus account (student number) and password used by auto-login.</summary>
internal sealed class CampusAccountDialog : Form
{
    private readonly TextBox _account = new() { Dock = DockStyle.Fill };
    private readonly TextBox _password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly CheckBox _auto = new() { Text = "开机、连上网络和掉线后自动登录", AutoSize = true, Margin = new Padding(0, 8, 0, 0) };

    public CampusAccountDialog(CampusAccount? current, bool autoLogin)
    {
        _auto.Checked = autoLogin;
        Text = "自动登录校园网";
        Font = new Font(Theme.UiFamily, 9f);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        _account.Text = current?.Account ?? "";
        _password.Text = current?.Password ?? "";

        var ok = new Button { Text = "保存", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        AcceptButton = ok;
        CancelButton = cancel;
        ok.Click += (s, e) =>
        {
            if (_account.Text.Trim().Length == 0 || _password.Text.Length == 0)
            {
                MessageBox.Show(this, "请填写账号和密码。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.None;
            }
        };

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        var hint = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(360, 0),
            Margin = new Padding(0, 0, 0, 12),
            Text = "填写校园网的登录账号（学号）和密码，用于主窗口的【登录校园网】按钮和自动登录。\n\n" +
                   "密码由 Windows 凭据管理器加密保存，只会发给学校的登录网关（172.18.3.3、172.18.2.2）。",
        };

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        grid.Controls.Add(hint, 0, 0);
        grid.SetColumnSpan(hint, 2);
        grid.Controls.Add(new Label { Text = "账号（学号）", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) }, 0, 1);
        grid.Controls.Add(_account, 1, 1);
        grid.Controls.Add(new Label { Text = "密码", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) }, 0, 2);
        grid.Controls.Add(_password, 1, 2);
        grid.Controls.Add(_auto, 0, 3);
        grid.SetColumnSpan(_auto, 2);
        grid.Controls.Add(buttons, 0, 4);
        grid.SetColumnSpan(buttons, 2);
        Controls.Add(grid);
    }

    public CampusAccount Result => new(_account.Text.Trim(), _password.Text);

    public bool AutoLogin => _auto.Checked;
}
