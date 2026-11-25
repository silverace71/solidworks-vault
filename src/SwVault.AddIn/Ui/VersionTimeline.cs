using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using SwVault.AddIn.Infrastructure;
using SwVault.Protocol;

namespace SwVault.AddIn.Ui
{
    /// <summary>
    /// A file's history drawn as a vertical graph, newest at the top (like Onshape's version graph):
    /// a large node per version, small nodes for workflow changes that kept the content, a flag on
    /// releases, and markers for the latest version and the one on this PC.
    /// </summary>
    internal sealed class VersionTimeline : ScrollableControl
    {
        private const int Gutter = 40;
        private const int VersionRowHeight = 66;
        private const int EventRowHeight = 34;

        private static readonly Color Line = Color.FromArgb(200, 205, 212);
        private static readonly Color VersionColor = Color.FromArgb(0, 102, 204);
        private static readonly Color ReleasedColor = Color.FromArgb(46, 139, 87);
        private static readonly Color EventColor = Color.FromArgb(128, 64, 160);
        private static readonly Color SelectedBack = Color.FromArgb(225, 238, 252);
        private static readonly Color HoverBack = Color.FromArgb(244, 247, 251);

        private readonly List<Row> _rows = new List<Row>();
        private int _localVersion;
        private int _selected = -1;
        private int _hover = -1;

        /// <summary>One commit in the file's history.</summary>
        public sealed class Row
        {
            public VersionInfoDto Entry;

            /// <summary>True when the content changed; false for a state change on the same content.</summary>
            public bool IsVersion;

            /// <summary>The previous content version (for "what changed"), or null for the first one.</summary>
            public VersionInfoDto Previous;

            /// <summary>State before this commit, for state-change rows.</summary>
            public string PreviousState;

            public int Top;
            public int Height;
        }

        public VersionTimeline()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = SystemColors.Window;
            AutoScroll = true;
            TabStop = true;
        }

        public event Action<Row> SelectionChanged;

        /// <summary>Double-click or Enter on a row.</summary>
        public event Action<Row> RowActivated;

        public Row Selected => _selected >= 0 && _selected < _rows.Count ? _rows[_selected] : null;

        public IReadOnlyList<Row> Rows => _rows;

        /// <param name="history">Newest first, as file.history returns it.</param>
        /// <param name="localVersion">The version on this PC (0 = none).</param>
        public void SetHistory(IList<VersionInfoDto> history, int localVersion)
        {
            _rows.Clear();
            _localVersion = localVersion;
            _rows.AddRange(BuildRows(history ?? new VersionInfoDto[0]));
            var top = 6;
            foreach (var row in _rows)
            {
                row.Height = row.IsVersion ? VersionRowHeight : EventRowHeight;
                row.Top = top;
                top += row.Height;
            }
            AutoScrollMinSize = new Size(0, top + 6);
            _selected = _rows.Count > 0 ? 0 : -1;
            _hover = -1;
            AutoScrollPosition = Point.Empty;
            Invalidate();
            SelectionChanged?.Invoke(Selected);
        }

        /// <summary>Classifies commits: a new content version, or a workflow change that kept the content.</summary>
        internal static List<Row> BuildRows(IList<VersionInfoDto> history)
        {
            var rows = new List<Row>();
            for (var i = 0; i < history.Count; i++)
            {
                var entry = history[i];
                var older = i + 1 < history.Count ? history[i + 1] : null;
                var isVersion = entry.IsNewVersion || older == null || older.Version != entry.Version || older.Oid != entry.Oid;
                VersionInfoDto previous = null;
                for (var j = i + 1; j < history.Count; j++)
                    if (history[j].Version != entry.Version)
                    {
                        previous = history[j];
                        break;
                    }
                rows.Add(new Row { Entry = entry, IsVersion = isVersion, Previous = previous, PreviousState = older?.State });
            }
            return rows;
        }

