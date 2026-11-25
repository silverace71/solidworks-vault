using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using SwVault.AddIn.Infrastructure;
using SwVault.Protocol;

namespace SwVault.AddIn.Ui
{
    /// <summary>Common look for SwVault dialogs.</summary>
    internal abstract class VaultDialog : Form
    {
        protected readonly Button OkButton = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(88, 28) };
        protected readonly Button CancelButtonControl = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(88, 28) };
        protected readonly FlowLayoutPanel Buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };

        protected VaultDialog(string title, int width, int height)
        {
            Text = title;
            Width = width;
            Height = height;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            Font = SystemFonts.MessageBoxFont;
            AcceptButton = OkButton;
            CancelButton = CancelButtonControl;
            Buttons.Controls.Add(CancelButtonControl);
            Buttons.Controls.Add(OkButton);
            Controls.Add(Buttons);
        }

        public static string Describe(FileStatusDto s)
        {
            if (s == null) return "";
            string local;
            switch (s.LocalState)
            {
                case LocalState.NotLocal: local = "Not local"; break;
                case LocalState.UpToDate: local = "Up to date"; break;
                case LocalState.Outdated: local = "Outdated (v" + s.LocalVersion + ", latest v" + s.ServerVersion + ")"; break;
                case LocalState.Modified: local = "Modified"; break;
                case LocalState.Conflict: local = "Conflict: changed locally and on the server"; break;
                case LocalState.LocalOnly: local = "New (not in vault)"; break;
                case LocalState.MissingLocally: local = "Missing locally"; break;
                case LocalState.DeletedOnServer: local = "Deleted from vault"; break;
                default: local = s.LocalState.ToString(); break;
            }
            switch (s.LockState)
            {
                case LockState.MineHere: local += " - checked out by you"; break;
                case LockState.MineElsewhere: local += " - checked out by you on another PC"; break;
                case LockState.Other: local += " - checked out by " + s.LockOwner; break;
            }
            return local;
        }
    }

    /// <summary>Pick files to check in, add a comment.</summary>
    internal sealed class CheckInDialog : VaultDialog
    {
        private readonly ListView _files = new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true };
        private readonly TextBox _comment = new TextBox { Dock = DockStyle.Fill, Multiline = true, Height = 70, ScrollBars = ScrollBars.Vertical };
        private readonly CheckBox _keep = new CheckBox { Text = "Keep checked out", AutoSize = true };

        public CheckInDialog(IEnumerable<(FileStatusDto Status, bool Checked)> files, string title = "Check In")
            : base(title, 720, 460)
        {
            _files.Columns.Add("File", 330);
            _files.Columns.Add("Status", 340);
            foreach (var (status, check) in files)
            {
                var item = new ListViewItem(new[] { Path.GetFileName(status.LocalPath), Describe(status) }) { Checked = check, Tag = status.LocalPath, ToolTipText = status.LocalPath };
                _files.Items.Add(item);
            }
            _files.ShowItemToolTips = true;

            var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 1, Padding = new Padding(8, 4, 8, 0) };
            bottom.Controls.Add(new Label { Text = VersionNoteDialog.Prompt, AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
            bottom.Controls.Add(_comment);
            bottom.Controls.Add(new Label { Text = VersionNoteDialog.Hint, AutoSize = true, ForeColor = SystemColors.GrayText });
            bottom.Controls.Add(_keep);
            Controls.Add(_files);
            Controls.Add(bottom);
            _files.BringToFront();
            OkButton.Text = title;
            Shown += (s, e) => _comment.Focus();
            OkButton.Click += (s, e) =>
            {
                if (SelectedPaths.Count == 0)
                {
                    DialogResult = DialogResult.None;
                    MessageBox.Show(this, "Select at least one file.", "SwVault");
                }
            };
        }

        public List<string> SelectedPaths => _files.CheckedItems.Cast<ListViewItem>().Select(i => (string)i.Tag).ToList();
        public string Comment => _comment.Text.Trim();
        public bool KeepCheckedOut => _keep.Checked;
    }

    /// <summary>
    /// Add to Vault: where in the vault (project, subsystem, folder) the file goes, and its name.
    /// Without a file name it is the folder chooser for Move.
    /// </summary>
    internal sealed class AddToVaultDialog : VaultDialog
    {
        private readonly TreeView _folders = new TreeView { Dock = DockStyle.Fill, HideSelection = false, PathSeparator = "/" };
        private readonly TextBox _name = new TextBox { Dock = DockStyle.Fill };
        private readonly Label _where = new Label { Dock = DockStyle.Fill, AutoSize = true, ForeColor = Color.DimGray, AutoEllipsis = true };
        private readonly Button _newFolder = new Button { Text = "New folder...", AutoSize = true, MinimumSize = new Size(100, 28) };
        private readonly string _localRoot;
        private readonly string _extension;
        private readonly bool _pickFolderOnly;

        /// <param name="folders">Every folder in the vault, as vault paths ("CB8/Steering Wheel/Fasteners").</param>
        /// <param name="selected">The vault path of the folder to start in ("" for the top).</param>
        public AddToVaultDialog(string vaultName, string localRoot, IEnumerable<string> folders, string selected, string fileName, string extension,
            string title = "Add to Vault", string prompt = null)
            : base(title, 520, 560)
        {
            _pickFolderOnly = fileName == null;
            _localRoot = localRoot.TrimEnd('\\');
            _extension = extension;
            MinimumSize = new Size(420, 400);
            FormBorderStyle = FormBorderStyle.Sizable;
            _folders.ImageList = Icons.StateImages();

            var root = Node(vaultName, "");
            _folders.Nodes.Add(root);
            foreach (var folder in folders.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) Ensure(folder);
            root.Expand();
            var start = Find(selected ?? "") ?? root;
            _folders.SelectedNode = start;
            for (var n = start; n != null; n = n.Parent) n.Expand();

            var top = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(8, 8, 8, 4),
                MaximumSize = new Size(500, 0),
                Text = prompt ?? "Choose where in the vault this file goes: a project, a subsystem, or a folder inside one (Fasteners, COTS Parts...).",
            };
            var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, Padding = new Padding(8, 4, 8, 0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            if (!_pickFolderOnly)
            {
                bottom.Controls.Add(new Label { Text = "File name:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
                bottom.Controls.Add(_name, 1, 0);
                _name.Text = Path.GetFileNameWithoutExtension(fileName);
            }
            bottom.Controls.Add(new Label { Text = "Goes to:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            bottom.Controls.Add(_where, 1, 1);

            Controls.Add(_folders);
            Controls.Add(top);
            Controls.Add(bottom);
            _folders.BringToFront();
            Buttons.SendToBack(); // below the file name, at the very bottom
            Buttons.Controls.Add(_newFolder);
            OkButton.Text = _pickFolderOnly ? "Move here" : "Add to Vault";

            _folders.AfterSelect += (s, e) => ShowTarget();
            _name.TextChanged += (s, e) => ShowTarget();
            _newFolder.Click += (s, e) => NewFolder();
            OkButton.Click += (s, e) =>
            {
                var problem = _pickFolderOnly ? null : NameProblem(_name.Text.Trim());
                if (problem == null) return;
                DialogResult = DialogResult.None;
                MessageBox.Show(this, problem, "SwVault", MessageBoxButtons.OK, MessageBoxIcon.Information);
                _name.Focus();
            };
            ShowTarget();
        }

        public string OkText { set => OkButton.Text = value; }

        /// <summary>The vault path of the chosen folder ("" for the top of the vault).</summary>
        public string Folder => _folders.SelectedNode?.Tag as string ?? "";

        /// <summary>The chosen folder on this PC.</summary>
        public string LocalFolderPath => LocalFolder(Folder);

        /// <summary>Where the file is saved on this PC.</summary>
        public string TargetPath => Path.Combine(LocalFolder(Folder), _name.Text.Trim() + _extension);

        private string LocalFolder(string vaultPath) =>
            vaultPath.Length == 0 ? _localRoot : Path.Combine(_localRoot, vaultPath.Replace('/', '\\'));

        private static TreeNode Node(string text, string vaultPath) =>
            new TreeNode(text) { Tag = vaultPath, ImageKey = Icons.Folder16, SelectedImageKey = Icons.Folder16 };

        private TreeNode Find(string vaultPath)
        {
            var node = _folders.Nodes[0];
            foreach (var part in vaultPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                node = node.Nodes.Cast<TreeNode>().FirstOrDefault(n => string.Equals(n.Text, part, StringComparison.OrdinalIgnoreCase));
                if (node == null) return null;
            }
            return node;
        }

        /// <summary>The node for a vault path, adding it and any missing parents.</summary>
        private TreeNode Ensure(string vaultPath)
        {
            var node = _folders.Nodes[0];
            var path = "";
            foreach (var part in vaultPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                path = path.Length == 0 ? part : path + "/" + part;
                var child = node.Nodes.Cast<TreeNode>().FirstOrDefault(n => string.Equals(n.Text, part, StringComparison.OrdinalIgnoreCase));
                if (child == null)
                {
                    child = Node(part, path);
                    node.Nodes.Add(child);
                }
                node = child;
            }
            return node;
        }

        private void ShowTarget()
        {
            _where.Text = (Folder.Length == 0 ? "the top of the vault" : Folder.Replace("/", " > ")) + "  (" + (_pickFolderOnly ? LocalFolderPath : TargetPath) + ")";
        }

        private static string NameProblem(string name)
        {
            if (name.Length == 0) return "Give the file a name.";
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "A file name can't contain any of these: \\ / : * ? \" < > |";
            return null;
        }

        private void NewFolder()
        {
            var parent = _folders.SelectedNode ?? _folders.Nodes[0];
            using (var prompt = new Form
            {
                Text = "SwVault - New folder",
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
                var parentPath = (string)parent.Tag;
                layout.Controls.Add(new Label { Text = "New folder inside " + (parentPath.Length == 0 ? parent.Text : parentPath.Replace("/", " > ")) + ":", AutoSize = true });
                layout.Controls.Add(box);
                var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Width = 300, Margin = new Padding(0, 10, 0, 0) };
                row.Controls.Add(cancel);
                row.Controls.Add(ok);
                layout.Controls.Add(row);
                prompt.Controls.Add(layout);
                prompt.AcceptButton = ok;
                prompt.CancelButton = cancel;
                if (prompt.ShowDialog(this) != DialogResult.OK) return;

                var name = box.Text.Trim().TrimEnd('.');
                if (name.Length == 0) return;
                if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.StartsWith(".swvault", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(this, "That name can't be used for a folder.", "SwVault", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                var vaultPath = ((string)parent.Tag).Length == 0 ? name : parent.Tag + "/" + name;
                try
                {
                    Directory.CreateDirectory(LocalFolder(vaultPath));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
                {
                    MessageBox.Show(this, "Couldn't create the folder: " + ex.Message, "SwVault", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                _folders.SelectedNode = Ensure(vaultPath);
                _folders.SelectedNode.EnsureVisible();
            }
        }
    }

    /// <summary>
    /// Asks for the note saved with a new version; it is what the history shows for that version.
    /// Left blank, the version is noted by its number ("Version 3").
    /// </summary>
    internal sealed class VersionNoteDialog : VaultDialog
    {
        public const string Prompt = "Version note: what changed and why";
        public const string Hint = "Shown in the file's history. Leave it blank and the version is just noted by its number.";

        private readonly TextBox _note = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };

        public VersionNoteDialog(string title, string caption, string suggestion)
            : base(title, 560, 300)
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(8) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { Text = caption, AutoSize = true, MaximumSize = new Size(520, 0) });
            layout.Controls.Add(new Label { Text = Prompt, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(0, 8, 0, 0) });
            layout.Controls.Add(_note);
            layout.Controls.Add(new Label { Text = Hint, AutoSize = true, ForeColor = SystemColors.GrayText });
            Controls.Add(layout);
            layout.BringToFront();
            _note.Text = suggestion ?? "";
            AcceptButton = null; // Enter starts a new line in the note
            OkButton.Text = title;
            Shown += (s, e) => { _note.Focus(); _note.SelectAll(); };
        }

        public string Note => _note.Text.Trim();
    }

    /// <summary>Check out an assembly or drawing, optionally with some of its references.</summary>
    internal sealed class CheckOutDialog : VaultDialog
    {
        private readonly TreeView _tree = new TreeView { Dock = DockStyle.Fill, CheckBoxes = true };

        public CheckOutDialog(ReferenceNodeDto root) : base("Check Out", 640, 480)
        {
            var label = new Label { Dock = DockStyle.Top, Height = 36, Padding = new Padding(8, 8, 8, 0), Text = "Tick the files you want to edit. Referenced files you don't tick stay read-only." };
            _tree.Nodes.Add(Build(root, isRoot: true));
            _tree.ExpandAll();
            Controls.Add(_tree);
            Controls.Add(label);
            _tree.BringToFront();
            OkButton.Text = "Check Out";
        }

        private static TreeNode Build(ReferenceNodeDto node, bool isRoot)
        {
            var status = node.Status;
            var blocked = status != null && status.LockState == LockState.Other;
            var text = Path.GetFileName(node.LocalPath) + "   (" + Describe(status) + ")";
            var tn = new TreeNode(text) { Tag = node.LocalPath, Checked = isRoot && !blocked };
            if (blocked) tn.ForeColor = Color.Gray;
            foreach (var child in node.Children ?? new ReferenceNodeDto[0]) tn.Nodes.Add(Build(child, false));
            return tn;
        }

        public List<string> SelectedPaths
        {
            get
            {
                var result = new List<string>();
                void Walk(TreeNodeCollection nodes)
                {
                    foreach (TreeNode n in nodes)
                    {
                        if (n.Checked && !result.Contains((string)n.Tag, StringComparer.OrdinalIgnoreCase)) result.Add((string)n.Tag);
                        Walk(n.Nodes);
                    }
                }
                Walk(_tree.Nodes);
                return result;
            }
        }
    }

    /// <summary>Version history as a timeline, with open, get-version and rollback.</summary>
    internal sealed class HistoryDialog : VaultDialog
    {
        public enum HistoryAction { None, GetVersion, Rollback, Open }

        private readonly VersionHistoryView _view = new VersionHistoryView(vertical: false) { Dock = DockStyle.Fill };

        public HistoryDialog(string path, VersionInfoDto[] versions, FileStatusDto status, bool hasReferences, bool canOpen,
            Func<VersionInfoDto, System.Threading.Tasks.Task<Image>> preview = null)
            : base("History - " + Path.GetFileName(path), 980, 600)
        {
            _view.PreviewProvider = preview;
            MaximizeBox = true;
            FormBorderStyle = FormBorderStyle.Sizable;
            Controls.Add(_view);
            _view.BringToFront();
            _view.SetHistory(versions, status, hasReferences, canOpen);

            OkButton.Visible = false;
            CancelButtonControl.Text = "Close";
            _view.OpenRequested += (v, asBuilt) => Finish(HistoryAction.Open, v, asBuilt);
            _view.GetRequested += (v, asBuilt) => Finish(HistoryAction.GetVersion, v, asBuilt);
            _view.RollbackRequested += v => Finish(HistoryAction.Rollback, v, false);
        }

        private void Finish(HistoryAction action, VersionInfoDto version, bool asBuilt)
        {
            Action = action;
            SelectedVersion = version;
            AsBuilt = asBuilt;
            DialogResult = DialogResult.OK;
        }

        public HistoryAction Action { get; private set; }
        public VersionInfoDto SelectedVersion { get; private set; }
        public bool AsBuilt { get; private set; }
    }

    /// <summary>Choose a workflow transition.</summary>
    internal sealed class TransitionDialog : VaultDialog
    {
        private readonly List<RadioButton> _options = new List<RadioButton>();
        private readonly TextBox _comment = new TextBox { Dock = DockStyle.Fill, Multiline = true, Height = 60 };

        public TransitionDialog(string path, string currentState, string revision, TransitionOptionDto[] options)
            : base("Change State - " + Path.GetFileName(path), 560, 380)
        {
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(12), WrapContents = false, AutoScroll = true };
            panel.Controls.Add(new Label { Text = "Current state: " + currentState + (string.IsNullOrEmpty(revision) ? "" : "   Revision: " + revision), AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
            foreach (var option in options)
            {
                var text = option.Name + "  ->  " + option.To + (option.BumpRevision ? "  (revision " + option.NextRevision + ")" : "");
                if (!option.Allowed) text += "   [" + option.Reason + "]";
                var radio = new RadioButton { Text = text, AutoSize = true, Enabled = option.Allowed, Tag = option };
                _options.Add(radio);
                panel.Controls.Add(radio);
                foreach (var warning in option.Warnings ?? new string[0])
                    panel.Controls.Add(new Label { Text = "    ! " + warning, AutoSize = true, ForeColor = Color.DarkOrange, MaximumSize = new Size(500, 0) });
            }
            if (options.Length == 0) panel.Controls.Add(new Label { Text = "No workflow actions are available from this state.", AutoSize = true });
            var first = _options.FirstOrDefault(o => o.Enabled);
            if (first != null) first.Checked = true;

            var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8, 0, 8, 0) };
            bottom.Controls.Add(new Label { Text = "Note (optional). Releasing records you as the approver.", AutoSize = true });
            bottom.Controls.Add(_comment);
            Controls.Add(panel);
            Controls.Add(bottom);
            panel.BringToFront();
            OkButton.Enabled = first != null;
        }

        public TransitionOptionDto Selected => _options.FirstOrDefault(o => o.Checked)?.Tag as TransitionOptionDto;
        public string Comment => _comment.Text.Trim();
    }

    /// <summary>Shows a list of files (where used, warnings); double-click opens one.</summary>
    internal sealed class FileListDialog : VaultDialog
    {
        private readonly ListBox _list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };

        public FileListDialog(string title, string caption, IEnumerable<string> files) : base(title, 620, 380)
        {
            Controls.Add(_list);
            Controls.Add(new Label { Dock = DockStyle.Top, Height = 30, Padding = new Padding(8, 8, 8, 0), Text = caption });
            _list.BringToFront();
            foreach (var f in files) _list.Items.Add(f);
            OkButton.Text = "Open";
            OkButton.Enabled = false;
            CancelButtonControl.Text = "Close";
            _list.SelectedIndexChanged += (s, e) => OkButton.Enabled = _list.SelectedItem != null;
            _list.DoubleClick += (s, e) => { if (_list.SelectedItem != null) DialogResult = DialogResult.OK; };
        }

        public string SelectedFile => _list.SelectedItem as string;
    }

    /// <summary>Connect this PC to a vault.</summary>
    internal sealed class VaultSetupDialog : VaultDialog
    {
        private readonly TextBox _url = new TextBox { Width = 380 };
        private readonly TextBox _root = new TextBox { Width = 380 };
        private readonly TextBox _user = new TextBox { Width = 200 };
        private readonly TextBox _token = new TextBox { Width = 200, UseSystemPasswordChar = true };
        private readonly Label _problem = new Label { AutoSize = true, MaximumSize = new Size(380, 0), ForeColor = Color.Firebrick };

        public VaultSetupDialog(VaultInfo[] existing) : base("SwVault - Connect a Vault", 600, 330)
        {
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12), AutoSize = true };
            void Row(string label, Control control)
            {
                grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) });
                grid.Controls.Add(control);
            }
            Row("Repository URL", _url);
            Row("Vault folder", _root);
            Row("User name", _user);
            Row("Access token", _token);
            grid.Controls.Add(new Label { Text = "", AutoSize = true });
            grid.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(380, 0),
                ForeColor = Color.DimGray,
                Text = "Leave the folder empty to use the vault's shared folder. Create an access token in your server profile (Settings > Applications). "
                    + (existing.Length > 0 ? "Connected: " + string.Join(", ", existing.Select(v => v.Name)) : ""),
            });
            grid.Controls.Add(new Label { Text = "", AutoSize = true });
            grid.Controls.Add(_problem);
            Controls.Add(grid);
            grid.BringToFront();
            OkButton.Text = "Connect";
            OkButton.Click += (s, e) =>
            {
                var missing = _url.Text.Trim().Length == 0 ? "Paste the vault address your admin gave you (like https://server/team/cad)."
                    : _user.Text.Trim().Length == 0 ? "Enter your user name on the vault server."
                    : _token.Text.Trim().Length == 0 ? "Enter your access token (server profile > Settings > Applications)."
                    : null;
                if (missing == null) return;
                Problem = missing;
                DialogResult = DialogResult.None;
            };
        }

        /// <summary>Why the last attempt failed, shown in red under the fields.</summary>
        public string Problem
        {
            set => _problem.Text = value ?? "";
        }

        public VaultAddRequest Request => new VaultAddRequest
        {
            RemoteUrl = _url.Text.Trim(),
            LocalRoot = _root.Text.Trim(),
            UserName = _user.Text.Trim(),
            Token = _token.Text.Trim(),
        };
    }

    /// <summary>Small progress window for vault jobs; stays on top of SOLIDWORKS but doesn't block it.</summary>
    internal sealed class ProgressWindow : Form
    {
        private readonly Label _message = new Label { Dock = DockStyle.Top, Height = 40, Padding = new Padding(10, 12, 10, 0), AutoEllipsis = true };
        private readonly ProgressBar _bar = new ProgressBar { Dock = DockStyle.Top, Height = 18, Style = ProgressBarStyle.Marquee, Margin = new Padding(10) };
        private readonly Button _cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(88, 28), Anchor = AnchorStyles.Right };
        private readonly Action<string> _onCancel;
        private string _jobId;

        /// <summary>True once Cancel was clicked.</summary>
        public bool CancelRequested { get; private set; }

        /// <param name="onCancel">Given the job's id when Cancel is clicked; null for work that can't be stopped half way.</param>
        public ProgressWindow(string title, Action<string> onCancel = null)
        {
            _onCancel = onCancel;
            Text = "SwVault - " + title;
            Width = 420;
            Height = onCancel == null ? 130 : 172;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            ControlBox = false;
            Font = SystemFonts.MessageBoxFont;
            var pad = new Panel { Dock = DockStyle.Top, Height = 10 };
            if (onCancel != null)
            {
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8, 0, 8, 8) };
                buttons.Controls.Add(_cancel);
                Controls.Add(buttons);
                _cancel.Click += (s, e) =>
                {
                    CancelRequested = true;
                    _cancel.Enabled = false;
                    _message.Text = "Cancelling...";
                    if (_jobId != null) _onCancel(_jobId); // else as soon as the agent says which job this is
                };
            }
            Controls.Add(pad);
            Controls.Add(_bar);
            Controls.Add(_message);
            _message.Text = title + "...";
        }

        /// <summary>The download is done and files are being put in place: too late to stop cleanly.</summary>
        public void DisableCancel()
        {
            if (!IsDisposed) _cancel.Enabled = false;
        }

        public void Update(JobInfo job)
        {
            if (IsDisposed) return;
            if (_jobId == null && !string.IsNullOrEmpty(job.JobId))
            {
                _jobId = job.JobId;
                if (CancelRequested) _onCancel?.Invoke(_jobId);
            }
            if (CancelRequested) return; // keep showing "Cancelling..."
            if (!string.IsNullOrEmpty(job.Message)) _message.Text = job.Message;
            if (job.Progress > 0 && job.Progress <= 1)
            {
                _bar.Style = ProgressBarStyle.Continuous;
                _bar.Value = (int)(job.Progress * 100);
            }
        }

        public static ProgressWindow ShowFor(string title, Action<string> onCancel = null)
        {
            var window = new ProgressWindow(title, onCancel);
            if (UiThread.Owner != null)
            {
                window.StartPosition = FormStartPosition.Manual;
                var screen = Screen.FromHandle(UiThread.Owner.Handle).WorkingArea;
                window.Location = new Point(screen.Left + (screen.Width - window.Width) / 2, screen.Top + (screen.Height - window.Height) / 2);
                window.Show(UiThread.Owner);
            }
            else
            {
                window.Show();
            }
            return window;
        }
    }
}
