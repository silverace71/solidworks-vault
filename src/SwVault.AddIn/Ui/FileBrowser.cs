using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using SwVault.AddIn.Infrastructure;
using SwVault.AddIn.Sw;
using SwVault.Protocol;

namespace SwVault.AddIn.Ui
{
    /// <summary>
    /// The vault's file browser: search as you type (fuzzy, over names, folders, properties and
    /// comments), thumbnails for every file, and everything the vault knows about the selected one:
    /// status, properties, references, where used and the version timeline. Opens files in SOLIDWORKS.
    /// One window per session; it stays open while you work.
    /// </summary>
    internal sealed class FileBrowser : Form
    {
        private const int MaxResults = 300;
        private const int TileImageSize = 96;

        private static FileBrowser _instance;

        private readonly AgentConnection _agent;
        private readonly VaultCommands _commands;
        private readonly Thumbnails _thumbnails;

        private readonly ComboBox _vaults = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        private readonly TextBox _search = new TextBox { Dock = DockStyle.Fill };
        private readonly ComboBox _type = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
        private readonly ComboBox _filter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        private readonly Label _count = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) };
        private readonly ListView _results = new ListView { Dock = DockStyle.Fill, View = View.Tile, MultiSelect = true, HideSelection = false, FullRowSelect = true, BorderStyle = BorderStyle.None };
        private readonly ImageList _images = new ImageList { ImageSize = new Size(TileImageSize, TileImageSize), ColorDepth = ColorDepth.Depth32Bit };
        private readonly ToolTip _tips = new ToolTip();

        private readonly PictureBox _preview = new PictureBox { Dock = DockStyle.Left, Width = 140, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
        private readonly Label _name = new Label { Dock = DockStyle.Top, Height = 26, AutoEllipsis = true };
        private readonly Label _summary = new Label { Dock = DockStyle.Top, Height = 58, AutoEllipsis = true, ForeColor = SystemColors.GrayText };
        private readonly Button _open = new Button { Text = "Open in SOLIDWORKS", AutoSize = true, BackColor = Color.FromArgb(0, 102, 204), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        private readonly Button _getLatest = new Button { Text = "Get Latest", AutoSize = true };
        private readonly Button _checkOut = new Button { Text = "Check Out", AutoSize = true };
        private readonly Button _checkIn = new Button { Text = "Check In...", AutoSize = true };
        private readonly Button _explorer = new Button { Text = "Show in Explorer", AutoSize = true };
        private readonly TabControl _tabs = new TabControl { Dock = DockStyle.Fill };
        private readonly ListView _properties = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, ShowGroups = true };
        private readonly TreeView _references = new TreeView { Dock = DockStyle.Fill, HideSelection = false };
        private readonly ListView _whereUsed = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable };
        private readonly VersionHistoryView _history = new VersionHistoryView(vertical: true) { Dock = DockStyle.Fill };
        private readonly TabPage _historyTab = new TabPage("History");
        private readonly TabPage _reviewsTab = new TabPage("Reviews");
        private readonly ListView _reviews = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HeaderStyle = ColumnHeaderStyle.Nonclickable };
        private readonly TextBox _reviewText = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window };
        private readonly Button _requestReview = new Button { Text = "Request Review...", AutoSize = true };
        private readonly Button _respond = new Button { Text = "Respond in Reviews...", AutoSize = true };
        private readonly Button _reviewWeb = new Button { Text = "Open on Server", AutoSize = true, Enabled = false };
        private string _reviewsLoadedFor;
        private readonly Panel _details = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        private readonly Label _empty = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = SystemColors.GrayText, Text = "Select a file to see everything about it." };

        private readonly Timer _searchDelay = new Timer { Interval = 180 };
        private readonly Timer _detailsDelay = new Timer { Interval = 120 };
        private readonly Timer _refreshDelay = new Timer { Interval = 700 };

        private readonly Dictionary<string, Image> _fullSize = new Dictionary<string, Image>(); // thumbnails at full size, for the preview
        private VaultInfo[] _vaultList = new VaultInfo[0];
        private int _searchGeneration;
        private int _detailsGeneration;
        private string _historyLoadedFor;
        private FileStatusDto _current;

        private static readonly (string Label, StatusFilter Filter)[] Filters =
        {
            ("Any status", StatusFilter.All),
            ("My check-outs", StatusFilter.MyCheckouts),
            ("Checked out (anyone)", StatusFilter.CheckedOut),
            ("Outdated", StatusFilter.Outdated),
            ("In review", StatusFilter.InReview),
            ("New and modified", StatusFilter.Modified),
        };

        private static readonly (string Label, string[] Extensions)[] Types =
            new[] { ("All files", (string[])null) }.Concat(CadFormats.Groups.Select(g => (g.Label, g.Extensions))).ToArray();

        /// <summary>Shows the browser (creating it the first time), optionally searching for <paramref name="query"/>.</summary>
        public static void ShowBrowser(AgentConnection agent, VaultCommands commands, SwDocs docs, string query = null)
        {
            if (_instance == null || _instance.IsDisposed)
            {
                _instance = new FileBrowser(agent, commands, docs);
                _instance.Show(UiThread.Owner);
            }
            else
            {
                if (_instance.WindowState == FormWindowState.Minimized) _instance.WindowState = FormWindowState.Normal;
                _instance.Activate();
            }
            if (!string.IsNullOrEmpty(query)) _instance._search.Text = query;
            _instance._search.Focus();
            _instance._search.SelectAll();
        }

        private FileBrowser(AgentConnection agent, VaultCommands commands, SwDocs docs)
        {
            _agent = agent;
            _commands = commands;
            _thumbnails = new Thumbnails(docs.SavePreviewBitmap);

            Text = "SwVault - File Browser";
            Width = 1240;
            Height = 780;
            MinimumSize = new Size(760, 480);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            KeyPreview = true;
            Font = SystemFonts.MessageBoxFont;
            Icon = SystemIcons.Application;

            BuildLayout();
            Wire();
            Load += (s, e) => UiThread.Run("File browser", LoadVaultsAsync);
        }

        // ------------------------------------------------------------ layout

        private void BuildLayout()
        {
            foreach (var f in Filters) _filter.Items.Add(f.Label);
            foreach (var t in Types) _type.Items.Add(t.Label);
            _filter.SelectedIndex = 0;
            _type.SelectedIndex = 0;

            var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 5, Padding = new Padding(8, 8, 8, 4) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _search.Font = new Font(Font.FontFamily, Font.Size + 3);
            top.Controls.Add(_vaults, 0, 0);
            top.Controls.Add(_search, 1, 0);
            top.Controls.Add(_type, 2, 0);
            top.Controls.Add(_filter, 3, 0);
            top.Controls.Add(_count, 4, 0);
            var hint = new Label
            {
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Padding = new Padding(0, 2, 0, 0),
                Text = "Type part of a name, number, material or comment; typos and initials work (\"frnt brkt\"). " +
                       "Narrow by a property with name:value, e.g. material:6061, by:alice, state:released, rev:B.",
            };
            top.Controls.Add(hint, 1, 1);
            top.SetColumnSpan(hint, 4);
            _tips.SetToolTip(_search, "Enter opens the selected file; Down moves to the results.");

            _results.LargeImageList = _images;
            _results.TileSize = new Size(380, TileImageSize + 10);
            _results.Columns.Add("Name", 200);
            _results.Columns.Add("Folder", 200);
            _results.Columns.Add("Status", 200);
            _results.Columns.Add("Match", 200);
            _results.ShowItemToolTips = true;
            _results.ContextMenuStrip = BuildMenu();

            // Details: header (thumbnail, name, summary, actions) over tabs.
            var header = new Panel { Dock = DockStyle.Top, Height = 150 };
            var headerText = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 0, 0, 0) };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true };
            actions.Controls.AddRange(new Control[] { _open, _getLatest, _checkOut, _checkIn, _explorer });
            _open.FlatAppearance.BorderSize = 0;
            _open.Font = new Font(Font, FontStyle.Bold);
            _name.Font = new Font(Font.FontFamily, Font.Size + 4, FontStyle.Bold);
            headerText.Controls.Add(actions);
            headerText.Controls.Add(_summary);
            headerText.Controls.Add(_name);
            header.Controls.Add(headerText);
            header.Controls.Add(_preview);

            _properties.Columns.Add("Property", 170);
            _properties.Columns.Add("Value", 340);
            _whereUsed.Columns.Add("File", 220);
            _whereUsed.Columns.Add("Folder", 300);

            var propertiesTab = new TabPage("Properties");
            propertiesTab.Controls.Add(_properties);
            var referencesTab = new TabPage("References");
            referencesTab.Controls.Add(_references);
            var whereUsedTab = new TabPage("Where Used");
            whereUsedTab.Controls.Add(_whereUsed);
            _historyTab.Controls.Add(_history);
            _history.PreviewProvider = v => _current == null ? Task.FromResult<Image>(null) : _agent.SavedThumbnailAsync(_current.LocalPath, v.Commit);

            _reviews.Columns.Add("Review", 120);
            _reviews.Columns.Add("Ver", 40);
            _reviews.Columns.Add("Status", 120);
            _reviews.Columns.Add("Lead", 90);
            _reviews.Columns.Add("Requested by", 90);
            _reviews.Columns.Add("Updated", 110);
            var reviewSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            reviewSplit.Panel1.Controls.Add(_reviews);
            reviewSplit.Panel2.Controls.Add(_reviewText);
            var reviewActions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
            reviewActions.Controls.AddRange(new Control[] { _requestReview, _respond, _reviewWeb });
            _reviewsTab.Controls.Add(reviewSplit);
            _reviewsTab.Controls.Add(reviewActions);
            _tips.SetToolTip(_requestReview, "Ask a subteam lead for a design, simulation or drawing review of this file");
            _tips.SetToolTip(_respond, "Your review inbox: approve, request changes or comment");

            _tabs.TabPages.AddRange(new[] { propertiesTab, referencesTab, whereUsedTab, _historyTab, _reviewsTab });

            var gap = new Panel { Dock = DockStyle.Top, Height = 8 };
            _details.Controls.Add(_tabs);
            _details.Controls.Add(gap);
            _details.Controls.Add(header);
            _tabs.BringToFront();

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, FixedPanel = FixedPanel.Panel2 };
            split.Panel1.Controls.Add(_results);
            split.Panel2.Controls.Add(_details);
            split.Panel2.Controls.Add(_empty);
            split.Panel2.BackColor = SystemColors.Window;
            _details.Visible = false;
            Controls.Add(split);
            Controls.Add(top);
            split.BringToFront();
            Shown += (s, e) => { try { split.SplitterDistance = Math.Max(400, split.Width - 560); } catch (InvalidOperationException) { } };
        }

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open in SOLIDWORKS", null, (s, e) => OpenSelected());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Get Latest", null, (s, e) => RunOnSelection("Get latest", paths => _commands.GetLatestAsync(paths, withReferences: true)));
            menu.Items.Add("Check Out", null, (s, e) => RunOnSelection("Check out", paths => _commands.CheckOutAsync(paths)));
            menu.Items.Add("Check In...", null, (s, e) => RunOnSelection("Check in", paths => _commands.CheckInAsync(paths)));
            menu.Items.Add("Undo Check Out", null, (s, e) => RunOnSelection("Undo check-out", paths => _commands.UndoCheckOutAsync(paths)));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Version History", null, (s, e) => _tabs.SelectedTab = _historyTab);
            menu.Items.Add("Copy Path", null, (s, e) => { var p = SelectedPaths.FirstOrDefault(); if (p != null) Clipboard.SetText(p); });
            menu.Items.Add("Show in Explorer", null, (s, e) => ShowInExplorer(SelectedPaths.FirstOrDefault()));
            menu.Opening += (s, e) => e.Cancel = SelectedPaths.Count == 0;
            return menu;
        }

        private void Wire()
        {
            _search.TextChanged += (s, e) => Restart(_searchDelay);
            _search.KeyDown += OnSearchKeyDown;
            _type.SelectedIndexChanged += (s, e) => Restart(_searchDelay);
            _filter.SelectedIndexChanged += (s, e) => Restart(_searchDelay);
            _vaults.SelectedIndexChanged += (s, e) => Restart(_searchDelay);
            _searchDelay.Tick += (s, e) => { _searchDelay.Stop(); Safe(SearchAsync); };

            _results.SelectedIndexChanged += (s, e) => Restart(_detailsDelay);
            _results.DoubleClick += (s, e) => OpenSelected();
            _results.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; OpenSelected(); }
                else if (e.KeyCode == Keys.Up && _results.SelectedIndices.Count > 0 && _results.SelectedIndices[0] == 0) { e.Handled = true; _search.Focus(); }
            };
            _detailsDelay.Tick += (s, e) => { _detailsDelay.Stop(); Safe(ShowSelectedAsync); };
            _refreshDelay.Tick += (s, e) => { _refreshDelay.Stop(); _historyLoadedFor = null; _reviewsLoadedFor = null; Safe(SearchAsync); };

            _open.Click += (s, e) => OpenPath(_current?.LocalPath);
            _getLatest.Click += (s, e) => RunOnCurrent("Get latest", p => _commands.GetLatestAsync(new[] { p }, withReferences: true));
            _checkOut.Click += (s, e) => RunOnCurrent("Check out", p => _commands.CheckOutAsync(new[] { p }));
            _checkIn.Click += (s, e) => RunOnCurrent("Check in", p => _commands.CheckInAsync(new[] { p }));
            _explorer.Click += (s, e) => ShowInExplorer(_current?.LocalPath);
            _references.NodeMouseDoubleClick += (s, e) => OpenPath(e.Node?.Tag as string);
            _whereUsed.DoubleClick += (s, e) => OpenPath(_whereUsed.SelectedItems.Count == 0 ? null : (string)_whereUsed.SelectedItems[0].Tag);
            _tabs.SelectedIndexChanged += (s, e) =>
            {
                if (_tabs.SelectedTab == _historyTab) Safe(LoadHistoryAsync);
                else if (_tabs.SelectedTab == _reviewsTab) Safe(LoadReviewsAsync);
            };
            _reviews.SelectedIndexChanged += (s, e) => ShowReview(_reviews.SelectedItems.Count == 0 ? null : _reviews.SelectedItems[0].Tag as ReviewDto);
            _requestReview.Click += (s, e) => RunOnCurrent("Request review", p => _agent.ShowAgentWindowAsync("requestReview", p));
            _respond.Click += (s, e) => UiThread.Run("Reviews", () => _agent.ShowAgentWindowAsync("reviews"));
            _reviewWeb.Click += (s, e) =>
            {
                var review = _reviews.SelectedItems.Count == 0 ? null : _reviews.SelectedItems[0].Tag as ReviewDto;
                if (!string.IsNullOrEmpty(review?.WebUrl)) Process.Start(new ProcessStartInfo(review.WebUrl) { UseShellExecute = true });
            };

            _history.OpenRequested += (v, asBuilt) => RunOnCurrent("Open version", p => _commands.GetVersionAsync(p, v.Version, asBuilt, open: true));
            _history.GetRequested += (v, asBuilt) => RunOnCurrent("Get version", p => _commands.GetVersionAsync(p, v.Version, asBuilt, open: false));
            _history.RollbackRequested += v => RunOnCurrent("Roll back", p => _commands.RollbackAsync(p, v.Version));

            // Keep the results current while the window is open; unhooked on close.
            _agent.StatusChanged += OnStatusChanged;
            _commands.Changed += OnCommandsChanged;
            FormClosed += (s, e) =>
            {
                _agent.StatusChanged -= OnStatusChanged;
                _commands.Changed -= OnCommandsChanged;
                _searchDelay.Dispose();
                _detailsDelay.Dispose();
                _refreshDelay.Dispose();
                _thumbnails.Dispose();
                if (_instance == this) _instance = null;
            };
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                if (_search.Text.Length > 0) _search.Clear();
                else Close();
                return true;
            }
            if (keyData == (Keys.Control | Keys.F) || keyData == (Keys.Control | Keys.L))
            {
                _search.Focus();
                _search.SelectAll();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void OnSearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down && _results.Items.Count > 0)
            {
                e.Handled = true;
                _results.Focus();
                if (_results.SelectedItems.Count == 0) Select(_results.Items[0]);
            }
            else if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                if (_searchDelay.Enabled) // still typing: search now, then open the best match
                {
                    _searchDelay.Stop();
                    Safe(async () => { await SearchAsync(); OpenSelected(); });
                }
                else OpenSelected();
            }
        }

        private void OnStatusChanged(StatusChangedNotification n)
        {
            if (n.VaultId == null || n.VaultId == CurrentVault?.Id) Restart(_refreshDelay);
        }

        private void OnCommandsChanged(IEnumerable<string> paths) => Restart(_refreshDelay);

        private static void Restart(Timer timer)
        {
            timer.Stop();
            timer.Start();
        }

        // ------------------------------------------------------------ helpers

        private VaultInfo CurrentVault => _vaults.SelectedIndex >= 0 && _vaults.SelectedIndex < _vaultList.Length ? _vaultList[_vaults.SelectedIndex] : null;

        private List<string> SelectedPaths => _results.SelectedItems.Cast<ListViewItem>().Select(i => ((FindResultDto)i.Tag).Status.LocalPath).ToList();

        private static void Select(ListViewItem item)
        {
            item.Selected = true;
            item.Focused = true;
            item.EnsureVisible();
        }

        /// <summary>Background work: failures show in the count label instead of a dialog.</summary>
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
                Log.Warn("File browser: " + ex.Message);
                if (!IsDisposed) _count.Text = ex.Message;
            }
        }

        private void RunOnSelection(string what, Func<IReadOnlyList<string>, Task> body)
        {
            var paths = SelectedPaths;
            if (paths.Count > 0) UiThread.Run(what, () => body(paths));
        }

        private void RunOnCurrent(string what, Func<string, Task> body)
        {
            var path = _current?.LocalPath;
            if (path != null) UiThread.Run(what, () => body(path));
        }

        private void OpenSelected()
        {
            var item = _results.SelectedItems.Count > 0 ? _results.SelectedItems[0] : _results.Items.Count > 0 ? _results.Items[0] : null;
            if (item != null) OpenPath(((FindResultDto)item.Tag).Status.LocalPath);
        }

        private void OpenPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            UiThread.Run("Open", () => _commands.OpenAsync(path)); // SOLIDWORKS for CAD files, Windows for the rest
        }

        private static void ShowInExplorer(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            Process.Start("explorer.exe", File.Exists(path) ? "/select,\"" + path + "\"" : "\"" + Path.GetDirectoryName(path) + "\"");
        }

        // ------------------------------------------------------------ search

        private async Task LoadVaultsAsync()
        {
            _vaultList = await _agent.GetVaultsAsync();
            await UiThread.OnUi();
            _vaults.Items.Clear();
            foreach (var v in _vaultList) _vaults.Items.Add(v.Name);
            _vaults.Visible = _vaultList.Length > 1;
            if (_vaultList.Length == 0)
            {
                _count.Text = "No vault connected yet.";
                return;
            }
            _vaults.SelectedIndex = 0; // triggers the first search
        }

        private async Task SearchAsync()
        {
            var vault = CurrentVault;
            if (vault == null) return;
            var generation = ++_searchGeneration;
            var query = _search.Text.Trim();
            _count.Text = "Searching...";
            var results = await _agent.FindAsync(vault.Id, query, Filters[Math.Max(0, _filter.SelectedIndex)].Filter, Types[Math.Max(0, _type.SelectedIndex)].Extensions, MaxResults);
            await UiThread.OnUi();
            if (generation != _searchGeneration || IsDisposed) return;

            _thumbnails.CancelPending();
            var selected = new HashSet<string>(SelectedPaths, StringComparer.OrdinalIgnoreCase);
            _results.BeginUpdate();
            _results.Items.Clear();
            foreach (var r in results) _results.Items.Add(ItemFor(r, selected.Contains(r.Status.LocalPath)));
            _results.EndUpdate();

            _count.Text = results.Length == 0 ? "No matches"
                : results.Length >= MaxResults ? "Best " + MaxResults + " matches"
                : results.Length + (results.Length == 1 ? " file" : " files");
            if (string.IsNullOrEmpty(query) && results.Length > 0) _count.Text += " (most recent first)";

            if (_results.SelectedItems.Count == 0 && _results.Items.Count > 0) Select(_results.Items[0]);
            else if (_results.SelectedItems.Count > 0) Restart(_detailsDelay); // refresh the details of the kept selection
            else ShowNothing();

            foreach (ListViewItem item in _results.Items) RequestThumbnail(item);
        }

        private ListViewItem ItemFor(FindResultDto r, bool selected)
        {
            var s = r.Status;
            var folder = Path.GetDirectoryName(s.Path.Replace('/', Path.DirectorySeparatorChar)) ?? "";
            string description = null;
            s.Properties?.TryGetValue("Description", out description);
            var match = r.MatchField != null ? r.MatchField + ": " + r.MatchText
                : !string.IsNullOrEmpty(description) ? description
                : s.Comment ?? "";
            return new ListViewItem(new[] { Path.GetFileName(s.Path), folder.Length == 0 ? "(vault root)" : folder, StatusLine(s), match.Replace("\n", " ") })
            {
                Tag = r,
                ImageKey = ImageKeyFor(s),
                Selected = selected,
                ToolTipText = s.Path + "\n" + VaultDialog.Describe(s) + (string.IsNullOrEmpty(s.Comment) ? "" : "\nLast note: " + s.Comment),
            };
        }

        private static string StatusLine(FileStatusDto s)
        {
            var parts = new List<string>();
            if (s.ServerVersion > 0) parts.Add("v" + s.ServerVersion);
            if (!string.IsNullOrEmpty(s.Revision)) parts.Add("Rev " + s.Revision);
            if (!string.IsNullOrEmpty(s.State)) parts.Add(s.State);
            parts.Add(VaultDialog.Describe(s));
            return string.Join("  ·  ", parts);
        }

        // ------------------------------------------------------------ thumbnails

        private static string ThumbKey(string localPath) => "t:" + localPath.ToLowerInvariant();

        private string ImageKeyFor(FileStatusDto s)
        {
            var key = ThumbKey(s.LocalPath);
            return _images.Images.ContainsKey(key) ? key : Placeholder(Path.GetExtension(s.Path));
        }

        private void RequestThumbnail(ListViewItem item)
        {
            var status = ((FindResultDto)item.Tag).Status;
            var key = ThumbKey(status.LocalPath);
            if (_images.Images.ContainsKey(key)) return;
            if (status.LocalState == LocalState.NotLocal || status.LocalState == LocalState.MissingLocally)
            {
                Safe(() => SavedThumbnailAsync(status, key));
                return;
            }
            _thumbnails.Get(status.LocalPath, image =>
            {
                if (IsDisposed) return;
                if (image == null)
                {
                    if (status.ServerVersion > 0) Safe(() => SavedThumbnailAsync(status, key));
                    return;
                }
                ShowThumbnail(key, image);
            });
        }

        /// <summary>The preview saved with the latest version at check-in (files not on this PC, or without a Windows thumbnail).</summary>
        private async Task SavedThumbnailAsync(FileStatusDto status, string key)
        {
            if (status.ServerVersion == 0) return;
            var image = await _agent.SavedThumbnailAsync(status.LocalPath);
            await UiThread.OnUi();
            if (image != null && !IsDisposed) ShowThumbnail(key, image);
        }

        private void ShowThumbnail(string key, Image image)
        {
            if (!_images.Images.ContainsKey(key)) _images.Images.Add(key, image);
            _fullSize[key] = image;
            foreach (ListViewItem i in _results.Items)
                if (ThumbKey(((FindResultDto)i.Tag).Status.LocalPath) == key) i.ImageKey = key;
            if (_current != null && ThumbKey(_current.LocalPath) == key) _preview.Image = image;
        }

        /// <summary>A drawn stand-in for files without a thumbnail (or not on this PC yet): the file type on a tinted card.</summary>
        private string Placeholder(string extension)
        {
            extension = (extension ?? "").ToLowerInvariant();
            var key = "p:" + extension;
            if (_images.Images.ContainsKey(key)) return key;
            string label;
            Color color;
            switch (extension)
            {
                case ".sldprt": label = "PART"; color = Color.FromArgb(0, 102, 204); break;
                case ".sldasm": label = "ASM"; color = Color.FromArgb(230, 145, 0); break;
                case ".slddrw": label = "DRW"; color = Color.FromArgb(46, 139, 87); break;
                default:
                    var format = CadFormats.Of("x" + extension);
                    label = format?.Label ?? extension.TrimStart('.').ToUpperInvariant();
                    switch (format?.Kind ?? CadKind.Other)
                    {
                        case CadKind.Neutral: color = Color.FromArgb(128, 64, 160); break;
                        case CadKind.Foreign: color = Color.FromArgb(160, 80, 60); break;
                        case CadKind.Mesh: color = Color.FromArgb(0, 140, 140); break;
                        case CadKind.Flat: color = Color.FromArgb(46, 139, 87); break;
                        default: color = Color.FromArgb(120, 120, 120); break;
                    }
                    break;
            }
            if (label.Length == 0) label = "FILE";
            var bitmap = new Bitmap(TileImageSize, TileImageSize, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            using (var fill = new SolidBrush(Color.FromArgb(30, color)))
            using (var pen = new Pen(color, 2))
            using (var font = new Font(Font.FontFamily, label.Length > 4 ? 13 : 17, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var text = new SolidBrush(color))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var card = new Rectangle(14, 10, TileImageSize - 28, TileImageSize - 20);
                g.FillRectangle(fill, card);
                g.DrawRectangle(pen, card);
                g.DrawString(label, font, text, card, format);
            }
            _images.Images.Add(key, bitmap);
            return key;
        }

        // ------------------------------------------------------------ details

        private void ShowNothing()
        {
            _current = null;
            _details.Visible = false;
            _empty.Visible = true;
        }

        private async Task ShowSelectedAsync()
        {
            if (_results.SelectedItems.Count == 0)
            {
                ShowNothing();
                return;
            }
            var result = (FindResultDto)_results.SelectedItems[0].Tag;
            var status = result.Status;
            var generation = ++_detailsGeneration;
            var samePath = _current != null && string.Equals(_current.LocalPath, status.LocalPath, StringComparison.OrdinalIgnoreCase);
            _current = status;
            if (!samePath)
            {
                _historyLoadedFor = null;
                _reviewsLoadedFor = null;
            }
            _empty.Visible = false;
            _details.Visible = true;

            // What the search result already carries, right away.
            _name.Text = Path.GetFileName(status.Path);
            _summary.Text = StatusLine(status) + "\n" + status.Path +
                            (string.IsNullOrEmpty(status.CheckedInBy) ? "" : "\nLast checked in by " + status.CheckedInBy + " on " + VersionTimeline.FormatDate(status.CheckedInAt));
            var key = ThumbKey(status.LocalPath);
            Image full;
            _preview.Image = _fullSize.TryGetValue(key, out full) ? full : _images.Images[Placeholder(Path.GetExtension(status.Path))];
            UpdateButtons(status);
            if (!samePath)
            {
                FillProperties(status, null, null);
                _references.Nodes.Clear();
                _whereUsed.Items.Clear();
            }

            // Then everything else the vault knows.
            var details = await _agent.DetailsAsync(status.LocalPath);
            await UiThread.OnUi();
            if (generation != _detailsGeneration || IsDisposed || details == null) return;
            _current = details.Status ?? status;
            UpdateButtons(_current);
            var subsystem = await SubsystemTextAsync(status.LocalPath);
            if (generation != _detailsGeneration || IsDisposed) return;
            FillProperties(_current, details, subsystem);
            FillWhereUsed(details.WhereUsed);

            if (details.References != null && details.References.Length > 0)
            {
                var tree = await _agent.ReferencesAsync(status.LocalPath);
                await UiThread.OnUi();
                if (generation != _detailsGeneration || IsDisposed) return;
                FillReferences(tree);
            }
            else
            {
                _references.Nodes.Clear();
                _references.Nodes.Add(new TreeNode(details.ExternalReferences?.Length > 0 ? "No references inside the vault." : "This file doesn't reference other files."));
            }

            if (_tabs.SelectedTab == _historyTab) await LoadHistoryAsync();
            else if (_tabs.SelectedTab == _reviewsTab) await LoadReviewsAsync();
        }

        private void UpdateButtons(FileStatusDto s)
        {
            var local = s.LocalState != LocalState.NotLocal && s.LocalState != LocalState.MissingLocally;
            _open.Text = CadFormats.OpensInSolidWorks(s.Path) ? (local ? "Open in SOLIDWORKS" : "Get && Open in SOLIDWORKS") : "Open";
            _getLatest.Enabled = s.ServerVersion > 0 && s.LocalState != LocalState.UpToDate;
            _checkOut.Enabled = s.ServerVersion > 0 && s.LockState == LockState.None;
            _checkIn.Enabled = s.LockState == LockState.MineHere || s.LocalState == LocalState.LocalOnly;
            _explorer.Enabled = local;
        }

        private async Task<string> SubsystemTextAsync(string path)
        {
            try
            {
                var s = await _agent.SubsystemForAsync(path);
                await UiThread.OnUi();
                if (s == null) return null;
                return s.Name + " (" + s.Car + ") - RE: " + (s.Engineers != null && s.Engineers.Length > 0 ? string.Join(", ", s.Engineers) : "none yet");
            }
            catch (AgentException)
            {
                return null; // no team service
            }
        }

        private void FillProperties(FileStatusDto s, FileDetailsDto details, string subsystem)
        {
            _properties.BeginUpdate();
            _properties.Items.Clear();
            _properties.Groups.Clear();
            var file = _properties.Groups.Add("file", "Vault");
            var custom = _properties.Groups.Add("custom", "Custom properties");
            var configurations = _properties.Groups.Add("configs", "Configurations");
            var revisions = _properties.Groups.Add("revs", "Revisions");
            var external = _properties.Groups.Add("external", "References outside the vault");

            void Add(ListViewGroup group, string name, string value)
            {
                if (!string.IsNullOrEmpty(value)) _properties.Items.Add(new ListViewItem(new[] { name, value.Replace("\n", " ") }, group) { ToolTipText = value });
            }

            Add(file, "Name", Path.GetFileName(s.Path));
            Add(file, "Folder", PathOrRoot(s.Path));
            Add(file, "Local path", s.LocalPath);
            Add(file, "Status", VaultDialog.Describe(s));
            Add(file, "Latest version", s.ServerVersion > 0 ? "v" + s.ServerVersion : "not checked in yet");
            Add(file, "Version on this PC", s.LocalVersion > 0 ? "v" + s.LocalVersion : null);
            Add(file, "Workflow state", s.State);
            Add(file, "Revision", s.Revision);
            Add(file, "Checked out by", s.LockState == LockState.None ? null : (s.LockOwner ?? "you") + (string.IsNullOrEmpty(s.LockedAt) ? "" : " since " + VersionTimeline.FormatDate(s.LockedAt)));
            Add(file, "Last checked in", string.IsNullOrEmpty(s.CheckedInBy) ? null : s.CheckedInBy + ", " + VersionTimeline.FormatDate(s.CheckedInAt));
            Add(file, "Last version note", s.Comment);
            Add(file, "Size", s.Size > 0 ? VersionTimeline.FormatSize(s.Size) : null);
            Add(file, "Subsystem", subsystem);
            if (!string.IsNullOrEmpty(details?.ApprovedBy))
            {
                Add(file, "Release approved by", details.ApprovedBy + ", " + VersionTimeline.FormatDate(details.ApprovedAt) +
                                                 (details.ApprovedVersion > 0 ? " (v" + details.ApprovedVersion + ")" : "") +
                                                 (string.IsNullOrEmpty(details.ApprovedWith) ? "" : " - " + details.ApprovedWith));
                Add(file, "Release note", details.ApprovalNote);
            }
            Add(file, "SOLIDWORKS version", details?.SwVersion);
            Add(file, "Exported from", details?.DerivedFrom);
            Add(file, "Content id", details?.Oid);

            foreach (var p in (s.Properties ?? new Dictionary<string, string>()).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)) Add(custom, p.Key, p.Value);
            foreach (var c in details?.Configurations ?? new string[0]) _properties.Items.Add(new ListViewItem(new[] { c, "" }, configurations));
            foreach (var r in (details?.Revisions ?? new RevisionDto[0]).Reverse())
                Add(revisions, "Rev " + r.Rev, "v" + r.Version + (string.IsNullOrEmpty(r.By) ? "" : " by " + r.By) + (string.IsNullOrEmpty(r.At) ? "" : ", " + VersionTimeline.FormatDate(r.At)));
            foreach (var x in details?.ExternalReferences ?? new string[0]) Add(external, Path.GetFileName(x), x);

            foreach (var group in new[] { custom, configurations, revisions, external })
                if (group.Items.Count == 0) _properties.Groups.Remove(group);
            _properties.EndUpdate();
        }

        private static string PathOrRoot(string vaultPath)
        {
            var i = vaultPath.LastIndexOf('/');
            return i <= 0 ? "(vault root)" : vaultPath.Substring(0, i);
        }

        private void FillReferences(ReferenceNodeDto root)
        {
            _references.BeginUpdate();
            _references.Nodes.Clear();
            foreach (var child in root?.Children ?? new ReferenceNodeDto[0]) _references.Nodes.Add(ReferenceNode(child));
            if (_references.Nodes.Count == 0) _references.Nodes.Add(new TreeNode("This file doesn't reference other files."));
            _references.ExpandAll();
            _references.EndUpdate();
        }

        private static TreeNode ReferenceNode(ReferenceNodeDto node)
        {
            var status = node.Status;
            var text = Path.GetFileName(node.LocalPath ?? node.Path) + "   v" + node.Version +
                       (status != null && status.ServerVersion > node.Version ? " (latest v" + status.ServerVersion + ")" : "") +
                       (status == null ? "" : "   " + VaultDialog.Describe(status));
            var tn = new TreeNode(text) { Tag = node.LocalPath, ToolTipText = node.Path };
            if (status != null && status.LockState == LockState.Other) tn.ForeColor = Color.FromArgb(200, 40, 40);
            foreach (var child in node.Children ?? new ReferenceNodeDto[0]) tn.Nodes.Add(ReferenceNode(child));
            return tn;
        }

        private void FillWhereUsed(string[] parents)
        {
            _whereUsed.BeginUpdate();
            _whereUsed.Items.Clear();
            foreach (var p in parents ?? new string[0])
                _whereUsed.Items.Add(new ListViewItem(new[] { Path.GetFileName(p), Path.GetDirectoryName(p) }) { Tag = p });
            if (_whereUsed.Items.Count == 0) _whereUsed.Items.Add(new ListViewItem(new[] { "Not used by any file in the vault.", "" }));
            _whereUsed.EndUpdate();
        }

        private async Task LoadReviewsAsync()
        {
            var status = _current;
            if (status == null || string.Equals(_reviewsLoadedFor, status.LocalPath, StringComparison.OrdinalIgnoreCase)) return;
            var generation = _detailsGeneration;
            _requestReview.Enabled = status.ServerVersion > 0;
            _reviews.Items.Clear();
            ShowReview(null);
            if (status.ServerVersion == 0)
            {
                _reviewText.Text = "Check the file in before asking for a review.";
                _reviewsLoadedFor = status.LocalPath;
                return;
            }
            _reviewText.Text = "Loading reviews...";
            ReviewDto[] reviews;
            try
            {
                reviews = await _agent.ReviewsForFileAsync(status.LocalPath);
                await UiThread.OnUi();
            }
            catch (AgentException ex)
            {
                if (generation == _detailsGeneration && !IsDisposed) _reviewText.Text = "Reviews aren't available: " + ex.Message;
                return;
            }
            if (generation != _detailsGeneration || IsDisposed) return;
            _reviewsLoadedFor = status.LocalPath;
            _reviews.BeginUpdate();
            foreach (var r in reviews ?? new ReviewDto[0])
            {
                var item = new ListViewItem(new[] { r.Kind, "v" + r.Version, r.Status, r.Lead, r.Requester, VersionTimeline.FormatDate(r.Updated) }) { Tag = r };
                item.ForeColor = r.Status == "Approved" ? Color.FromArgb(46, 139, 87)
                    : r.Status == "Changes requested" ? Color.FromArgb(200, 110, 0)
                    : r.Status == "Cancelled" ? SystemColors.GrayText
                    : SystemColors.WindowText;
                _reviews.Items.Add(item);
            }
            _reviews.EndUpdate();
            _reviewsTab.Text = reviews == null || reviews.Length == 0 ? "Reviews" : "Reviews (" + reviews.Length + ")";
            if (_reviews.Items.Count > 0) _reviews.Items[0].Selected = true;
            else _reviewText.Text = "No reviews of this file yet. Use Request Review to ask a subteam lead.";
        }

        private void ShowReview(ReviewDto r)
        {
            _reviewWeb.Enabled = !string.IsNullOrEmpty(r?.WebUrl);
            if (r == null)
            {
                _reviewText.Text = "";
                return;
            }
            var text = new System.Text.StringBuilder();
            text.Append(r.Kind).Append(" of version ").Append(r.Version).Append(" - ").Append(r.Status).Append("\r\n");
            text.Append("Requested by ").Append(r.Requester).Append(" on ").Append(VersionTimeline.FormatDate(r.Created))
                .Append("; reviewer ").Append(string.IsNullOrEmpty(r.Lead) ? "(none)" : r.Lead).Append("\r\n");
            if (r.Cc != null && r.Cc.Length > 0) text.Append("Copied: ").Append(string.Join(", ", r.Cc)).Append("\r\n");
            if (_current != null && _current.ServerVersion > r.Version) text.Append("The file has changed since (now v").Append(_current.ServerVersion).Append(").\r\n");
            if (!string.IsNullOrEmpty(r.Message)) text.Append("\r\n").Append(r.Message.Replace("\n", "\r\n")).Append("\r\n");
            foreach (var c in r.Comments ?? new ReviewCommentDto[0])
                text.Append("\r\n--- ").Append(c.Author).Append(", ").Append(VersionTimeline.FormatDate(c.At)).Append(" ---\r\n")
                    .Append((c.Text ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n")).Append("\r\n");
            _reviewText.Text = text.ToString();
        }

        private async Task LoadHistoryAsync()
        {
            var status = _current;
            if (status == null || string.Equals(_historyLoadedFor, status.LocalPath, StringComparison.OrdinalIgnoreCase)) return;
            var generation = _detailsGeneration;
            if (status.ServerVersion == 0)
            {
                _history.Clear("Not checked in yet.");
                _historyLoadedFor = status.LocalPath;
                return;
            }
            _history.Clear("Loading history...");
            var versions = await _agent.HistoryAsync(status.LocalPath);
            await UiThread.OnUi();
            if (generation != _detailsGeneration || IsDisposed) return;
            _history.SetHistory(versions, status, SwDocs.HasReferences(status.Path), SwDocs.IsSolidWorksFile(status.Path));
            _historyLoadedFor = status.LocalPath;
        }
    }
}