        public static string FormatDate(string iso)
        {
            DateTime at;
            return DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out at)
                ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
                : iso ?? "";
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#", CultureInfo.CurrentCulture) + " KB";
            return (bytes / (1024.0 * 1024)).ToString("0.#", CultureInfo.CurrentCulture) + " MB";
        }

        private static bool IsReleased(VersionInfoDto v) => string.Equals(v.State, "Released", StringComparison.OrdinalIgnoreCase);

        // ------------------------------------------------------------ painting

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            if (_rows.Count == 0)
            {
                TextRenderer.DrawText(g, "No history yet. The file's versions appear here once it is checked in.", Font, new Rectangle(8, 8, Width - 16, 40), SystemColors.GrayText, TextFormatFlags.WordBreak);
                return;
            }
            g.TranslateTransform(0, AutoScrollPosition.Y);
            var width = ClientSize.Width;
            var cx = Gutter / 2;

            // The spine, from the first node to the last.
            using (var pen = new Pen(Line, 2))
                g.DrawLine(pen, cx, _rows[0].Top + NodeY(_rows[0]), cx, _rows[_rows.Count - 1].Top + NodeY(_rows[_rows.Count - 1]));

            using (var bold = new Font(Font, FontStyle.Bold))
            using (var small = new Font(Font.FontFamily, Font.Size - 0.5f))
            {
                for (var i = 0; i < _rows.Count; i++)
                {
                    var row = _rows[i];
                    var bounds = new Rectangle(0, row.Top, width, row.Height);
                    if (!e.ClipRectangle.IntersectsWith(new Rectangle(bounds.X, bounds.Y + AutoScrollPosition.Y, bounds.Width, bounds.Height))) continue;
                    if (i == _selected) using (var b = new SolidBrush(SelectedBack)) g.FillRectangle(b, Rectangle.Inflate(bounds, 0, -1));
                    else if (i == _hover) using (var b = new SolidBrush(HoverBack)) g.FillRectangle(b, Rectangle.Inflate(bounds, 0, -1));
                    DrawNode(g, row, cx);
                    if (row.IsVersion) DrawVersion(g, row, i == 0, bounds, bold, small);
                    else DrawEvent(g, row, bounds, small);
                }
            }
        }

        private static int NodeY(Row row) => row.IsVersion ? 18 : row.Height / 2;

        private void DrawNode(Graphics g, Row row, int cx)
        {
            var cy = row.Top + NodeY(row);
            if (row.IsVersion)
            {
                var color = IsReleased(row.Entry) ? ReleasedColor : VersionColor;
                using (var fill = new SolidBrush(color)) g.FillEllipse(fill, cx - 8, cy - 8, 16, 16);
                using (var ring = new Pen(Color.White, 2)) g.DrawEllipse(ring, cx - 6, cy - 6, 12, 12);
                if (row.Entry.Version == _localVersion)
                    using (var you = new Pen(color, 2)) g.DrawEllipse(you, cx - 12, cy - 12, 24, 24);
            }
            else
            {
                var color = IsReleased(row.Entry) ? ReleasedColor : EventColor;
                using (var back = new SolidBrush(BackColor)) g.FillEllipse(back, cx - 5, cy - 5, 10, 10);
                using (var pen = new Pen(color, 2)) g.DrawEllipse(pen, cx - 5, cy - 5, 10, 10);
            }
        }

        private void DrawVersion(Graphics g, Row row, bool isLatest, Rectangle bounds, Font bold, Font small)
        {
            var v = row.Entry;
            var x = Gutter;
            var y = bounds.Top + 8;
            var title = "Version " + v.Version;
            TextRenderer.DrawText(g, title, bold, new Point(x, y), ForeColor, TextFormatFlags.NoPadding);
            x += TextRenderer.MeasureText(g, title, bold, Size.Empty, TextFormatFlags.NoPadding).Width + 8;

            if (!string.IsNullOrEmpty(v.Revision)) x = DrawBadge(g, "Rev " + v.Revision, Color.FromArgb(90, 90, 160), x, y, small) + 4;
            if (!string.IsNullOrEmpty(v.State)) x = DrawBadge(g, (IsReleased(v) ? "⚑ " : "") + v.State, IsReleased(v) ? ReleasedColor : Color.FromArgb(120, 120, 120), x, y, small) + 4;
            if (isLatest) x = DrawBadge(g, "Latest", VersionColor, x, y, small) + 4;
            if (v.Version == _localVersion) DrawBadge(g, "On this PC", Color.FromArgb(230, 145, 0), x, y, small);

            var textWidth = Math.Max(40, bounds.Width - Gutter - 8);
            var comment = string.IsNullOrWhiteSpace(v.Comment) ? "(no comment)" : v.Comment.Replace("\r", " ").Replace("\n", " ");
            TextRenderer.DrawText(g, comment, Font, new Rectangle(Gutter, y + 20, textWidth, 18),
                string.IsNullOrWhiteSpace(v.Comment) ? SystemColors.GrayText : ForeColor, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            var meta = (v.By ?? "?") + "  ·  " + FormatDate(v.At) + "  ·  " + FormatSize(v.Size) +
                       (string.IsNullOrEmpty(v.ApprovedBy) ? "" : "  ·  release approved by " + v.ApprovedBy);
            TextRenderer.DrawText(g, meta, small, new Rectangle(Gutter, y + 38, textWidth, 16), SystemColors.GrayText, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private void DrawEvent(Graphics g, Row row, Rectangle bounds, Font small)
        {
            var v = row.Entry;
            var change = string.IsNullOrEmpty(row.PreviousState) || row.PreviousState == v.State ? (v.State ?? "Updated") : row.PreviousState + " → " + v.State;
            if (!string.IsNullOrEmpty(v.Revision)) change += "  (Rev " + v.Revision + ")";
            var who = string.IsNullOrEmpty(v.ApprovedBy) ? (v.By ?? "?") : "approved by " + v.ApprovedBy;
            var text = change + "  ·  " + who + "  ·  " + FormatDate(v.At) + (string.IsNullOrWhiteSpace(v.Comment) ? "" : "  ·  " + v.Comment.Replace("\n", " "));
            TextRenderer.DrawText(g, text, small, new Rectangle(Gutter, bounds.Top, Math.Max(40, bounds.Width - Gutter - 8), bounds.Height),
                ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private static int DrawBadge(Graphics g, string text, Color color, int x, int y, Font font)
        {
            var size = TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding);
            var rect = new Rectangle(x, y, size.Width + 10, size.Height + 2);
            using (var path = RoundedRect(rect, 7))
            using (var fill = new SolidBrush(Color.FromArgb(28, color)))
            using (var pen = new Pen(color))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, text, font, new Point(x + 5, y + 1), color, TextFormatFlags.NoPadding);
            return rect.Right;
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // ------------------------------------------------------------ input

        private int RowAt(int y)
        {
            y -= AutoScrollPosition.Y;
            for (var i = 0; i < _rows.Count; i++)
                if (y >= _rows[i].Top && y < _rows[i].Top + _rows[i].Height) return i;
            return -1;
        }

        private void Select(int index)
        {
            if (index < 0 || index >= _rows.Count || index == _selected) return;
            _selected = index;
            var row = _rows[index];
            // Keep the selected row in view.
            var top = row.Top + AutoScrollPosition.Y;
            if (top < 0) AutoScrollPosition = new Point(0, row.Top);
            else if (top + row.Height > ClientSize.Height) AutoScrollPosition = new Point(0, row.Top + row.Height - ClientSize.Height);
            Invalidate();
            SelectionChanged?.Invoke(row);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            Select(RowAt(e.Y));
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            var index = RowAt(e.Y);
            if (index >= 0) RowActivated?.Invoke(_rows[index]);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var index = RowAt(e.Y);
            if (index == _hover) return;
            _hover = index;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = -1;
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData) => keyData == Keys.Up || keyData == Keys.Down || keyData == Keys.Enter || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Down) Select(Math.Min(_rows.Count - 1, _selected + 1));
            else if (e.KeyCode == Keys.Up) Select(Math.Max(0, _selected - 1));
            else if (e.KeyCode == Keys.Enter && Selected != null) RowActivated?.Invoke(Selected);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Invalidate();
        }

        protected override void OnScroll(ScrollEventArgs se)
        {
            base.OnScroll(se);
            Invalidate();
        }
    }

    /// <summary>What changed between two versions of a file, from their sidecars.</summary>
    internal static class VersionDiff
    {
        public static List<string> Describe(VersionInfoDto current, VersionInfoDto previous)
        {
            var changes = new List<string>();
            if (previous == null)
            {
                changes.Add("First version in the vault.");
                return changes;
            }
            if (current.Oid != previous.Oid)
            {
                var delta = current.Size - previous.Size;
                changes.Add("Content changed (" + VersionTimeline.FormatSize(previous.Size) + " → " + VersionTimeline.FormatSize(current.Size) +
                            (delta == 0 ? "" : ", " + (delta > 0 ? "+" : "-") + VersionTimeline.FormatSize(Math.Abs(delta))) + ")");
            }
            if (!string.Equals(current.Revision, previous.Revision, StringComparison.Ordinal))
                changes.Add("Revision: " + (previous.Revision ?? "none") + " → " + (current.Revision ?? "none"));
            if (!string.Equals(current.State, previous.State, StringComparison.Ordinal))
                changes.Add("State: " + (previous.State ?? "none") + " → " + (current.State ?? "none"));

            var before = previous.Properties ?? new Dictionary<string, string>();
            var after = current.Properties ?? new Dictionary<string, string>();
            foreach (var key in before.Keys.Union(after.Keys).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                string a, b;
                var had = before.TryGetValue(key, out a);
                var has = after.TryGetValue(key, out b);
                if (had && !has) changes.Add("Property removed: " + key + " (was \"" + a + "\")");
                else if (!had) changes.Add("Property added: " + key + " = \"" + b + "\"");
                else if (a != b) changes.Add("Property " + key + ": \"" + a + "\" → \"" + b + "\"");
            }

            var oldRefs = (previous.References ?? new ReferenceDto[0]).ToDictionary(r => r.Path, r => r.Version, StringComparer.OrdinalIgnoreCase);
            var newRefs = (current.References ?? new ReferenceDto[0]).ToDictionary(r => r.Path, r => r.Version, StringComparer.OrdinalIgnoreCase);
            foreach (var r in newRefs.Where(r => !oldRefs.ContainsKey(r.Key))) changes.Add("Reference added: " + System.IO.Path.GetFileName(r.Key) + " v" + r.Value);
            foreach (var r in oldRefs.Where(r => !newRefs.ContainsKey(r.Key))) changes.Add("Reference removed: " + System.IO.Path.GetFileName(r.Key));
            foreach (var r in newRefs.Where(r => oldRefs.ContainsKey(r.Key) && oldRefs[r.Key] != r.Value))
                changes.Add("Reference updated: " + System.IO.Path.GetFileName(r.Key) + " v" + oldRefs[r.Key] + " → v" + r.Value);

            var oldConfigs = previous.Configurations ?? new string[0];
            var newConfigs = current.Configurations ?? new string[0];
            foreach (var c in newConfigs.Except(oldConfigs)) changes.Add("Configuration added: " + c);
            foreach (var c in oldConfigs.Except(newConfigs)) changes.Add("Configuration removed: " + c);

            if (changes.Count == 0) changes.Add("No recorded changes besides the comment.");
            return changes;
        }
    }

    /// <summary>
    /// The timeline with a details panel for the selected version: who, when, comment, what changed
    /// since the version before, and actions (open it, get it, roll back to it).
    /// </summary>
    internal sealed class VersionHistoryView : UserControl
    {
        private readonly VersionTimeline _timeline = new VersionTimeline { Dock = DockStyle.Fill };
        private readonly Label _title = new Label { Dock = DockStyle.Top, Height = 24, Padding = new Padding(4, 4, 4, 0) };
        private readonly Label _meta = new Label { Dock = DockStyle.Top, Height = 40, Padding = new Padding(4, 0, 4, 0), ForeColor = SystemColors.GrayText };
        private readonly TextBox _comment = new TextBox { Dock = DockStyle.Top, Height = 48, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.Vertical };
        private readonly PictureBox _preview = new PictureBox { Dock = DockStyle.Right, Width = 112, SizeMode = PictureBoxSizeMode.Zoom, BackColor = SystemColors.Window };
        private int _previewRequest;
        private readonly ListBox _changes = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None, HorizontalScrollbar = true };
        private readonly FlowLayoutPanel _actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(2) };
        private readonly Button _open = new Button { Text = "Open This Version", AutoSize = true };
        private readonly Button _get = new Button { Text = "Get This Version", AutoSize = true };
        private readonly Button _rollback = new Button { Text = "Roll Back To This", AutoSize = true };
        private readonly CheckBox _asBuilt = new CheckBox { Text = "With references as built", AutoSize = true, Checked = true, Visible = false };
        private readonly ToolTip _tips = new ToolTip();
        private bool _checkedOutByMe;

        public VersionHistoryView(bool vertical)
        {
            Font = SystemFonts.MessageBoxFont;
            _comment.BackColor = SystemColors.Window;
            var details = new Panel { Dock = DockStyle.Fill, BackColor = SystemColors.Window, Padding = new Padding(4) };
            var changesLabel = new Label { Text = "Changes from the version before:", Dock = DockStyle.Top, Height = 22, Padding = new Padding(4, 6, 4, 0), Font = new Font(Font, FontStyle.Bold) };
            details.Controls.Add(_changes);
            details.Controls.Add(changesLabel);
            details.Controls.Add(_comment);
            var heading = new Panel { Dock = DockStyle.Top, Height = 112 };
            var headingText = new Panel { Dock = DockStyle.Fill };
            _meta.Dock = DockStyle.Fill;
            headingText.Controls.Add(_meta);
            headingText.Controls.Add(_title);
            heading.Controls.Add(headingText);
            heading.Controls.Add(_preview);
            details.Controls.Add(heading);
            _changes.BringToFront();
            _title.Font = new Font(Font.FontFamily, Font.Size + 2, FontStyle.Bold);

            _actions.Controls.Add(_open);
            _actions.Controls.Add(_get);
            _actions.Controls.Add(_rollback);
            _actions.Controls.Add(_asBuilt);
            details.Controls.Add(_actions);
            _tips.SetToolTip(_open, "Put this version in your vault folder (read-only) and open it in SOLIDWORKS");
            _tips.SetToolTip(_get, "Put this version in your vault folder (read-only). Get Latest returns to the newest");
            _tips.SetToolTip(_rollback, "Replace your checked-out copy with this version; check in to make it the newest");

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = vertical ? Orientation.Horizontal : Orientation.Vertical, FixedPanel = FixedPanel.None };
            split.Panel1.Controls.Add(_timeline);
            split.Panel2.Controls.Add(details);
            Controls.Add(split);
            split.HandleCreated += (s, e) => { try { split.SplitterDistance = vertical ? split.Height * 55 / 100 : split.Width * 55 / 100; } catch (InvalidOperationException) { } };

            _timeline.SelectionChanged += ShowRow;
            _timeline.RowActivated += row => { if (row != null && _open.Enabled) OpenRequested?.Invoke(row.Entry, AsBuilt); };
            _open.Click += (s, e) => { if (Selected != null) OpenRequested?.Invoke(Selected, AsBuilt); };
            _get.Click += (s, e) => { if (Selected != null) GetRequested?.Invoke(Selected, AsBuilt); };
            _rollback.Click += (s, e) => { if (Selected != null) RollbackRequested?.Invoke(Selected); };
            ShowRow(null);
        }

        public event Action<VersionInfoDto, bool> OpenRequested;
        public event Action<VersionInfoDto, bool> GetRequested;
        public event Action<VersionInfoDto> RollbackRequested;

        /// <summary>Loads the preview saved with a version (null when it has none). Optional.</summary>
        public Func<VersionInfoDto, Task<Image>> PreviewProvider { get; set; }

        public VersionInfoDto Selected => _timeline.Selected?.Entry;
        public bool AsBuilt => _asBuilt.Visible && _asBuilt.Checked;

        /// <summary>Shows a file's history. <paramref name="canOpen"/>: the file is a SOLIDWORKS document.</summary>
        public void SetHistory(VersionInfoDto[] history, FileStatusDto status, bool hasReferences, bool canOpen)
        {
            _checkedOutByMe = status?.LockState == LockState.MineHere;
            _asBuilt.Visible = hasReferences;
            _open.Visible = canOpen;
            _timeline.SetHistory(history, status?.LocalVersion ?? 0);
        }

        public void Clear(string message)
        {
            _timeline.SetHistory(new VersionInfoDto[0], 0);
            _title.Text = message ?? "";
        }

        private async void LoadPreview(VersionInfoDto v)
        {
            var request = ++_previewRequest;
            if (PreviewProvider == null) return;
            try
            {
                var image = await PreviewProvider(v);
                await UiThread.OnUi();
                if (request == _previewRequest && !IsDisposed) _preview.Image = image;
            }
            catch (Exception ex)
            {
                Infrastructure.Log.Warn("Version preview: " + ex.Message); // the preview is a nicety; the history still works
            }
        }

        private void ShowRow(VersionTimeline.Row row)
        {
            var hasRow = row != null;
            _open.Enabled = hasRow && !_checkedOutByMe;
            _get.Enabled = hasRow && !_checkedOutByMe;
            _rollback.Enabled = hasRow && _checkedOutByMe;
            _tips.SetToolTip(_rollback, _checkedOutByMe ? "Replace your checked-out copy with this version; check in to make it the newest" : "Check the file out first to roll back.");
            _changes.Items.Clear();
            _preview.Image = null;
            if (!hasRow)
            {
                _title.Text = "";
                _meta.Text = "";
                _comment.Text = "";
                return;
            }
            var v = row.Entry;
            _title.Text = "Version " + v.Version + (string.IsNullOrEmpty(v.Revision) ? "" : "  ·  Rev " + v.Revision) + (string.IsNullOrEmpty(v.State) ? "" : "  ·  " + v.State);
            _meta.Text = (row.IsVersion ? "Checked in by " : "Changed by ") + (v.By ?? "?") + " on " + VersionTimeline.FormatDate(v.At) + "\n" +
                         (string.IsNullOrEmpty(v.ApprovedBy) ? "" : "Release approved by " + v.ApprovedBy + (string.IsNullOrEmpty(v.ApprovalNote) ? "" : ": " + v.ApprovalNote) + "\n") +
                         VersionTimeline.FormatSize(v.Size) + "  ·  commit " + (v.Commit?.Length > 8 ? v.Commit.Substring(0, 8) : v.Commit);
            LoadPreview(v);
            _comment.Text = string.IsNullOrWhiteSpace(v.Comment) ? "(no comment)" : v.Comment;
            var changes = row.IsVersion
                ? VersionDiff.Describe(v, row.Previous)
                : new List<string> { "Workflow change only; the content is the same as version " + v.Version + "." , "State: " + (row.PreviousState ?? "none") + " → " + (v.State ?? "none") };
            foreach (var change in changes) _changes.Items.Add(change);
        }
    }
}
