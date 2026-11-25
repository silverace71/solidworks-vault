using SwVault.Core.Client;

namespace SwVault.Agent;

/// <summary>
/// Shows what an update is doing: downloading, unpacking, waiting for SOLIDWORKS to close,
/// installing. The installer then stops this agent (and this window) and shows its own status
/// until the new version is running. Closing the window doesn't stop the update.
/// </summary>
internal sealed class UpdateProgressForm : Form
{
    private readonly UpdateWatcher _updates;
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(400, 0), Margin = new Padding(0, 0, 0, 10) };
    private readonly ProgressBar _bar = new() { Width = 400, Height = 18, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30 };
    private readonly Label _hint = new() { AutoSize = true, MaximumSize = new Size(400, 0), ForeColor = Color.DimGray, Margin = new Padding(0, 10, 0, 0) };
    private readonly Button _hide = new() { Text = "Hide", AutoSize = true, MinimumSize = new Size(88, 28) };

    public UpdateProgressForm(UpdateWatcher updates, InstallerRelease release)
    {
        _updates = updates;
        Text = $"SwVault - updating to {release.Version}";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Icon = AppIcon.Create();
        Font = SystemFonts.MessageBoxFont;

        var layout = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16) };
        layout.Controls.Add(_status);
        layout.Controls.Add(_bar);
        layout.Controls.Add(_hint);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Width = 400, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.Add(_hide);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        _hide.Click += (_, _) => Close();
        _updates.Changed += OnChanged;
        FormClosed += (_, _) => _updates.Changed -= OnChanged;
        Refresh(_updates.Status ?? "Starting...");
    }

    private void OnChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() => Refresh(_updates.Status));
        }
        catch (InvalidOperationException)
        {
            // Closing.
        }
    }

    private void Refresh(string? status)
    {
        if (status == null)
        {
            // The attempt ended without handing over to the installer.
            _bar.Style = ProgressBarStyle.Continuous;
            _bar.Value = 0;
            _status.Text = _updates.LastError != null ? "The update didn't install: " + _updates.LastError : "The update stopped.";
            _hint.Text = "Nothing was changed. Try again from the tray icon > Install update.";
            _hide.Text = "Close";
            return;
        }
        _status.Text = status;
        if (_updates.Fraction is { } fraction)
        {
            _bar.Style = ProgressBarStyle.Continuous;
            _bar.Value = Math.Clamp((int)(fraction * 100), 0, 100);
        }
        else
        {
            _bar.Style = ProgressBarStyle.Marquee;
        }
        _hint.Text = status.StartsWith("Update ready", StringComparison.Ordinal)
            ? "Save your work and close SOLIDWORKS. The update installs as soon as it's closed."
            : status.StartsWith("Installing", StringComparison.Ordinal)
                ? "Approve the Windows administrator prompt when it appears."
                : "Keep SOLIDWORKS closed until the update is done. You can hide this window; the update carries on.";
    }
}
