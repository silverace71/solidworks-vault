using SwVault.Core;
using SwVault.Core.Client;

namespace SwVault.Agent;

/// <summary>
/// Projects and their subsystems (each a vault folder) with their responsible engineers. Anyone can
/// add a project or a subsystem and ask to be a subsystem's RE; an admin approves RE requests.
/// Admins, and whoever added one, can rename or remove it.
/// </summary>
internal sealed class SubsystemsForm : Form
{
    private readonly AgentHost _agent;
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false, ShowNodeToolTips = true };
    private readonly Button _addCar = new() { Text = "Add project...", AutoSize = true };
    private readonly Button _addSubsystem = new() { Text = "Add subsystem...", AutoSize = true, Enabled = false };
    private readonly Button _addFolder = new() { Text = "Add folder...", AutoSize = true, Enabled = false };
    private readonly Button _edit = new() { Text = "Rename...", AutoSize = true, Enabled = false };
    private readonly Button _delete = new() { Text = "Remove", AutoSize = true, Enabled = false };
    private readonly Button _claim = new() { Text = "I'm a responsible engineer of this", AutoSize = true, Enabled = false };
    private readonly Button _stepDown = new() { Text = "Step down", AutoSize = true, Enabled = false };
    private readonly Button _remove = new() { Text = "Remove this RE", AutoSize = true, Enabled = false };
    private readonly Button _refresh = new() { Text = "Refresh", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(640, 0) };
    private string _me = "";

    private sealed record EngineerNode(Subsystem Subsystem, SubsystemEngineer Engineer);

    private sealed record SubfolderNode(Subsystem Subsystem, string Name);

    /// <summary>Folders a new subsystem starts with; the prompt lets you change them.</summary>
    private const string StandardFolders = "Custom Parts, COTS Parts, Fasteners";

    public SubsystemsForm(AgentHost agent)
    {
        _agent = agent;
        Text = "SwVault - Projects and subsystems";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 760;
        Height = 560;
        MinimumSize = new Size(560, 400);
        Icon = AppIcon.Create();
        Font = SystemFonts.MessageBoxFont;

        var intro = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(10, 10, 10, 6),
            MaximumSize = new Size(680, 0),
            Text = "A project (a car, a rig, a year's build) has subsystems, and each subsystem is a folder in the vault. A subsystem's responsible engineers (REs) are told when others add, check in or release files there, " +
                   "and are copied on review requests from general members. Being an RE needs an admin's approval.",
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
        buttons.Controls.AddRange(new Control[] { _addCar, _addSubsystem, _addFolder, _edit, _delete, _claim, _stepDown, _remove, _refresh });
        var statusBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(10, 0, 10, 8) };
        statusBar.Controls.Add(_status);
        Controls.Add(_tree);
        Controls.Add(intro);
        Controls.Add(buttons);
        Controls.Add(statusBar);

        _tree.AfterSelect += (_, _) => UpdateButtons();
        _addCar.Click += async (_, _) => await AddCarAsync();
        _addSubsystem.Click += async (_, _) => await AddSubsystemAsync();
        _addFolder.Click += async (_, _) => await AddFolderAsync();
        _edit.Click += async (_, _) => await EditAsync();
        _delete.Click += async (_, _) => await DeleteAsync();
        _claim.Click += async (_, _) => await ClaimAsync();
        _stepDown.Click += async (_, _) => await RemoveAsync(self: true);
        _remove.Click += async (_, _) => await RemoveAsync(self: false);
        _refresh.Click += async (_, _) => await LoadAsync();
        Shown += async (_, _) => await LoadAsync();
    }

    private Car? SelectedCar => _tree.SelectedNode?.Tag switch
    {
        Car car => car,
        Subsystem s => _cars.FirstOrDefault(c => c.Id == s.CarId),
        EngineerNode e => _cars.FirstOrDefault(c => c.Id == e.Subsystem.CarId),
        SubfolderNode f => _cars.FirstOrDefault(c => c.Id == f.Subsystem.CarId),
        _ => null,
    };

    private Subsystem? SelectedSubsystem => _tree.SelectedNode?.Tag switch
    {
        Subsystem s => s,
        EngineerNode e => e.Subsystem,
        SubfolderNode f => f.Subsystem,
        _ => null,
    };

    private IReadOnlyList<Car> _cars = Array.Empty<Car>();

    private async Task LoadAsync(string? selectId = null)
    {
        _status.Text = "Loading...";
        try
        {
            var (cars, me) = await Task.Run(() => _agent.CarsAsync());
            _cars = cars;
            _me = me;
            selectId ??= _tree.SelectedNode?.Tag switch { Car c => c.Id, Subsystem s => s.Id, EngineerNode e => e.Subsystem.Id, SubfolderNode f => f.Subsystem.Id, _ => null };
            _tree.BeginUpdate();
            _tree.Nodes.Clear();
            TreeNode? select = null;
            foreach (var car in cars)
            {
                var carNode = new TreeNode($"{car.Name}   [{car.Folder}]") { Tag = car, NodeFont = new Font(_tree.Font, FontStyle.Bold) };
                if (car.Id == selectId) select = carNode;
                foreach (var s in car.Subsystems)
                {
                    var approved = s.Engineers.Where(e => e.Approved).Select(e => e.Login).ToList();
                    var subNode = new TreeNode($"{s.Name}   [{s.Folder}]   RE: {(approved.Count == 0 ? "none yet" : string.Join(", ", approved))}")
                    {
                        Tag = s,
                        ToolTipText = s.Folder,
                    };
                    if (s.IsEngineer(me)) subNode.ForeColor = Color.FromArgb(0, 102, 204);
                    foreach (var folder in s.Folders)
                        subNode.Nodes.Add(new TreeNode("Folder: " + folder) { Tag = new SubfolderNode(s, folder), ToolTipText = s.Folder + "/" + folder, ForeColor = Color.DimGray });
                    foreach (var e in s.Engineers)
                    {
                        var node = new TreeNode(e.Approved ? e.Login : $"{e.Login}  (waiting for an admin)") { Tag = new EngineerNode(s, e) };
                        if (!e.Approved) node.ForeColor = Color.DarkGoldenrod;
                        subNode.Nodes.Add(node);
                    }
                    carNode.Nodes.Add(subNode);
                    if (s.Id == selectId) select = subNode;
                }
                _tree.Nodes.Add(carNode);
            }
            _tree.ExpandAll();
            _tree.EndUpdate();
            if (select != null) _tree.SelectedNode = select;
            _status.Text = cars.Count == 0 ? "No projects yet. Add one, then add its subsystems." : "";
            UpdateButtons();
        }
        catch (VaultException ex)
        {
            _status.Text = ex.Message;
        }
    }

    private void UpdateButtons()
    {
        var subsystem = SelectedSubsystem;
        _addSubsystem.Enabled = SelectedCar != null;
        _addFolder.Enabled = subsystem != null;
        // The server decides who may (admins, or whoever added it) and says so otherwise.
        _edit.Enabled = _tree.SelectedNode?.Tag is Car or Subsystem;
        _delete.Enabled = _tree.SelectedNode?.Tag is Car or Subsystem or SubfolderNode;
        _claim.Enabled = subsystem != null && !subsystem.Engineers.Any(e => string.Equals(e.Login, _me, StringComparison.OrdinalIgnoreCase));
        var mineEntry = subsystem?.Engineers.FirstOrDefault(e => string.Equals(e.Login, _me, StringComparison.OrdinalIgnoreCase));
        _stepDown.Enabled = mineEntry != null;
        _stepDown.Text = mineEntry is { Approved: false } ? "Withdraw my request" : "Step down";
        _remove.Enabled = _tree.SelectedNode?.Tag is EngineerNode other && _agent.TeamWatcher.IsAdmin
                          && !string.Equals(other.Engineer.Login, _me, StringComparison.OrdinalIgnoreCase);
    }

    private async Task AddCarAsync()
    {
        using var prompt = new NamePrompt("Add a project", "Project name (e.g. 2027 Car):", "Vault folder (leave empty to use the name):");
        if (prompt.ShowDialog(this) != DialogResult.OK) return;
        await RunAsync(() => _agent.AddCarAsync(prompt.NameText, prompt.FolderText), $"Added {prompt.NameText}.");
    }

    private async Task AddSubsystemAsync()
    {
        var car = SelectedCar;
        if (car == null) return;
        using var prompt = new NamePrompt($"Add a subsystem to {car.Name}", "Subsystem name (e.g. Front Suspension):",
            $"Vault folder (leave empty for {car.Folder}/<name>):", extraLabel: "Folders inside it (separate with commas; leave empty for none):", extra: StandardFolders);
        if (prompt.ShowDialog(this) != DialogResult.OK) return;
        var folders = prompt.ExtraText.Split(',').Select(f => f.Trim()).Where(f => f.Length > 0).ToList();
        await RunAsync(() => _agent.AddSubsystemAsync(car.Id, prompt.NameText, prompt.FolderText, folders), $"Added {prompt.NameText} to {car.Name}. Its folder is in your vault folder.");
    }

    private async Task AddFolderAsync()
    {
        var subsystem = SelectedSubsystem;
        if (subsystem == null) return;
        using var prompt = new NamePrompt($"Add a folder to {subsystem.Name}", "Folder name (e.g. Fasteners, or COTS Parts/Bearings):", folderLabel: null);
        if (prompt.ShowDialog(this) != DialogResult.OK) return;
        await RunAsync(() => _agent.AddSubsystemFolderAsync(subsystem.Id, prompt.NameText), $"Added the folder {prompt.NameText} to {subsystem.Name}. Everyone's vault folder gets it.");
    }

    private async Task EditAsync()
    {
        switch (_tree.SelectedNode?.Tag)
        {
            case Car car:
            {
                using var prompt = new NamePrompt($"Rename {car.Name}", "Project name:", "Vault folder:", "Save", car.Name, car.Folder);
                if (prompt.ShowDialog(this) != DialogResult.OK) return;
                await RunAsync(() => _agent.UpdateCarAsync(car.Id, prompt.NameText, prompt.FolderText), $"Saved {prompt.NameText}.");
                break;
            }
            case Subsystem s:
            {
                using var prompt = new NamePrompt($"Rename {s.Name}", "Subsystem name:", "Vault folder (files already in the old folder stay there):", "Save", s.Name, s.Folder);
                if (prompt.ShowDialog(this) != DialogResult.OK) return;
                await RunAsync(() => _agent.UpdateSubsystemAsync(s.Id, prompt.NameText, prompt.FolderText), $"Saved {prompt.NameText}.");
                break;
            }
        }
    }

    private async Task DeleteAsync()
    {
        switch (_tree.SelectedNode?.Tag)
        {
            case Car car:
                if (!Confirm($"Remove the project {car.Name} and its {car.Subsystems.Count} subsystem(s) from this list?\n\nFiles and folders in the vault are not touched.")) return;
                await RunAsync(() => _agent.RemoveCarAsync(car.Id), $"Removed {car.Name}.");
                break;
            case Subsystem s:
                if (!Confirm($"Remove the subsystem {s.Name} from this list? Its responsible engineers are no longer notified.\n\nFiles and folders in the vault are not touched.")) return;
                await RunAsync(() => _agent.RemoveSubsystemAsync(s.Id), $"Removed {s.Name}.");
                break;
            case SubfolderNode f:
                if (!Confirm($"Take the folder {f.Name} off {f.Subsystem.Name}'s list?\n\nThe folder and any files in it stay in the vault; it just isn't created on new PCs any more.")) return;
                await RunAsync(() => _agent.RemoveSubsystemFolderAsync(f.Subsystem.Id, f.Name), $"Removed the folder {f.Name}.");
                break;
        }
    }

    private bool Confirm(string question) =>
        MessageBox.Show(this, question, "SwVault", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;

    private async Task ClaimAsync()
    {
        var subsystem = SelectedSubsystem;
        if (subsystem == null) return;
        _status.Text = "Sending...";
        try
        {
            var approved = await Task.Run(() => _agent.ClaimEngineerAsync(subsystem.Id));
            await LoadAsync(subsystem.Id);
            _status.Text = approved
                ? $"You're a responsible engineer of {subsystem.Name}."
                : $"Asked to be a responsible engineer of {subsystem.Name}. An admin has to approve it; SwVault tells you when they do.";
        }
        catch (VaultException ex)
        {
            _status.Text = ex.Message;
        }
    }

    private async Task RemoveAsync(bool self)
    {
        var subsystem = SelectedSubsystem;
        if (subsystem == null) return;
        var login = self ? _me : (_tree.SelectedNode?.Tag as EngineerNode)?.Engineer.Login;
        if (login == null) return;
        var question = self ? $"Stop being a responsible engineer of {subsystem.Name}?" : $"Remove {login} as a responsible engineer of {subsystem.Name}?";
        if (MessageBox.Show(this, question, "SwVault", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        await RunAsync(() => _agent.RemoveEngineerAsync(subsystem.Id, login), self ? $"You're no longer an RE of {subsystem.Name}." : $"Removed {login}.");
    }

    private async Task RunAsync(Func<Task> action, string done)
    {
        _status.Text = "Saving...";
        try
        {
            await Task.Run(action);
            await LoadAsync();
            _status.Text = done;
        }
        catch (VaultException ex)
        {
            _status.Text = ex.Message;
        }
    }
}

/// <summary>Asks for a name, an optional folder, and optionally one more line.</summary>
internal sealed class NamePrompt : Form
{
    private readonly TextBox _name = new() { Width = 320 };
    private readonly TextBox _folder = new() { Width = 320 };
    private readonly TextBox _extra = new() { Width = 320 };

    public NamePrompt(string title, string nameLabel, string? folderLabel, string okText = "Add", string? name = null, string? folder = null,
        string? extraLabel = null, string? extra = null)
    {
        _name.Text = name ?? "";
        _folder.Text = folder ?? "";
        _extra.Text = extra ?? "";
        Text = "SwVault - " + title;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = SystemFonts.MessageBoxFont;
        var layout = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Padding = new Padding(12) };
        layout.Controls.Add(new Label { Text = nameLabel, AutoSize = true });
        layout.Controls.Add(_name);
        if (folderLabel != null)
        {
            layout.Controls.Add(new Label { Text = folderLabel, AutoSize = true, Margin = new Padding(0, 8, 0, 0) });
            layout.Controls.Add(_folder);
        }
        if (extraLabel != null)
        {
            layout.Controls.Add(new Label { Text = extraLabel, AutoSize = true, Margin = new Padding(0, 8, 0, 0) });
            layout.Controls.Add(_extra);
        }
        var ok = new Button { Text = okText, AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Width = 320, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
        ok.Click += (_, _) =>
        {
            if (_name.Text.Trim().Length > 0) return;
            DialogResult = DialogResult.None;
            _name.Focus();
        };
    }

    public string NameText => _name.Text.Trim();
    public string? FolderText => _folder.Text.Trim().Length == 0 ? null : _folder.Text.Trim();
    public string ExtraText => _extra.Text.Trim();
}
