using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using SwVault.AddIn.Infrastructure;
using SwVault.Protocol;

namespace SwVault.AddIn.Ui
{
    /// <summary>
    /// The SwVault task pane: pick a vault and browse it as one tree, folders with their files inside,
    /// each file showing its state and who has it checked out. Right-click a file or a folder for
    /// the commands. With a filter or a search the tree becomes a flat list of the matching files.
    /// </summary>
    internal sealed class VaultPane : UserControl
    {
        private readonly AgentConnection _agent;
        private readonly VaultCommands _commands;
        private readonly ComboBox _vaults = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly ComboBox _filter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly TextBox _search = new TextBox { Dock = DockStyle.Fill };
        private readonly Button _refresh = new Button { Text = "Refresh", AutoSize = true };
        private readonly Button _connect = new Button { Text = "Connect vault...", AutoSize = true };
        private readonly Button _projects = new Button { Text = "Projects...", AutoSize = true };
        private readonly Button _browse = new Button { Text = "Browse...", AutoSize = true };
        private readonly Button _addFiles = new Button { Text = "Add Files...", AutoSize = true };
        private readonly Label _connection = new Label { Dock = DockStyle.Top, Height = 20, ForeColor = Color.DimGray, AutoEllipsis = true };
        private readonly TreeView _folders = new TreeView { Dock = DockStyle.Fill, HideSelection = false, ShowNodeToolTips = true, FullRowSelect = true, ShowLines = false };
        private readonly Label _active = new Label { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(4), AutoEllipsis = true, BorderStyle = BorderStyle.FixedSingle };
        private readonly Button _add = new Button { Text = "Add to Vault", Dock = DockStyle.Bottom, Height = 32, Visible = false, BackColor = Color.FromArgb(0, 150, 136), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        private readonly Timer _debounce = new Timer { Interval = 600 };
        private readonly ImageList _images = Icons.StateImages();
        private VaultInfo[] _vaultList = new VaultInfo[0];
        private string _activePath;
        /// <summary>Files picked with Ctrl or Shift (the tree itself selects one node at a time).</summary>
        private readonly List<TreeNode> _picked = new List<TreeNode>();
        /// <summary>True while a filter or search shows a flat list instead of the folder tree.</summary>
        private bool _flat;
        private HashSet<string> _expandedBeforeFlat;
        private string _selectedBeforeFlat;

        private static readonly string[] Filters = { "Browse folders", "My check-outs", "Outdated", "In review", "Checked out (anyone)", "New and modified" };

        public VaultPane(AgentConnection agent, VaultCommands commands)
        {
            _agent = agent;
            _commands = commands;
            Font = SystemFonts.MessageBoxFont;
            _folders.ImageList = _images;
            _filter.Items.AddRange(Filters);
            _filter.SelectedIndex = 0;

            var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(2) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.Controls.Add(_vaults, 0, 0);
            top.Controls.Add(_refresh, 1, 0);
            top.Controls.Add(_filter, 0, 1);
            top.Controls.Add(_connect, 1, 1);
            top.Controls.Add(_search, 0, 2);
            top.Controls.Add(_browse, 1, 2);
            new ToolTip().SetToolTip(_browse, "Open the file browser: fuzzy search with thumbnails and full file details");
            top.Controls.Add(_projects, 1, 3);
            top.Controls.Add(_addFiles, 0, 3);
            _addFiles.Anchor = AnchorStyles.Left;
            new ToolTip().SetToolTip(_addFiles, "Add files from this PC to the vault: STEP, IGES, DXF, vendor models, PDFs...");
            _search.GotFocus += (s, e) => { if (_search.ForeColor == Color.Gray) { _search.Text = ""; _search.ForeColor = SystemColors.WindowText; } };
            SetSearchPlaceholder();

            Controls.Add(_folders);
            Controls.Add(_add);
            Controls.Add(_active);
            Controls.Add(_connection);
            Controls.Add(top);
            _folders.BringToFront();

            _folders.ContextMenuStrip = BuildMenu();
            _folders.NodeMouseDoubleClick += (s, e) => { if (e.Node.Tag is FileStatusDto file) Run("Open", () => _commands.OpenAsync(file.LocalPath)); };
            _folders.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter || SelectedPaths.Count == 0) return;
                e.SuppressKeyPress = true;
                Run("Open", () => _commands.OpenAsync(SelectedPaths.First()), true);
            };
            _folders.BeforeExpand += (s, e) => Safe(() => ExpandAsync(e.Node));
            _folders.BeforeSelect += OnBeforeSelect;
            // A right-click doesn't select by itself; the menu should be about what was clicked.
            _folders.NodeMouseClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Right || _picked.Contains(e.Node)) return;
                ClearPicked();
                _folders.SelectedNode = e.Node;
            };
            _vaults.SelectedIndexChanged += (s, e) => Safe(LoadRootAsync);
            _filter.SelectedIndexChanged += (s, e) => Safe(ShowModeAsync);
            _search.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Safe(ShowModeAsync); } };
            _refresh.Click += (s, e) => Safe(() => RefreshAllAsync(sync: true));
            _connect.Click += (s, e) => Run("Connect vault", () => _commands.SetupVaultAsync());
            _projects.Click += (s, e) => Run("Projects", () => _agent.ShowAgentWindowAsync("subsystems"));
            _add.Click += (s, e) => AddToVaultRequested?.Invoke();
            _browse.Click += (s, e) => BrowseRequested?.Invoke(SearchText);
            _addFiles.Click += (s, e) => Run("Add files", () => _commands.AddFilesAsync());
            _debounce.Tick += (s, e) => { _debounce.Stop(); Safe(async () => { await RefreshFoldersAsync(); await ShowActiveDocAsync(_activePath); }); };
            _commands.DefaultFolder = () =>
            {
                var vault = CurrentVault;
                var folder = SelectedFolder;
                return vault == null || string.IsNullOrEmpty(vault.LocalRoot) ? null
                    : string.IsNullOrEmpty(folder) ? vault.LocalRoot
                    : Path.Combine(vault.LocalRoot, folder.Replace('/', '\\'));
            };

            _agent.StatusChanged += n => { if (n.VaultId == null || n.VaultId == CurrentVault?.Id) { _debounce.Stop(); _debounce.Start(); } };
            _agent.ConnectionChanged += () => UpdateConnectionLabel();
            _commands.Changed += _ => { _debounce.Stop(); _debounce.Start(); };
        }

        /// <summary>"Front Suspension (2027 Car) - RE: bob, carol" (subsystem, its project, its REs), or null when the file isn't in a subsystem.</summary>
        private async Task<string> SubsystemTextAsync(string path)
        {
            try
            {
                var s = await _agent.SubsystemForAsync(path);
                if (s == null) return null;
                var engineers = s.Engineers != null && s.Engineers.Length > 0 ? string.Join(", ", s.Engineers) : "none yet";
                return s.Name + " (" + s.Car + ") - RE: " + engineers;
            }
            catch (AgentException)
            {
                return null; // older agent or no team service: just leave it out
            }
        }

        /// <summary>The Browse button was clicked; carries the pane's search text (may be empty).</summary>
        public event Action<string> BrowseRequested;

        /// <summary>A command the add-in owns was picked from the pane's menu: "import", "reviews", "invite".</summary>
        public event Action<string> CommandRequested;

        /// <summary>The Add to Vault button was clicked; the add-in adds the active document.</summary>
        public event Action AddToVaultRequested;

        private VaultInfo CurrentVault => _vaults.SelectedIndex >= 0 && _vaults.SelectedIndex < _vaultList.Length ? _vaultList[_vaults.SelectedIndex] : null;

        /// <summary>The files the commands apply to: the ones picked with Ctrl or Shift, else the selected file.</summary>
        private List<string> SelectedPaths =>
            _picked.Count > 0 ? _picked.Select(n => ((FileStatusDto)n.Tag).LocalPath).ToList()
            : _folders.SelectedNode?.Tag is FileStatusDto file ? new List<string> { file.LocalPath }
            : new List<string>();

        /// <summary>The vault path of the selected folder, or of the folder the selected file is in ("" = the top).</summary>
        private string SelectedFolder
        {
            get
            {
                var node = _folders.SelectedNode;
                if (node?.Tag is string folder) return folder;
                if (node?.Tag is FileStatusDto file) return PathFolder(file.Path);
                return "";
            }
        }

        private static string PathFolder(string vaultPath)
        {
            var slash = (vaultPath ?? "").LastIndexOf('/');
            return slash < 0 ? "" : vaultPath.Substring(0, slash);
        }

        // ------------------------------------------------------------ picking several files

        private void OnBeforeSelect(object sender, TreeViewCancelEventArgs e)
        {
            if (e.Action != TreeViewAction.ByMouse && e.Action != TreeViewAction.ByKeyboard) return;
            var ctrl = (ModifierKeys & Keys.Control) != 0;
            var shift = (ModifierKeys & Keys.Shift) != 0;
            var file = e.Node?.Tag is FileStatusDto;
            if (!file || !(ctrl || shift))
            {
                ClearPicked();
                return;
            }
            // Ctrl adds or removes one file; Shift takes everything from the selected file to this one.
            e.Cancel = true;
            var anchor = _folders.SelectedNode;
            if (anchor?.Tag is FileStatusDto && !_picked.Contains(anchor)) Pick(anchor);
            if (shift && anchor != null && anchor.Parent == e.Node.Parent && anchor.Tag is FileStatusDto)
            {
                var siblings = (e.Node.Parent?.Nodes ?? _folders.Nodes).Cast<TreeNode>().ToList();
                int a = siblings.IndexOf(anchor), b = siblings.IndexOf(e.Node);
                foreach (var node in siblings.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1))
                    if (node.Tag is FileStatusDto && !_picked.Contains(node)) Pick(node);
            }
            else if (_picked.Contains(e.Node)) Unpick(e.Node);
            else Pick(e.Node);
        }

        private void Pick(TreeNode node)
        {
            _picked.Add(node);
            node.BackColor = SystemColors.Highlight;
            node.ForeColor = SystemColors.HighlightText;
        }

        private void Unpick(TreeNode node)
        {
            _picked.Remove(node);
            node.BackColor = Color.Empty;
            node.ForeColor = Color.Empty;
        }

        private void ClearPicked()
        {
            foreach (var node in _picked.ToList()) Unpick(node);
        }

        private void SetSearchPlaceholder()
        {
            _search.ForeColor = Color.Gray;
            _search.Text = "Search file names and properties (Enter)";
        }

        private string SearchText => _search.ForeColor == Color.Gray ? "" : _search.Text.Trim();

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Opening += (s, e) =>
            {
                menu.Items.Clear();
                if (CurrentVault == null)
                {
                    e.Cancel = true;
                    return;
                }
                // Right-clicking the empty area below the tree is about the top of the vault.
                var onNode = _folders.GetNodeAt(_folders.PointToClient(Cursor.Position)) != null;
                if (onNode && SelectedPaths.Count > 0) FileMenu(menu);
                else FolderMenu(menu, onNode && !_flat ? SelectedFolder : "");
                e.Cancel = false;
            };
            return menu;
        }

        private void FileMenu(ContextMenuStrip menu)
        {
            menu.Items.Add("Open", null, (s, e) => Run("Open", () => _commands.OpenAsync(SelectedPaths.First()), true));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Get Latest", null, (s, e) => Run("Get latest", () => _commands.GetLatestAsync(SelectedPaths, withReferences: true), true));
            menu.Items.Add("Check Out", null, (s, e) => Run("Check out", () => _commands.CheckOutAsync(SelectedPaths), true));
            menu.Items.Add("Check In...", null, (s, e) => Run("Check in", () => _commands.CheckInAsync(SelectedPaths), true));
            menu.Items.Add("Undo Check Out", null, (s, e) => Run("Undo check-out", () => _commands.UndoCheckOutAsync(SelectedPaths), true));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("History...", null, (s, e) => Run("History", () => _commands.HistoryAsync(SelectedPaths.First()), true));
            menu.Items.Add("Where Used...", null, (s, e) => Run("Where used", () => _commands.WhereUsedAsync(SelectedPaths.First()), true));
            menu.Items.Add("Change State...", null, (s, e) => Run("Change state", () => _commands.ChangeStateAsync(SelectedPaths.First()), true));
            menu.Items.Add("Request Review...", null, (s, e) => Run("Request review", () => _agent.ShowAgentWindowAsync("requestReview", SelectedPaths.First()), true));
            menu.Items.Add("Move...", null, (s, e) => Run("Move", () => _commands.MoveAsync(SelectedPaths), true));
            menu.Items.Add("Update Preview", null, (s, e) => Run("Update preview", () => _commands.SavePreviewsAsync(SelectedPaths), true));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Show in Explorer", null, (s, e) =>
            {
                var path = SelectedPaths.FirstOrDefault();
                if (path != null) Process.Start("explorer.exe", File.Exists(path) ? "/select,\"" + path + "\"" : "\"" + Path.GetDirectoryName(path) + "\"");
            });
        }

        private void FolderMenu(ContextMenuStrip menu, string folder)
        {
            var vault = CurrentVault;
            var local = folder.Length == 0 ? vault.LocalRoot : Path.Combine(vault.LocalRoot, folder.Replace('/', '\\'));
            var title = menu.Items.Add(folder.Length == 0 ? vault.Name : folder.Replace("/", " > "));
            title.Enabled = false;
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("New Subfolder...", null, (s, e) => Run("New subfolder", () => NewSubfolderAsync(vault, folder)));
            menu.Items.Add("Add Files Here...", null, (s, e) => Run("Add files", () => _commands.AddFilesAsync()));
            menu.Items.Add("Show in Explorer", null, (s, e) =>
            {
                Directory.CreateDirectory(local);
                Process.Start("explorer.exe", "\"" + local + "\"");
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Projects and Subsystems...", null, (s, e) => Run("Projects", () => _agent.ShowAgentWindowAsync("subsystems")));
            menu.Items.Add("Import Folder...", null, (s, e) => CommandRequested?.Invoke("import"));
            menu.Items.Add("Reviews...", null, (s, e) => CommandRequested?.Invoke("reviews"));
            menu.Items.Add("Invite People...", null, (s, e) => CommandRequested?.Invoke("invite"));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Refresh", null, (s, e) => Safe(() => RefreshAllAsync(sync: true)));
        }

        /// <summary>Asks for a name and makes the folder; directly inside a subsystem it appears on everyone's PC.</summary>
        private async Task NewSubfolderAsync(VaultInfo vault, string parent)
        {
            string name;
            using (var prompt = new Form
            {
                Text = "SwVault - New subfolder",
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Font = Font,
            })
            {
                var box = new TextBox { Width = 300 };
                var ok = new Button { Text = "Create", DialogResult = DialogResult.OK, AutoSize = true };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
                var layout = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Padding = new Padding(12) };
                layout.Controls.Add(new Label { Text = "New folder inside " + (parent.Length == 0 ? vault.Name : parent.Replace("/", " > ")) + ":", AutoSize = true });
                layout.Controls.Add(box);
                var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Width = 300, Margin = new Padding(0, 10, 0, 0) };
                row.Controls.Add(cancel);
                row.Controls.Add(ok);
                layout.Controls.Add(row);
                prompt.Controls.Add(layout);
                prompt.AcceptButton = ok;
                prompt.CancelButton = cancel;
                if (prompt.ShowDialog(UiThread.Owner) != DialogResult.OK) return;
                name = box.Text.Trim().TrimEnd('.');
            }
            if (name.Length == 0) return;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.StartsWith(".swvault", StringComparison.OrdinalIgnoreCase))
            {
                UiThread.ShowInfo("That name can't be used for a folder.");
                return;
            }
            var created = await _agent.CreateFolderAsync(vault.Id, parent.Length == 0 ? name : parent + "/" + name);
            await UiThread.OnUi();
            await RefreshFoldersAsync();
            await UiThread.OnUi();
            var node = FindFolderNode(created.Path);
            if (node != null)
            {
                for (var n = node.Parent; n != null; n = n.Parent) n.Expand();
                _folders.SelectedNode = node;
                node.EnsureVisible();
            }
            _connection.Text = created.Shared
                ? name + " was added; it appears on everyone's PC."
                : name + " was added on this PC. Teammates get it with the first file checked in to it.";
        }

        private TreeNode FindFolderNode(string vaultPath)
        {
            TreeNode Search(TreeNodeCollection nodes)
            {
                foreach (TreeNode node in nodes)
                {
                    if (node.Tag is string path && string.Equals(path, vaultPath, StringComparison.OrdinalIgnoreCase)) return node;
                    var found = Search(node.Nodes);
                    if (found != null) return found;
                }
                return null;
            }
            return Search(_folders.Nodes);
        }

        private void Run(string what, Func<Task> body, bool requireSelection = false)
        {
            if (requireSelection && SelectedPaths.Count == 0) return;
            UiThread.Run(what, body);
        }

        /// <summary>Background refreshes: failures show in the status line instead of an error dialog.</summary>
        private async void Safe(Func<Task> body)
        {
            if (!UiThread.OnUiThread)
            {
                UiThread.Post(() => Safe(body));
                return;
            }
            UiThread.EnsureContext();
            try
            {
                await body();
            }
            catch (Exception ex)
            {
                Log.Warn("Task pane: " + ex.Message);
                if (!IsDisposed) _connection.Text = ex.Message;
            }
        }

        // ------------------------------------------------------------ loading

        public async Task RefreshAllAsync(bool sync)
        {
            try
            {
                var selectedId = CurrentVault?.Id;
                if (sync && selectedId != null) await _agent.SyncVaultAsync(selectedId);
                var vaults = await _agent.GetVaultsAsync();
                await UiThread.OnUi();
                _vaultList = vaults;
                _vaults.BeginUpdate();
                _vaults.Items.Clear();
                foreach (var v in _vaultList) _vaults.Items.Add(v.Name + "  (" + v.LocalRoot + ")");
                _vaults.EndUpdate();
                var index = Array.FindIndex(_vaultList, v => v.Id == selectedId);
                if (_vaultList.Length > 0) _vaults.SelectedIndex = index >= 0 ? index : 0;
                else _connection.Text = "No vault connected yet - click 'Connect vault...'.";
                // Selecting the vault loads the tree, but only when the selection changed.
                if (_folders.Nodes.Count == 0 && CurrentVault != null) await LoadRootAsync();
                else if (sync) await RefreshFoldersAsync();
                UpdateConnectionLabel();
            }
            catch (AgentException ex)
            {
                _connection.Text = ex.Message;
                Log.Warn("Task pane refresh: " + ex.Message);
            }
        }

        private void UpdateConnectionLabel()
        {
            var v = CurrentVault;
            if (!_agent.IsConnected) _connection.Text = "SwVault agent not connected.";
            else if (v == null) return;
            else _connection.Text = (v.Online ? "Online" : "Offline") + " - signed in as " + (v.UserLogin ?? "?") + (v.LastError != null && !v.Online ? " - " + v.LastError : "");
        }

        private bool WantsFlat => _filter.SelectedIndex > 0 || SearchText.Length > 0;

        /// <summary>Switches between the folder tree and the flat list of a filter or search, keeping what was expanded.</summary>
        private async Task ShowModeAsync()
        {
            if (WantsFlat)
            {
                if (!_flat)
                {
                    _expandedBeforeFlat = new HashSet<string>(AllNodes(_folders.Nodes).Where(n => n.Tag is string && n.IsExpanded).Select(n => (string)n.Tag), StringComparer.OrdinalIgnoreCase);
                    _selectedBeforeFlat = SelectedFolder;
                }
                await LoadFlatAsync();
                return;
            }
            if (!_flat && _folders.Nodes.Count > 0) return;
            await LoadRootAsync();
        }

        private static IEnumerable<TreeNode> AllNodes(TreeNodeCollection nodes)
        {
            foreach (TreeNode node in nodes)
            {
                yield return node;
                foreach (var child in AllNodes(node.Nodes)) yield return child;
            }
        }

        private async Task LoadRootAsync()
        {
            var vault = CurrentVault;
            ClearPicked();
            _folders.Nodes.Clear();
            _flat = false;
            if (vault == null) return;
            if (WantsFlat)
            {
                await LoadFlatAsync();
                return;
            }
            var root = FolderNode(vault.Name, "");
            _folders.Nodes.Add(root);
            UpdateConnectionLabel();
            await ExpandAsync(root);
            await UiThread.OnUi();
            if (root.TreeView == null) return;
            root.Expand();
            _folders.SelectedNode = root;

            // Back from a filter or search: open what was open before.
            var reopen = _expandedBeforeFlat;
            var reselect = _selectedBeforeFlat;
            _expandedBeforeFlat = null;
            _selectedBeforeFlat = null;
            if (reopen != null) await ReopenAsync(root, reopen);
            await UiThread.OnUi();
            var selected = string.IsNullOrEmpty(reselect) ? null : FindFolderNode(reselect);
            if (selected != null) _folders.SelectedNode = selected;
        }

        private async Task ReopenAsync(TreeNode node, HashSet<string> expanded)
        {
            foreach (var child in node.Nodes.Cast<TreeNode>().Where(n => n.Tag is string path && expanded.Contains(path)).ToList())
            {
                await ExpandAsync(child);
                await UiThread.OnUi();
                if (child.TreeView == null) return;
                child.Expand();
                await ReopenAsync(child, expanded);
            }
        }

        /// <summary>The matches of a filter or search, as one list (each file shown with its folder).</summary>
        private async Task LoadFlatAsync()
        {
            var vault = CurrentVault;
            if (vault == null) return;
            try
            {
                var filter = new[] { StatusFilter.All, StatusFilter.MyCheckouts, StatusFilter.Outdated, StatusFilter.InReview, StatusFilter.CheckedOut, StatusFilter.Modified }[Math.Max(0, _filter.SelectedIndex)];
                var entries = await _agent.SearchAsync(vault.Id, SearchText, filter);
                await UiThread.OnUi();
                var picked = new HashSet<string>(SelectedPaths, StringComparer.OrdinalIgnoreCase);
                ClearPicked();
                _flat = true;
                _folders.BeginUpdate();
                _folders.Nodes.Clear();
                foreach (var e in Ordered(entries.Where(x => !x.IsFolder)))
                    _folders.Nodes.Add(FileNode(e, withFolder: true));
                if (_folders.Nodes.Count == 0)
                    _folders.Nodes.Add(new TreeNode("Nothing matches.") { ForeColor = Color.Gray, ImageKey = "notlocal", SelectedImageKey = "notlocal" });
                _folders.EndUpdate();
                Reselect(_folders.Nodes, picked);
            }
            catch (AgentException ex)
            {
                _connection.Text = ex.Message;
            }
        }

        /// <summary>Loads a folder's sub-folders and files the first time it is opened.</summary>
        private async Task ExpandAsync(TreeNode node)
        {
            var vault = CurrentVault;
            if (vault == null || !(node.Tag is string folder) || IsLoaded(node)) return;
            try
            {
                var entries = await _agent.ListFolderAsync(vault.Id, folder);
                await UiThread.OnUi();
                if (node.TreeView == null || IsLoaded(node)) return;
                _folders.BeginUpdate();
                node.Nodes.Clear();
                foreach (var sub in entries.Where(e => e.IsFolder).OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
                    node.Nodes.Add(FolderNode(Path.GetFileName(sub.Path), sub.Path));
                foreach (var file in Ordered(entries.Where(e => !e.IsFolder)))
                    node.Nodes.Add(FileNode(file, withFolder: false));
                _folders.EndUpdate();
            }
            catch (AgentException ex)
            {
                _connection.Text = ex.Message;
            }
        }

        /// <summary>A folder node with a placeholder child, so it can be expanded and loaded on demand.</summary>
        private static TreeNode FolderNode(string text, string vaultPath)
        {
            var node = new TreeNode(text) { Tag = vaultPath, ImageKey = Icons.Folder16, SelectedImageKey = Icons.Folder16 };
            node.Nodes.Add(new TreeNode("...") { Tag = null });
            return node;
        }

        private static bool IsLoaded(TreeNode node) => !(node.Nodes.Count == 1 && node.Nodes[0].Tag == null);

        /// <summary>Assemblies first with the top level at the top, then parts, drawings and the rest.</summary>
        private static IEnumerable<FileStatusDto> Ordered(IEnumerable<FileStatusDto> files) =>
            files.OrderBy(TypeRank).ThenBy(x => TypeRank(x) == 0 ? x.Level : 0).ThenBy(x => Path.GetFileName(x.Path), StringComparer.OrdinalIgnoreCase);

        private TreeNode FileNode(FileStatusDto e, bool withFolder)
        {
            var node = new TreeNode();
            Fill(node, e, withFolder);
            return node;
        }

        /// <summary>
        /// "Bracket.SLDPRT   v3 · out: alex · Outdated": the name, then only what is worth noticing
        /// (the icon already shows the type and state; the tooltip has everything).
        /// </summary>
        private void Fill(TreeNode node, FileStatusDto e, bool withFolder)
        {
            var notes = new List<string>();
            if (e.ServerVersion > 0) notes.Add("v" + e.ServerVersion);
            if (e.LockState == LockState.Other) notes.Add("out: " + e.LockOwner);
            else if (e.LockState != LockState.None) notes.Add(e.LockState == LockState.MineElsewhere ? "out: you (elsewhere)" : "out: you");
            if (e.LocalState != LocalState.UpToDate) notes.Add(ShortState(e));
            if (!string.IsNullOrEmpty(e.State) && !string.Equals(e.State, "WIP", StringComparison.OrdinalIgnoreCase)) notes.Add(e.State);
            if (!string.IsNullOrEmpty(e.Revision)) notes.Add("rev " + e.Revision);
            var folder = withFolder ? PathFolder(e.Path) : "";
            node.Text = Path.GetFileName(e.Path) + (notes.Count > 0 ? "   " + string.Join(" \u00b7 ", notes) : "") + (folder.Length > 0 ? "   (" + folder + ")" : "");
            node.Tag = e;
            node.ImageKey = node.SelectedImageKey = Icons.FileKey(_images, e);
            node.ToolTipText = e.Path + "\n" + TypeName(e) + " - " + VaultDialog.Describe(e) + (string.IsNullOrEmpty(e.Comment) ? "" : "\nLast comment: " + e.Comment);
        }

        /// <summary>Selects and picks again the files that were selected before a list was rebuilt.</summary>
        private void Reselect(TreeNodeCollection nodes, HashSet<string> picked, string selected = null)
        {
            foreach (TreeNode node in nodes)
            {
                if (!(node.Tag is FileStatusDto file)) continue;
                if (picked.Count > 1 && picked.Contains(file.LocalPath)) Pick(node);
                if (selected != null ? string.Equals(file.LocalPath, selected, StringComparison.OrdinalIgnoreCase) : picked.Count == 1 && picked.Contains(file.LocalPath))
                    _folders.SelectedNode = node;
            }
        }

        /// <summary>
        /// Brings the tree up to date without collapsing it: new folders and files appear, ones that
        /// are gone disappear, and each file's state is refreshed. Only opened folders are asked about.
        /// </summary>
        private async Task RefreshFoldersAsync()
        {
            var vault = CurrentVault;
            if (vault == null) return;
            if (WantsFlat)
            {
                await LoadFlatAsync();
                return;
            }
            if (_flat || _folders.Nodes.Count == 0)
            {
                await LoadRootAsync();
                return;
            }
            try
            {
                await RefreshNodeAsync(vault, _folders.Nodes[0]);
            }
            catch (AgentException ex)
            {
                _connection.Text = ex.Message;
            }
        }

        private async Task RefreshNodeAsync(VaultInfo vault, TreeNode node)
        {
            if (!IsLoaded(node)) return;
            var entries = await _agent.ListFolderAsync(vault.Id, (string)node.Tag);
            await UiThread.OnUi();
            if (node.TreeView == null) return; // the tree was reloaded meanwhile

            _folders.BeginUpdate();
            try
            {
                // Folders: drop the ones that are gone, add the new ones in name order above the files.
                var wanted = entries.Where(e => e.IsFolder).Select(e => e.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                var folderNodes = node.Nodes.Cast<TreeNode>().Where(n => n.Tag is string).ToList();
                foreach (var gone in folderNodes.Where(n => !wanted.Contains((string)n.Tag, StringComparer.OrdinalIgnoreCase)))
                {
                    if (IsOrContains(gone, _folders.SelectedNode)) _folders.SelectedNode = node;
                    foreach (var picked in _picked.Where(p => IsOrContains(gone, p)).ToList()) Unpick(picked);
                    gone.Remove();
                }
                foreach (var path in wanted.Where(p => !folderNodes.Any(n => string.Equals((string)n.Tag, p, StringComparison.OrdinalIgnoreCase))))
                {
                    var index = node.Nodes.Cast<TreeNode>().TakeWhile(n => n.Tag is string other && string.Compare(other, path, StringComparison.OrdinalIgnoreCase) < 0).Count();
                    node.Nodes.Insert(index, FolderNode(Path.GetFileName(path), path));
                }

                // Files: update the ones still there in place (keeps the selection), then fix the order.
                var files = Ordered(entries.Where(e => !e.IsFolder)).ToList();
                var fileNodes = node.Nodes.Cast<TreeNode>().Where(n => n.Tag is FileStatusDto).ToList();
                foreach (var gone in fileNodes.Where(n => !files.Any(f => string.Equals(f.Path, ((FileStatusDto)n.Tag).Path, StringComparison.OrdinalIgnoreCase))))
                {
                    if (_folders.SelectedNode == gone) _folders.SelectedNode = node;
                    if (_picked.Contains(gone)) Unpick(gone);
                    gone.Remove();
                    fileNodes.Remove(gone);
                }
                var firstFile = node.Nodes.Cast<TreeNode>().Count(n => n.Tag is string);
                for (var i = 0; i < files.Count; i++)
                {
                    var existing = fileNodes.FirstOrDefault(n => string.Equals(((FileStatusDto)n.Tag).Path, files[i].Path, StringComparison.OrdinalIgnoreCase));
                    if (existing == null)
                    {
                        node.Nodes.Insert(firstFile + i, FileNode(files[i], withFolder: false));
                        continue;
                    }
                    Fill(existing, files[i], withFolder: false);
                    if (existing.Index != firstFile + i)
                    {
                        // Moving a node means removing it, which would drop the selection.
                        var wasSelected = _folders.SelectedNode == existing;
                        existing.Remove();
                        node.Nodes.Insert(firstFile + i, existing);
                        if (wasSelected) _folders.SelectedNode = existing;
                    }
                }
            }
            finally
            {
                _folders.EndUpdate();
            }

            foreach (var child in node.Nodes.Cast<TreeNode>().Where(n => n.Tag is string).ToList())
                await RefreshNodeAsync(vault, child);
        }

        private static bool IsOrContains(TreeNode ancestor, TreeNode node)
        {
            for (; node != null; node = node.Parent)
                if (node == ancestor) return true;
            return false;
        }

        /// <summary>Assemblies first, then parts, drawings and everything else.</summary>
        private static int TypeRank(FileStatusDto e)
        {
            switch ((Path.GetExtension(e.Path) ?? "").ToLowerInvariant())
            {
                case ".sldasm": return 0;
                case ".sldprt": return 1;
                case ".slddrw": return 2;
                default: return 3;
            }
        }

        private static string TypeName(FileStatusDto e)
        {
            switch (TypeRank(e))
            {
                case 0: return e.Level == 0 ? "Assembly (top level)" : "Sub-assembly";
                case 1: return "Part";
                case 2: return "Drawing";
                default: return CadFormats.Of(e.Path)?.Name ?? "File";
            }
        }

        private static string ShortState(FileStatusDto e)
        {
            switch (e.LocalState)
            {
                case LocalState.UpToDate: return "Up to date";
                case LocalState.Outdated: return "Outdated (v" + e.LocalVersion + ")";
                case LocalState.Modified: return "Modified";
                case LocalState.Conflict: return "Conflict";
                case LocalState.LocalOnly: return "New";
                case LocalState.NotLocal: return "Not local";
                case LocalState.MissingLocally: return "Missing";
                case LocalState.DeletedOnServer: return "Deleted";
                default: return e.LocalState.ToString();
            }
        }

        /// <summary>Shows the active document's vault status at the bottom of the pane.</summary>
        public async Task ShowActiveDocAsync(string path)
        {
            _activePath = path;
            if (path == null)
            {
                _active.Text = "No document open.";
                _active.BackColor = SystemColors.Control;
                _add.Visible = false;
                return;
            }
            if (path.Length == 0)
            {
                _active.Text = "This document hasn't been saved. Add to Vault saves it in your vault folder and checks it in.";
                _active.BackColor = SystemColors.Control;
                _add.Visible = true;
                return;
            }
            try
            {
                var status = (await _agent.GetStatusAsync(path)).FirstOrDefault();
                await UiThread.OnUi();
                if (path != _activePath) return;
                _add.Visible = status == null || status.LocalState == LocalState.LocalOnly;
                if (status == null)
                {
                    _active.Text = Path.GetFileName(path) + ": not in a vault folder.";
                    _active.BackColor = SystemColors.Control;
                    return;
                }
                var subsystem = await SubsystemTextAsync(path);
                await UiThread.OnUi();
                if (path != _activePath) return;
                _active.Text = Path.GetFileName(path) + "  " + (status.ServerVersion > 0 ? "v" + status.ServerVersion + "  " : "") +
                               (status.State ?? "") + (string.IsNullOrEmpty(status.Revision) ? "" : " rev " + status.Revision) + "\n" + VaultDialog.Describe(status) +
                               (subsystem == null ? "" : "\n" + subsystem);
                _active.BackColor = status.LocalState == LocalState.Outdated || status.LocalState == LocalState.Conflict ? Color.FromArgb(255, 243, 205)
                    : status.LockState == LockState.MineHere ? Color.FromArgb(217, 234, 250)
                    : status.LockState == LockState.Other ? Color.FromArgb(250, 219, 216)
                    : SystemColors.Control;
            }
            catch (AgentException ex)
            {
                _active.Text = ex.Message;
            }
        }
    }
}
