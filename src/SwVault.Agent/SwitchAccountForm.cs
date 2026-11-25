using SwVault.Core;

namespace SwVault.Agent;

/// <summary>
/// Signs this PC's SwVault in to the vault as a different account (tray icon > Switch account...),
/// e.g. on a shared lab PC, or an admin moving between their admin and everyday accounts.
/// </summary>
internal sealed class SwitchAccountForm : Form
{
    private readonly AgentHost _agent;
    private readonly TextBox _user = new() { Width = 260 };
    private readonly TextBox _password = new() { Width = 260, UseSystemPasswordChar = true };
    private readonly Button _switch = new() { Text = "Switch", AutoSize = true, MinimumSize = new Size(88, 28) };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, MinimumSize = new Size(88, 28), DialogResult = DialogResult.Cancel };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(360, 0) };

    public SwitchAccountForm(AgentHost agent, string? currentUser, int checkedOutHere)
    {
        _agent = agent;
        Text = "SwVault - Switch account";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Icon = AppIcon.Create();
        Font = SystemFonts.MessageBoxFont;

        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(14) };
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(360, 0),
            Margin = new Padding(0, 0, 0, 10),
            Text = (string.IsNullOrEmpty(currentUser) ? "" : $"This PC is signed in as {currentUser}. ")
                   + "Sign in as another vault account: check-outs, check-ins and reviews from this PC are then that account's. Your files stay where they are.",
        });
        if (checkedOutHere > 0)
        {
            layout.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(360, 0),
                Margin = new Padding(0, 0, 0, 10),
                ForeColor = Color.FromArgb(160, 90, 0),
                Text = $"{currentUser} has {checkedOutHere} file{(checkedOutHere == 1 ? "" : "s")} checked out on this PC. The other account can't check "
                       + $"{(checkedOutHere == 1 ? "it" : "them")} in: check in (or undo) first, or switch back later to finish.",
            });
        }
        layout.Controls.Add(new Label { Text = "User name", AutoSize = true });
        layout.Controls.Add(_user);
        layout.Controls.Add(new Label { Text = "Password", AutoSize = true, Margin = new Padding(3, 8, 3, 0) });
        layout.Controls.Add(_password);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Width = 360, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_switch);
        layout.Controls.Add(buttons);
        layout.Controls.Add(_status);
        Controls.Add(layout);
        AcceptButton = _switch;
        CancelButton = _cancel;

        _switch.Click += async (_, _) => await SwitchAsync();
    }

    /// <summary>The account this PC is signed in as after a successful switch.</summary>
    public string? SwitchedTo { get; private set; }

    private async Task SwitchAsync()
    {
        var user = _user.Text.Trim();
        if (user.Length == 0 || _password.Text.Length == 0)
        {
            _status.Text = "Enter the user name and password of the account to switch to.";
            return;
        }
        _switch.Enabled = _cancel.Enabled = _user.Enabled = _password.Enabled = false;
        _status.ForeColor = Color.DimGray;
        _status.Text = "Signing in...";
        try
        {
            var password = _password.Text;
            SwitchedTo = await Task.Run(() => _agent.SwitchAccountAsync(user, password));
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (VaultException ex)
        {
            _status.ForeColor = Color.Firebrick;
            _status.Text = ex.Code == SwVault.Protocol.ErrorCodes.Unauthorized ? "Wrong user name or password. This PC is still signed in as before." : ex.Message;
            _switch.Enabled = _cancel.Enabled = _user.Enabled = _password.Enabled = true;
            _password.SelectAll();
            _password.Focus();
        }
    }
}
