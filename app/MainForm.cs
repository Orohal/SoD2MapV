using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SoD2MapViewer;

public sealed class MainForm : Form
{
    private readonly MapLoader _loader = new();
    private static readonly string DefaultGameDir = @"D:\Games\State of Decay 2\StateOfDecay2";

    private readonly TreeView _mapTree;
    private readonly DataGridView _grid;
    private readonly MapCanvas _canvas;
    private readonly TextBox _search;
    private readonly TextBox _status;
    private readonly Panel _legendHost;

    private MapModel? _map;
    private readonly BindingSource _source = new();
    private List<MapObject> _visible = new();
    private readonly TextBox _mapFilter;
    private readonly CheckBox _chkEdit;
    private CheckBox _chkFandom = null!;
    private CheckBox _chkBg = null!;
    private CheckBox _chkEditBg = null!;
    private readonly Stack<UndoEntry> _undo = new();

    private sealed class UndoEntry
    {
        public int Idx;
        public float OldX, OldY;
        public float OldQX, OldQY, OldQZ, OldQW;
        public float OldSX, OldSY, OldSZ;
        public bool OldDeleted;
    }

    public MainForm()
    {
        Text = $"State of Decay 2 — Map Viewer (v{typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "?"})";
        Size = new Size(1400, 900);
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;
        KeyPreview = true;
        KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.F && ActiveControl is not TextBox)
            {
                _canvas.Fit();
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.Z && ActiveControl is not TextBox)
            {
                UndoLastMove();
                e.Handled = true;
            }
            else if (ActiveControl is not TextBox)
            {
                if (e.KeyCode == Keys.M) { _canvas.Mode = MapCanvas.EditMode.Move; e.Handled = true; }
                else if (e.KeyCode == Keys.R) { _canvas.Mode = MapCanvas.EditMode.Rotate; e.Handled = true; }
                else if (e.KeyCode == Keys.S) { _canvas.Mode = MapCanvas.EditMode.Scale; e.Handled = true; }
                else if (e.KeyCode == Keys.Delete) { _canvas.DeleteSelected(); e.Handled = true; }
            }
        };

        // left: map filter + map list (grouped by region, collapsible) + legend
        var left = new Panel { Dock = DockStyle.Fill };
        _mapTree = new TreeView
        {
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9f),
            BackColor = Color.FromArgb(30, 32, 38),
            ForeColor = Color.Gainsboro,
            LineColor = Color.FromArgb(90, 95, 110),
            BorderStyle = BorderStyle.None,
            HideSelection = false,
            ShowPlusMinus = true,
            ShowRootLines = true,
            FullRowSelect = true,
        };
        _mapTree.AfterSelect += MapTreeSelected;
        _legendHost = new Panel { Dock = DockStyle.Bottom, Height = 180, BackColor = Color.FromArgb(32, 34, 40) };
        _mapFilter = new TextBox
        {
            Dock = DockStyle.Top,
            PlaceholderText = "filter maps (name contains...)",
        };
        _mapFilter.TextChanged += (s, e) => ApplyMapFilter();

        left.Controls.Add(_mapTree);
        left.Controls.Add(_legendHost);
        left.Controls.Add(_mapFilter);

        // right: grid (top) + canvas (bottom)
        var right = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            IsSplitterFixed = false,
        };
        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            Font = new Font("Consolas", 9f),
            BorderStyle = BorderStyle.None,
            BackgroundColor = Color.FromArgb(30, 32, 38),
            DefaultCellStyle = { ForeColor = Color.Gainsboro, BackColor = Color.FromArgb(30, 32, 38), SelectionForeColor = Color.Black, SelectionBackColor = Color.FromArgb(255, 200, 120) },
            ColumnHeadersDefaultCellStyle = { ForeColor = Color.Gainsboro, BackColor = Color.FromArgb(38, 40, 48) },
            EnableHeadersVisualStyles = false,
        };
        _grid.SelectionChanged += GridSelection;
        // X/Y/Z are stored in UE centimeters; display them in meters (like canvas/tooltip/copy)
        _grid.CellFormatting += (s, e) =>
        {
            if (e.ColumnIndex < 0 || e.RowIndex < 0) return;
            var prop = _grid.Columns[e.ColumnIndex].DataPropertyName;
            if (prop is "X" or "Y" or "Z" && e.Value != null)
            {
                try { e.Value = Convert.ToSingle(e.Value) / 100f; } catch { }
            }
        };
        _canvas = new MapCanvas { Dock = DockStyle.Fill };
        _canvas.SelectionChanged += CanvasSelection;
        _canvas.ObjectMoved += CanvasObjectMoved;
        _canvas.ObjectRotated += CanvasObjectRotated;
        _canvas.ObjectScaled += CanvasObjectScaled;
        _canvas.ObjectDeleted += CanvasObjectDeleted;
        right.Panel1.Controls.Add(_grid);
        right.Panel2.Controls.Add(_canvas);

        // outer splitter: left panel (maps) | right area (grid + canvas)
        var outer = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            IsSplitterFixed = false,
        };
        outer.Panel1.Controls.Add(left);
        outer.Panel2.Controls.Add(right);

        // top toolbar (responsive flow layout, no absolute positioning -> no overlaps)
        var top = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Color.FromArgb(38, 40, 48) };
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = false,
            Padding = new Padding(8, 8, 8, 8),
            BackColor = Color.Transparent,
        };
        var btnOpen = new Button { Text = "Choose game folder...", AutoSize = true, Height = 30, Margin = new Padding(0, 0, 12, 0) };
        btnOpen.Click += (s, e) => PickGameDir();
        var labelMap = new Label { AutoSize = true, Text = "Object filter:", ForeColor = Color.Gainsboro, Margin = new Padding(0, 8, 4, 0) };
        _search = new TextBox { Width = 320, PlaceholderText = "name/class/mesh contains...", Height = 30, Margin = new Padding(0, 0, 12, 0) };
        _search.TextChanged += (s, e) => ApplyFilter();
        _chkEdit = new CheckBox
        {
            Text = "Enable editing (drag objects)",
            ForeColor = Color.Gainsboro,
            Height = 30,
            Margin = new Padding(0, 0, 12, 0),
        };
        _chkEdit.CheckedChanged += (s, e) => _canvas.EditingEnabled = _chkEdit.Checked;
        var btnExport = new Button { Text = "Export mod .pak...", AutoSize = true, Height = 30 };
        btnExport.Click += (s, e) => ExportModPak();
        var btnInject = new Button { Text = "Inject vehicle...", AutoSize = true, Height = 30 };
        btnInject.Click += (s, e) => OpenInjectDialog(null);
        _chkFandom = new CheckBox { Text = "Fandom map (offline)", AutoSize = true, Checked = true, ForeColor = Color.Gainsboro, Padding = new Padding(0, 8, 0, 0) };
        _chkFandom.CheckedChanged += (s, e) => { _canvas.ShowFandomBase = _chkFandom.Checked; _canvas.Invalidate(); };
        _chkBg = new CheckBox { Text = "Vector map (asset)", AutoSize = true, ForeColor = Color.Gainsboro, Padding = new Padding(0, 8, 0, 0) };
        _chkBg.CheckedChanged += (s, e) => { _canvas.ShowBgBase = _chkBg.Checked; _canvas.Invalidate(); };
        _chkEditBg = new CheckBox { Text = "Edit base-map pos", AutoSize = true, ForeColor = Color.Gainsboro, Padding = new Padding(0, 8, 0, 0) };
        _chkEditBg.CheckedChanged += (s, e) => { _canvas.EditBgOffset = _chkEditBg.Checked; _canvas.Invalidate(); };
        flow.Controls.AddRange(new Control[] { btnOpen, labelMap, _search, _chkEdit, btnExport, btnInject, _chkFandom, _chkBg, _chkEditBg });
        top.Controls.Add(flow);
        _canvas.WorldRightClicked += w => OpenInjectDialog(w);

        _status = new TextBox
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(38, 40, 48),
            ForeColor = Color.LightGray,
        };

        Controls.Add(outer);
        Controls.Add(_status);
        Controls.Add(top);

        // Set splitter distances once the form's actual size is known
        Load += (s, e) =>
        {
            // inner: grid vs canvas height
            right.Panel1MinSize = 200;
            right.Panel2MinSize = 300;
            var innerAvail = right.Height - right.SplitterWidth;
            var innerMin = right.Panel1MinSize;
            var innerMax = innerAvail - right.Panel2MinSize;
            if (innerMax < innerMin) innerMax = innerMin;
            right.SplitterDistance = Math.Clamp(420, innerMin, innerMax);

            // outer: left panel width
            outer.Panel1MinSize = 250;
            outer.Panel2MinSize = 400;
            var outerAvail = outer.Width - outer.SplitterWidth;
            var omin = outer.Panel1MinSize;
            var omax = outerAvail - outer.Panel2MinSize;
            if (omax < omin) omax = omin;
            outer.SplitterDistance = Math.Clamp(360, omin, omax);
        };

        BeginInitGame();
    }

    private void BeginInitGame()
    {
        var dir = _loader.GameDirectory.Length > 0 ? _loader.GameDirectory : DefaultGameDir;
        SetStatus($"Loading paks from {dir} ...");
        var progress = new Progress<string>(s => SetStatus(s));
        Task<MapLoader.InitResult> task;
        try
        {
            task = _loader.InitializeAsync(dir, progress);
        }
        catch (Exception e)
        {
            MessageBox.Show(this, e.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        task.ContinueWith(t => OnInitialized(t.Result), TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnInitialized(MapLoader.InitResult res)
    {
        if (res.Error != null)
        {
            MessageBox.Show(this, res.Error, "Load error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("failed to mount paks.");
            return;
        }
        SetStatus($"mounted paks: {res.MountedPaks}, files: {res.TotalFiles:N0}, maps found: {res.MapCount}");
        ApplyMapFilter();

        if (res.NeedsKey)
        {
            var info = "Encrypted pak files were detected (no AES key available):\n" +
                       string.Join(Environment.NewLine, res.RequiredGuids) +
                       "\n\nPaste the AES key as hex (64 hex digits). Choose Skip for unencrypted installs.";
            using var dlg = new KeyDialog(info);
            if (dlg.ShowDialog(this) == DialogResult.OK && dlg.KeyHex is { Length: 64 })
            {
                foreach (var guid in res.RequiredGuids)
                    _loader.SubmitAesKey(guid, dlg.KeyHex);
            }
        }
    }

    private void ApplyMapFilter()
    {
        var f = _mapFilter.Text?.Trim() ?? "";
        _mapTree.BeginUpdate();
        try
        {
            _mapTree.Nodes.Clear();

            var filtered = _loader.MapPaths
                .Where(m => f.Length == 0 || m.Contains(f, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Group by region (known regions first, in MapRegions.All order, then "Other")
            var groups = new Dictionary<string, (string label, List<string> maps)>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in MapRegions.All)
                groups[r.Key] = (r.Display, new List<string>());
            const string otherKey = "__other__";
            groups[otherKey] = ("Other", new List<string>());

            foreach (var m in filtered)
            {
                var region = MapRegions.FromMapPath(m);
                var key = region?.Key ?? otherKey;
                if (!groups.TryGetValue(key, out var g))
                    g = groups[otherKey];
                g.maps.Add(m);
            }

            int shown = 0;
            // Add known regions in order, then Other
            foreach (var r in MapRegions.All)
            {
                if (!groups.TryGetValue(r.Key, out var g) || g.maps.Count == 0) continue;
                var node = new TreeNode($"{g.label} ({g.maps.Count})")
                {
                    ForeColor = Color.FromArgb(255, 220, 150),
                    NodeFont = new Font(_mapTree.Font, FontStyle.Bold),
                };
                foreach (var m in g.maps)
                {
                    node.Nodes.Add(new TreeNode(m) { Tag = m });
                    shown++;
                }
                _mapTree.Nodes.Add(node);
                if (f.Length > 0) node.Expand();
            }
            if (groups.TryGetValue(otherKey, out var other) && other.maps.Count > 0)
            {
                var node = new TreeNode($"{other.label} ({other.maps.Count})")
                {
                    ForeColor = Color.FromArgb(180, 180, 180),
                    NodeFont = new Font(_mapTree.Font, FontStyle.Bold),
                };
                foreach (var m in other.maps)
                {
                    node.Nodes.Add(new TreeNode(m) { Tag = m });
                    shown++;
                }
                _mapTree.Nodes.Add(node);
                if (f.Length > 0) node.Expand();
            }

            SetStatus($"maps shown: {shown:N0} / {_loader.MapPaths.Count:N0}");
        }
        finally
        {
            _mapTree.EndUpdate();
        }
    }

    private void PickGameDir()
    {
        using var dlg = new FolderBrowserDialog
        {
            SelectedPath = _loader.GameDirectory.Length > 0 ? _loader.GameDirectory : DefaultGameDir,
            Description = "Select the folder that contains the .pak files (Content\\Paks) or the game root.",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _loader.GameDirectory = dlg.SelectedPath;
            BeginInitGame();
        }
    }

    private async void MapTreeSelected(object? sender, TreeViewEventArgs e)
    {
        if (e.Node?.Tag is not string path) return; // group nodes have no Tag
        SetStatus($"parsing {Path.GetFileName(path)} ...");
        var model = await _loader.LoadMapAsync(path);
        SetMap(model);
    }

    private void SetMap(MapModel model)
    {
        _map = model;
        _undo.Clear();
        // tiled maps store local object coords; the Wand tiles are in world coords → shift tiles
        _canvas.MapWorldOffsetX = model.HasWorldTileOffset ? model.WorldTileOffsetX : 0;
        _canvas.MapWorldOffsetY = model.HasWorldTileOffset ? model.WorldTileOffsetY : 0;
        // select the matching Wand background region from the map path (folder codename)
        _canvas.MapRegion = MapRegions.FromMapPath(model.Path);
        // vector basemap renders from the game paks — provider available after open
        _canvas.BgSource = _loader.Provider;
        var bgRegion = _canvas.MapRegion ?? MapRegions.TrumbullValley;
        var ok = model.Error == null;
        var bgMap = BgMaps.Get(_loader.Provider, bgRegion, 1800);
        if (bgMap != null)
            SetStatus($"map: {Path.GetFileName(model.Path)}  |  exports: {model.TotalExports:N0}  objects: {model.Objects.Count:N0}  " +
                $"without position: {Math.Max(0, model.FailedExports):N0}  parse time: {model.LoadMs} ms  " +
                $"|  Vector-Region: {bgRegion.Key}  WorldRect X {bgMap.WorldX0:0}..{bgMap.WorldX1:0} Y {bgMap.WorldY0:0}..{bgMap.WorldY1:0}" +
                (ok ? "" : $"  |  ERROR: {model.Error}") +
                (ok && !model.HasWorldTileOffset && model.Path.Contains("maps/", StringComparison.OrdinalIgnoreCase)
                    ? "  |  kein Kachel-Offset (kein WorldTileInfo) — Wand-Hintergrund evtl. versetzt, Kachel-UMAP laden"
                    : ""));
        ApplyFilter();
        RenderLegend();
    }

    private void RenderLegend()
    {
        _legendHost.Controls.Clear();
        if (_map == null) return;
        var groups = _map.Objects
            .GroupBy(o => ClassCategory.From(o.ClassName))
            .Select(w => (cat: w.Key, count: w.Count()))
            .OrderByDescending(t => t.count)
            .ToList();
        if (groups.Count == 0)
        {
            var none = new Label { Left = 10, Top = 8, AutoSize = true, ForeColor = Color.Gray, Text = "no objects" };
            _legendHost.Controls.Add(none);
            return;
        }
        int y = 8, x = 10, col = 0;
        foreach (var (cat, count) in groups)
        {
            var sw = new Panel { Left = x, Top = y + 2, Width = 13, Height = 13, BackColor = LegendColor(cat) };
            var lbl = new Label
            {
                Left = x + 20, Top = y, AutoSize = true,
                Text = $"{cat} ({count:N0})",
                ForeColor = Color.FromArgb(200, 210, 220),
            };
            _legendHost.Controls.Add(sw);
            _legendHost.Controls.Add(lbl);
            y += 24;
            if (++col % 6 == 0) { y = 8; x += 150; }
        }
    }

    private static Color LegendColor(string cat) => MapCanvas.GetPaletteColor(cat);

    private void ApplyFilter()
    {
        if (_map == null) return;
        var filter = _search.Text?.Trim() ?? "";
        IEnumerable<MapObject> q = _map.Objects;
        if (filter.Length > 0)
        {
            q = q.Where(o =>
                o.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                o.ClassName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                o.ComponentClass.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                o.MeshPath.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }
        _visible = q.ToList();

        var binding = new BindingList<MapObject>(_visible) { AllowNew = false };
        _source.DataSource = binding;
        if (_grid.DataSource == null)
        {
            _grid.DataSource = _source;
        }
        else
        {
            // force refresh keeping old columns
            _grid.DataSource = null;
            _grid.DataSource = _source;
        }
        BalanceColumns();
        _canvas.SetObjects(_visible);
    }

    private void BalanceColumns()
    {
        if (_grid.Columns["Name"] is DataGridViewTextBoxColumn cName)
        {
            cName.Frozen = true;
            cName.Width = 200;
        }
        if (_grid.Columns["ClassName"] is DataGridViewTextBoxColumn cc)
            cc.Width = 180;
        if (_grid.Columns["MeshPath"] is DataGridViewTextBoxColumn cm)
            cm.Width = 320;
        foreach (string col in new[] { "X", "Y", "Z", "SX", "SY", "SZ", "QX", "QY", "QZ", "QW" })
            if (_grid.Columns[col] != null) _grid.Columns[col]!.DefaultCellStyle.Format = "0.##";
        foreach (string col in new[] { "X", "Y", "Z" })
            if (_grid.Columns[col] is DataGridViewTextBoxColumn cm2) cm2.HeaderText = col + " (m)";
    }

    private void CanvasObjectMoved(int idx, float oldX, float oldY)
    {
        if (idx < 0 || idx >= _visible.Count) return;
        var o = _visible[idx];
        _undo.Push(new UndoEntry { Idx = idx, OldX = o.X, OldY = o.Y,
            OldQX = o.QX, OldQY = o.QY, OldQZ = o.QZ, OldQW = o.QW,
            OldSX = o.SX, OldSY = o.SY, OldSZ = o.SZ,
            OldDeleted = o.Deleted });
        o.Edited = true;
        _source.ResetItem(idx);
        SetStatus($"moved {o.Name}: ({oldX / 100:0.##}, {oldY / 100:0.##}) m   |   undo: {_undo.Count}  (Ctrl+Z)");
    }

    private void CanvasObjectRotated(int idx, float oldQX, float oldQY, float oldQZ, float oldQW)
    {
        if (idx < 0 || idx >= _visible.Count) return;
        var o = _visible[idx];
        _undo.Push(new UndoEntry { Idx = idx, OldX = o.X, OldY = o.Y,
            OldQX = oldQX, OldQY = oldQY, OldQZ = oldQZ, OldQW = oldQW,
            OldSX = o.SX, OldSY = o.SY, OldSZ = o.SZ,
            OldDeleted = o.Deleted });
        o.EditedRotation = true;
        _source.ResetItem(idx);
        var yaw = YawDeg(o.QX, o.QY, o.QZ, o.QW);
        var oldYaw = YawDeg(oldQX, oldQY, oldQZ, oldQW);
        SetStatus($"rotated {o.Name}: {oldYaw:0.#}° -> {yaw:0.#}°   |   undo: {_undo.Count}  (Ctrl+Z)");
    }

    private void CanvasObjectScaled(int idx, float oldSX, float oldSY, float oldSZ)
    {
        if (idx < 0 || idx >= _visible.Count) return;
        var o = _visible[idx];
        _undo.Push(new UndoEntry { Idx = idx, OldX = o.X, OldY = o.Y,
            OldQX = o.QX, OldQY = o.QY, OldQZ = o.QZ, OldQW = o.QW,
            OldSX = oldSX, OldSY = oldSY, OldSZ = oldSZ,
            OldDeleted = o.Deleted });
        o.EditedScale = true;
        _source.ResetItem(idx);
        SetStatus($"scaled {o.Name}: ({oldSX:0.##},{oldSY:0.##},{oldSZ:0.##}) -> ({o.SX:0.##},{o.SY:0.##},{o.SZ:0.##})   |   undo: {_undo.Count}  (Ctrl+Z)");
    }

    private void CanvasObjectDeleted(int idx)
    {
        if (idx < 0 || idx >= _visible.Count) return;
        var o = _visible[idx];
        _undo.Push(new UndoEntry { Idx = idx, OldX = o.X, OldY = o.Y,
            OldQX = o.QX, OldQY = o.QY, OldQZ = o.QZ, OldQW = o.QW,
            OldSX = o.SX, OldSY = o.SY, OldSZ = o.SZ,
            OldDeleted = o.Deleted });
        o.Deleted = true;
        _source.ResetItem(idx);
        SetStatus($"deleted {o.Name} (component data will be zeroed on export)   |   undo: {_undo.Count}  (Ctrl+Z)");
    }

    private static float YawDeg(float qx, float qy, float qz, float qw)
    {
        float yaw = MathF.Atan2(2f * (qw * qy + qx * qz), 1f - 2f * (qy * qy + qz * qz));
        return yaw * 180f / MathF.PI;
    }

    private void OpenInjectDialog(System.Drawing.PointF? worldCm) =>
        new InjectDialog(worldCm).ShowDialog(this);

    private void ExportModPak()    {
        if (_map == null || _map.Error != null)
        {
            MessageBox.Show(this, "Load a map first.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var edited = _map.Objects.Where(o => o.Edited || o.EditedRotation || o.EditedScale || o.Deleted).ToList();
        if (edited.Count == 0)
        {
            MessageBox.Show(this, "No objects were moved, rotated, scaled, or deleted. Enable editing, modify an object, then export.",
                "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        byte[] data;
        try
        {
            data = _loader.ReadMapBytes(_map.Path);
        }
        catch (Exception e)
        {
            MessageBox.Show(this, "Failed to read map bytes:\n" + e.Message, "Export", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        foreach (var o in edited)
        {
            if (o.Deleted)
            {
                var r = UmapPatcher.PatchErase(data, o);
                if (r.Error.Length > 0)
                {
                    MessageBox.Show(this, $"Failed to erase {o.Name}:\n{r.Error}", "Export", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                continue;
            }
            if (o.Edited)
            {
                var r = UmapPatcher.PatchLocation(data, o, o.X, o.Y, o.Z);
                if (r.Error.Length > 0)
                {
                    MessageBox.Show(this, $"Failed to patch location {o.Name}:\n{r.Error}", "Export", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }
            if (o.EditedRotation)
            {
                var r = UmapPatcher.PatchRotation(data, o, o.QX, o.QY, o.QZ, o.QW);
                if (r.Error.Length > 0)
                {
                    MessageBox.Show(this, $"Failed to patch rotation {o.Name}:\n{r.Error}", "Export", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }
            if (o.EditedScale)
            {
                var r = UmapPatcher.PatchScale(data, o, o.SX, o.SY, o.SZ);
                if (r.Error.Length > 0)
                {
                    MessageBox.Show(this, $"Failed to patch scale {o.Name}:\n{r.Error}", "Export", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }
        }

        string mountPoint, rel;
        try
        {
            var pe = _loader.Provider[_map.Path] as CUE4Parse.UE4.Pak.Objects.FPakEntry
                ?? throw new InvalidOperationException("map is not inside a pak");
            var mp = ((CUE4Parse.UE4.Pak.PakFileReader)pe.Vfs).MountPoint;
            mountPoint = mp.StartsWith("../") ? mp : "../../../" + mp;
            rel = StripMount(_map.Path, mp);
        }
        catch (Exception e)
        {
            MessageBox.Show(this, "Failed to resolve pak mount point:\n" + e.Message, "Export", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var mapName = Path.GetFileNameWithoutExtension(_map.Path);
        using var dlg = new SaveFileDialog
        {
            Title = "Save mod pak",
            Filter = "PAK archive (*.pak)|*.pak",
            FileName = $"mod_{mapName}_P.pak",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            PakWriter.Write(dlg.FileName, mountPoint, new[] { new PakWriter.Entry { Path = rel, Data = data } });

            // reset edit flags so re-export doesn't re-patch the same objects
            foreach (var o in _map.Objects)
                o.Edited = o.EditedRotation = o.EditedScale = o.Deleted = false;

            SetStatus($"exported {edited.Count:N0} object(s) -> {dlg.FileName}");
            MessageBox.Show(this,
                $"Mod pak written:\n{dlg.FileName}\n\n" +
                $"Objects modified: {edited.Count:N0}\n\n" +
                "To use it in the game, copy the file into the game's Paks folder:\n" +
                "StateOfDecay2\\Content\\Paks\\\n\n" +
                "The '_P' suffix makes the game prefer this pak over the original map.",
                "Export complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception e)
        {
            MessageBox.Show(this, "Failed to write pak:\n" + e.Message, "Export", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
    }

    private static string StripMount(string fullPath, string mountPoint)
    {
        var m = mountPoint.Replace('\\', '/');
        while (m.StartsWith("../")) m = m[3..];
        var fp = fullPath.Replace('\\', '/');
        if (m.Length > 0 && fp.StartsWith(m, StringComparison.OrdinalIgnoreCase))
            return fp[m.Length..];
        var idx = fp.IndexOf("Content/", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? fp[(idx + "Content/".Length)..] : fp;
    }

    private void UndoLastMove()
    {
        if (_undo.Count == 0)
        {
            SetStatus("nothing to undo");
            return;
        }
        var entry = _undo.Pop();
        if (entry.Idx < 0 || entry.Idx >= _visible.Count) return;
        var o = _visible[entry.Idx];
        o.X = entry.OldX;
        o.Y = entry.OldY;
        o.QX = entry.OldQX;
        o.QY = entry.OldQY;
        o.QZ = entry.OldQZ;
        o.QW = entry.OldQW;
        o.SX = entry.OldSX;
        o.SY = entry.OldSY;
        o.SZ = entry.OldSZ;
        o.Deleted = entry.OldDeleted;
        o.Edited = o.EditedRotation = o.EditedScale = false;
        _source.ResetItem(entry.Idx);
        _canvas.Invalidate();
        SetStatus($"undo: {o.Name} back to pos=({entry.OldX / 100:0.##},{entry.OldY / 100:0.##}) rot=({entry.OldQX:0.##},{entry.OldQY:0.##},{entry.OldQZ:0.##},{entry.OldQW:0.##}) scale=({entry.OldSX:0.##},{entry.OldSY:0.##},{entry.OldSZ:0.##})   |   undo: {_undo.Count}");
    }

    private void CanvasSelection(int idx)
    {
        if (idx >= 0 && idx < _grid.Rows.Count)
        {
            _grid.ClearSelection();
            _grid.Rows[idx].Selected = true;
            _grid.FirstDisplayedScrollingRowIndex = Math.Max(0, idx - 8);
            var nameCol = _grid.Columns["X"] ?? _grid.Columns[0];
            try { _grid.CurrentCell = _grid.Rows[idx].Cells[nameCol!.Index]; } catch { }
        }
        else
        {
            _grid.ClearSelection();
        }
    }

    private void GridSelection(object? sender, EventArgs e)
    {
        var row = _grid.CurrentRow;
        if (row != null && row.Index < _visible.Count && _canvas.Objects.Count == _visible.Count)
        {
            _canvas.SelectIndex(row.Index, centerOnSelection: false);
        }
    }

    private void SetStatus(string text) => _status.Text = text;
}

internal sealed class KeyDialog : Form
{
    public string? KeyHex;
    public KeyDialog(string info)
    {
        Text = "Encrypted pak — AES key needed";
        Size = new Size(620, 280);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        var lbl = new Label { Left = 12, Top = 12, Width = 580, Height = 150, Text = info };
        var box = new TextBox { Left = 12, Top = 170, Width = 580, Font = new Font("Consolas", 11f) };
        var ok = new Button { Text = "OK", Left = 404, Top = 210, Width = 80, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Skip", Left = 494, Top = 210, Width = 80, DialogResult = DialogResult.Cancel };
        ok.Click += (s, e) => KeyHex = box.Text.Trim();
        Controls.AddRange(new Control[] { lbl, box, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
