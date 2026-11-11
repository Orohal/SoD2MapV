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

    private readonly TreeView _mapTree = null!;
    private readonly DataGridView _grid = null!;
    private readonly MapCanvas _canvas = null!;
    private readonly TextBox _search = null!;
    private readonly TextBox _status = null!;
    private readonly Panel _legendHost = null!;

    private MapModel? _map;
    private readonly BindingSource _source = new();
    private List<MapObject> _visible = new();
    private readonly TextBox _mapFilter = null!;
    private readonly CheckBox _chkEdit = null!;
    private CheckBox _chkFandom = null!;
    private CheckBox _chkBg = null!;
    private CheckBox _chkEditBg = null!;
    private readonly ComboBox _cmbHouses = null!;
    private bool _houseComboUpdating;
    private readonly Stack<UndoEntry> _undo = new();
    private SessionStateData _session = SessionState.Load();
    private List<string>? _pendingRestoreSelectionNames;
    private string? _pendingRestorePrimaryName;

    private sealed class HouseItem
    {
        public MapObject? Obj;
        public bool Fallback;     // listed via generic prefab fallback (not house/home-named)
        public string? Label;
        public override string ToString()
        {
            if (Obj == null) return Label ?? "(house: none — click or right-click the map)";
            return (Fallback ? "[prefab] " : "") + $"{Obj.Name}  ({Obj.X / 100:0.#}, {Obj.Y / 100:0.#} m)";
        }
    }

    private sealed class UndoItem
    {
        public int Idx;
        public float OldX, OldY, OldZ;
        public float OldQX, OldQY, OldQZ, OldQW;
        public float OldSX, OldSY, OldSZ;
        public bool OldDeleted;
    }

    private sealed class UndoEntry
    {
        public List<UndoItem> Items = new();
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
                else if (e.KeyCode == Keys.Delete) { _canvas.DeleteSelection(); e.Handled = true; }
                else if (e.KeyCode == Keys.Escape) { _canvas.ClearSelection(); e.Handled = true; }
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
        _canvas.ObjectsMovedTogether += CanvasObjectsMovedTogether;
        _canvas.ObjectRotated += CanvasObjectRotated;
        _canvas.ObjectScaled += CanvasObjectScaled;
        _canvas.ObjectDeleted += CanvasObjectDeleted;
        _canvas.ObjectsDeleted += CanvasObjectsDeleted;
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
        var labelHouse = new Label { AutoSize = true, Text = "House:", ForeColor = Color.Gainsboro, Margin = new Padding(0, 8, 4, 0) };
        _cmbHouses = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            DropDownWidth = 420,
            Width = 300,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 32, 38),
            ForeColor = Color.Gainsboro,
            Margin = new Padding(0, 4, 12, 0),
        };
        _cmbHouses.SelectedIndexChanged += (s, e) =>
        {
            if (_houseComboUpdating) return;
            if (_cmbHouses.SelectedItem is HouseItem { Obj: { } h })
            {
                var idx = _visible.IndexOf(h);
                if (idx < 0) { SetStatus("house not in current view — clear the object filter"); return; }
                _canvas.SelectHouseGroup(idx);
                SetStatus($"house group selected: {h.Name} — {_canvas.Selection.Count} objects within {_canvas.HouseSelectRadiusCm / 100:0} m. " +
                          "Drag in Move mode to relocate, then 'Export mod .pak' to let the house appear in-game at the new spot.");
            }
        };
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
        var btnAdjustZ = new Button { Text = "Adjust height (Z)…", AutoSize = true, Height = 30 };
        btnAdjustZ.Click += (s, e) => AdjustSelectionHeight();
        var btnChar = new Button { Text = "Show character", AutoSize = true, Height = 30 };
        btnChar.Click += (s, e) => ShowCharacterPosition();

        var injectMenu = new ContextMenuStrip();
        injectMenu.Items.Add("Inject vehicle...", null, (s, e) => OpenInjectDialog(null));
        injectMenu.Items.Add("Inject item...", null, (s, e) => OpenItemInjectDialog());
        injectMenu.Items.Add("Inject at player", null, (s, e) =>
        {
            if (_canvas.PlayerWorldPos is { } p)
            {
                // 10m "in front" (default: +X); yaw unknown from save. Adjust in dialog if needed.
                var ahead = new System.Drawing.PointF(p.X + 1000f, p.Y);
                OpenInjectDialog(ahead, _canvas.PlayerWorldZCm, 0f,
                    "Hinweis: Beim Injizieren am Spieler am besten auf freier Fläche stehen (sonst spawnt das Fahrzeug evtl. in einem Objekt). Position/Yaw/Höhe ggf. anpassen.");
                SetStatus("Inject: Spieler-Pos + 10m (X). Tipp: auf freier Fläche stehen. Position/Yaw/Höhe ggf. anpassen.");
            }
            else
                SetStatus("No player position known — click 'Show character' first.");
        });
        var btnInjectMenu = new Button { Text = "Inject ▾", AutoSize = true, Height = 30 };
        btnInjectMenu.Click += (s, e) => injectMenu.Show(btnInjectMenu, new Point(0, btnInjectMenu.Height));
        _chkFandom = new CheckBox { Text = "Fandom map (offline)", AutoSize = true, Checked = false, ForeColor = Color.Gainsboro, Padding = new Padding(0, 8, 0, 0) };
        _chkFandom.CheckedChanged += (s, e) => { _canvas.ShowFandomBase = _chkFandom.Checked; _canvas.Invalidate(); };
        _chkBg = new CheckBox { Text = "Vector map (asset)", AutoSize = true, Checked = true, ForeColor = Color.Gainsboro, Padding = new Padding(0, 8, 0, 0) };
        _chkBg.CheckedChanged += (s, e) => { _canvas.ShowBgBase = _chkBg.Checked; _canvas.Invalidate(); };
        _canvas.ShowBgBase = true;
        _chkEditBg = new CheckBox { Text = "Edit base-map pos", AutoSize = true, ForeColor = Color.Gainsboro, Padding = new Padding(0, 8, 0, 0) };
        _chkEditBg.CheckedChanged += (s, e) => { _canvas.EditBgOffset = _chkEditBg.Checked; _canvas.Invalidate(); };
        flow.Controls.AddRange(new Control[] { btnOpen, labelMap, _search, labelHouse, _cmbHouses, _chkEdit, btnExport, btnAdjustZ, btnInjectMenu, btnChar, _chkFandom, _chkBg, _chkEditBg });
        top.Controls.Add(flow);
        _canvas.WorldRightClicked += w => OpenInjectDialog(w);
        _canvas.SelectionChanged += _ => SaveSessionStateLight();
        FormClosing += (s, e) => SaveSessionState();

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

        // restore session (filters + game dir) before first init
        if (!string.IsNullOrWhiteSpace(_session.LastObjectFilter))
            _search.Text = _session.LastObjectFilter;
        if (!string.IsNullOrWhiteSpace(_session.LastMapFilter))
            _mapFilter.Text = _session.LastMapFilter;
        if (!string.IsNullOrWhiteSpace(_session.LastGameDir) && System.IO.Directory.Exists(_session.LastGameDir))
            _loader.GameDirectory = _session.LastGameDir!;

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
        RestoreLastMapSelection();

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

    private void SaveSessionStateLight()
    {
        try
        {
            _session.LastGameDir = _loader.GameDirectory;
            _session.LastObjectFilter = _search?.Text;
            _session.LastMapFilter = _mapFilter?.Text;
            if (_map != null) _session.LastMapPath = _map.Path;
            var sel = _canvas?.Selection;
            if (sel != null)
            {
                var names = new List<string>();
                foreach (var idx in sel)
                {
                    if (idx < 0 || idx >= _visible.Count) continue;
                    names.Add(_visible[idx].Name);
                }
                _session.SelectedNames = names;
                _session.PrimarySelectedName = names.Count > 0 ? names[0] : null;
            }
            SessionState.Save(_session);
        }
        catch { }
    }

    private void SaveSessionState() => SaveSessionStateLight();

    private void RestoreLastMapSelection()
    {
        var path = _session.LastMapPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        var node = FindNodeByTag(_mapTree.Nodes, path);
        if (node == null) return;
        _pendingRestoreSelectionNames = _session.SelectedNames != null ? new List<string>(_session.SelectedNames) : null;
        _pendingRestorePrimaryName = _session.PrimarySelectedName;
        node.Parent?.Expand();
        _mapTree.SelectedNode = node;
        node.EnsureVisible();
    }

    private static TreeNode? FindNodeByTag(TreeNodeCollection nodes, string tag)
    {
        foreach (TreeNode n in nodes)
        {
            if (n.Tag is string s && string.Equals(s, tag, StringComparison.OrdinalIgnoreCase))
                return n;
            var child = FindNodeByTag(n.Nodes, tag);
            if (child != null) return child;
        }
        return null;
    }

    private void RestorePendingSelection(string mapPath)
    {
        if (_pendingRestoreSelectionNames == null) return;
        if (!string.Equals(_session.LastMapPath, mapPath, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(_pendingRestoreSelectionNames.Count > 0 ? mapPath : mapPath, mapPath, StringComparison.Ordinal)) { }
        var names = _pendingRestoreSelectionNames;
        var primary = _pendingRestorePrimaryName;
        _pendingRestoreSelectionNames = null;
        _pendingRestorePrimaryName = null;
        if (names.Count == 0 && string.IsNullOrEmpty(primary)) return;

        // try to select primary; if multiple, select group via multi-select logic
        int primaryIdx = -1;
        var idxs = new List<int>();
        for (int i = 0; i < _visible.Count; i++)
        {
            if (names.Contains(_visible[i].Name, StringComparer.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(primary) && string.Equals(_visible[i].Name, primary, StringComparison.OrdinalIgnoreCase)))
            {
                idxs.Add(i);
            }
        }
        if (idxs.Count == 0) return;
        if (!string.IsNullOrEmpty(primary))
            primaryIdx = idxs.FirstOrDefault(i => string.Equals(_visible[i].Name, primary, StringComparison.OrdinalIgnoreCase));
        if (primaryIdx < 0) primaryIdx = idxs[0];

        // select primary and toggle others into multi-selection
        _canvas.ClearSelection();
        _canvas.SelectIndex(primaryIdx, centerOnSelection: true);
        foreach (var i in idxs)
        {
            if (i == primaryIdx) continue;
            _canvas.ToggleSelected(i);
        }
        _canvas.Invalidate();
        SetStatus($"restored selection: {idxs.Count} object(s)");
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
        _canvas.MapPath = model.Path;
        // vector basemap renders from the game paks — provider available after open
        _canvas.BgSource = _loader.Provider;
        var bgRegion = _canvas.MapRegion ?? MapRegions.TrumbullValley;
        var ok = model.Error == null;
        var bgMap = BgMaps.Get(_loader.Provider, bgRegion, 1800, model.Path);
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
        RestorePendingSelection(model.Path);
        SaveSessionStateLight();
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

        // house identification: list all house prefabs of the current view for quick selection
        _houseComboUpdating = true;
        _cmbHouses.Items.Clear();
        var houses = _visible.Where(ClassCategory.IsHousePrefab).ToList();
        var fallback = false;
        if (houses.Count == 0)
        {
            // map without house/home-prefixed names: offer all building prefabs instead
            houses = _visible.Where(ClassCategory.IsPrefabBuilding).ToList();
            fallback = houses.Count > 0;
        }
        if (houses.Count == 0)
            _cmbHouses.Items.Add(new HouseItem { Obj = null, Label = "(no prefabs in this view — check object filter / map)" });
        else
        {
            _cmbHouses.Items.Add(new HouseItem { Obj = null, Label = $"(select a house — {houses.Count} found)" });
            foreach (var o in houses)
                _cmbHouses.Items.Add(new HouseItem { Obj = o, Fallback = fallback });
        }
        _cmbHouses.SelectedIndex = 0;
        _houseComboUpdating = false;

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

    private UndoItem Snapshot(int idx)
    {
        var o = _visible[idx];
        return new UndoItem
        {
            Idx = idx,
            OldX = o.X, OldY = o.Y, OldZ = o.Z,
            OldQX = o.QX, OldQY = o.QY, OldQZ = o.QZ, OldQW = o.QW,
            OldSX = o.SX, OldSY = o.SY, OldSZ = o.SZ,
            OldDeleted = o.Deleted,
        };
    }

    private static UndoEntry Entry(params UndoItem[] items)
    {
        var e = new UndoEntry();
        e.Items.AddRange(items);
        return e;
    }

    private void CanvasObjectMoved(int idx, float oldX, float oldY)
    {
        if (idx < 0 || idx >= _visible.Count) return;
        var item = Snapshot(idx);
        item.OldX = oldX;
        item.OldY = oldY;
        // Z unchanged by 2D drag
        item.OldZ = _visible[idx].Z;
        var o = _visible[idx];
        _undo.Push(Entry(item));
        o.Edited = true;
        _source.ResetItem(idx);
        SetStatus($"moved {o.Name}: ({oldX / 100:0.##}, {oldY / 100:0.##}) m   |   undo: {_undo.Count}  (Ctrl+Z)");
    }

    private void CanvasObjectsMovedTogether(IReadOnlyList<(int Idx, float OldX, float OldY)> moves)
    {
        if (moves.Count == 0) return;
        var items = new List<UndoItem>();
        foreach (var m in moves)
        {
            if (m.Idx < 0 || m.Idx >= _visible.Count) continue;
            var item = Snapshot(m.Idx);
            item.OldX = m.OldX;
            item.OldY = m.OldY;
            item.OldZ = _visible[m.Idx].Z;
            items.Add(item);
            _visible[m.Idx].Edited = true;
        }
        _undo.Push(new UndoEntry { Items = items });
        SetStatus($"moved {items.Count} objects together (house group)   |   undo: {_undo.Count}  (Ctrl+Z)");
        foreach (var m in moves)
            _source.ResetItem(m.Idx);
    }

    private void CanvasObjectRotated(int idx, float oldQX, float oldQY, float oldQZ, float oldQW)
    {
        if (idx < 0 || idx >= _visible.Count) return;
        var item = Snapshot(idx);
        item.OldQX = oldQX; item.OldQY = oldQY; item.OldQZ = oldQZ; item.OldQW = oldQW;
        var o = _visible[idx];
        _undo.Push(Entry(item));
        o.EditedRotation = true;
        _source.ResetItem(idx);
        var yaw = YawDeg(o.QX, o.QY, o.QZ, o.QW);
        var oldYaw = YawDeg(oldQX, oldQY, oldQZ, oldQW);
        SetStatus($"rotated {o.Name}: {oldYaw:0.#}° -> {yaw:0.#}°   |   undo: {_undo.Count}  (Ctrl+Z)");
    }

    private void CanvasObjectScaled(int idx, float oldSX, float oldSY, float oldSZ)
    {
        if (idx < 0 || idx >= _visible.Count) return;
        var item = Snapshot(idx);
        item.OldSX = oldSX; item.OldSY = oldSY; item.OldSZ = oldSZ;
        var o = _visible[idx];
        _undo.Push(Entry(item));
        o.EditedScale = true;
        _source.ResetItem(idx);
        SetStatus($"scaled {o.Name}: ({oldSX:0.##},{oldSY:0.##},{oldSZ:0.##}) -> ({o.SX:0.##},{o.SY:0.##},{o.SZ:0.##})   |   undo: {_undo.Count}  (Ctrl+Z)");
    }

    private void CanvasObjectDeleted(int idx)
    {
        if (idx < 0 || idx >= _visible.Count) return;
        var o = _visible[idx];
        // snapshot BEFORE marking deleted (canvas now delegates flag setting to us)
        var item = Snapshot(idx);
        _undo.Push(Entry(item));
        o.Deleted = true;
        _source.ResetItem(idx);
        SetStatus($"deleted {o.Name} (component data will be zeroed on export)   |   undo: {_undo.Count}  (Ctrl+Z)");
    }

    private void CanvasObjectsDeleted(IReadOnlyList<int> idxs)
    {
        if (idxs.Count == 0) return;
        var items = new List<UndoItem>();
        foreach (var idx in idxs)
        {
            if (idx < 0 || idx >= _visible.Count) continue;
            var o = _visible[idx];
            if (o.Deleted) continue;
            items.Add(Snapshot(idx));
            o.Deleted = true;
        }
        if (items.Count == 0) return;
        _undo.Push(new UndoEntry { Items = items });
        foreach (var idx in idxs)
            _source.ResetItem(idx);
        _canvas.Invalidate();
        SetStatus($"deleted {items.Count} objects (selection)   |   undo: {_undo.Count}  (Ctrl+Z)");
    }

    private static float YawDeg(float qx, float qy, float qz, float qw)
    {
        float yaw = MathF.Atan2(2f * (qw * qy + qx * qz), 1f - 2f * (qy * qy + qz * qz));
        return yaw * 180f / MathF.PI;
    }

    private void OpenInjectDialog(System.Drawing.PointF? worldCm, float? zCm = null, float yawDeg = 0f, string? hint = null) =>
        new InjectDialog(worldCm, zCm, yawDeg, hint).ShowDialog(this);

    private void OpenItemInjectDialog() =>
        new ItemInjectDialog().ShowDialog(this);

    private void ShowCharacterPosition()
    {
        try
        {
            var saves = Savegame.FindSavegames();
            string? savPath = null;
            if (saves.Length == 1)
            {
                savPath = saves[0].FullName;
            }
            else
            {
                using var dlg = new OpenFileDialog
                {
                    Title = "Select savegame (.sav) — shows last player breadcrumb",
                    Filter = "Savegame (*.sav)|*.sav|All files (*.*)|*.*",
                    CheckFileExists = true,
                };
                if (saves.Length > 0)
                    dlg.InitialDirectory = Path.GetDirectoryName(saves[0].FullName);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                savPath = dlg.FileName;
            }

            var pos = Savegame.GetPlayerPosition(savPath);
            if (pos == null)
            {
                _canvas.PlayerWorldPos = null;
                _canvas.Invalidate();
                SetStatus("Character position: no PlayerBreadcrumbs found in this save.");
                return;
            }

            var (sx, sy, sz, arch) = pos.Value; // save coords, cm
            var cal = Calibration.Get(savPath);
            double dx = cal?.ShiftX ?? 0, dy = cal?.ShiftY ?? 0, dz = cal?.ShiftZ ?? 0; // shift cm: save = world + shift => world = save - shift
            var wx = (float)(sx - dx);
            var wy = (float)(sy - dy);
            var wz = (float)(sz - dz);
            _canvas.PlayerWorldPos = new System.Drawing.PointF(wx, wy);
            _canvas.PlayerWorldZCm = wz;
            _canvas.PlayerLabel = "Player";
            // canvas centers/edits in tile-local coords for tiled maps (world = local + offset)
            _canvas.CenterOn(wx - _canvas.MapWorldOffsetX, wy - _canvas.MapWorldOffsetY);
            _canvas.Invalidate();
            var calNote = cal == null ? " (keine Kalibrierung — evtl. versetzt; wie bei Fahrzeug-Injektion kalibrieren)" : "";
            SetStatus($"Character: save({sx / 100:0.#}, {sy / 100:0.#}, {sz / 100:0.#}) m, world({wx / 100:0.#}, {wy / 100:0.#}, {wz / 100:0.#}) m, map={arch}, save={Path.GetFileName(savPath)}{calNote}");
        }
        catch (Exception ex)
        {
            SetStatus("Character position error: " + ex.Message);
        }
    }

    private void AdjustSelectionHeight()
    {
        if (_map == null)
        {
            MessageBox.Show(this, "Load a map first.", "Adjust height", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var sel = _canvas.Selection;
        if (sel.Count == 0)
        {
            MessageBox.Show(this, "Select one or more objects first (click the map or pick a house group).", "Adjust height", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dlg = new Form
        {
            Text = "Adjust height (Z)",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(360, 150),
            MaximizeBox = false,
            MinimizeBox = false,
        };
        var lbl = new Label
        {
            AutoSize = false,
            Text = $"Delta Z for {sel.Count} selected object(s) in meters\n(positive = up, negative = down). Use ',' or '.'.",
            Left = 12, Top = 12, Width = 336, Height = 40,
        };
        var tb = new TextBox { Left = 12, Top = 56, Width = 336, Text = "0" };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 192, Top = 100, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 273, Top = 100, Width = 75 };
        dlg.Controls.AddRange(new Control[] { lbl, tb, ok, cancel });
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var s = (tb.Text ?? "").Trim().Replace(',', '.');
        if (!float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dzM))
        {
            MessageBox.Show(this, "Invalid number.", "Adjust height", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var dzCm = dzM * 100f;
        if (Math.Abs(dzCm) < 0.0001f)
        {
            SetStatus("height unchanged (delta 0)");
            return;
        }

        var items = new List<UndoItem>();
        var touched = new HashSet<int>(sel);
        foreach (var idx in sel)
        {
            if (idx < 0 || idx >= _visible.Count) continue;
            var o = _visible[idx];
            if (o.Deleted) continue;
            items.Add(Snapshot(idx));
            o.Z += dzCm;
            o.Edited = true;
            // owner-linked function actors (outpost/homesite interactables) follow the height
            foreach (var li in _canvas.FindOwnerLinked(idx))
            {
                if (li < 0 || li >= _visible.Count) continue;
                if (!touched.Add(li)) continue;
                var lo = _visible[li];
                if (lo.Deleted) continue;
                items.Add(Snapshot(li));
                lo.Z += dzCm;
                lo.Edited = true;
            }
        }
        if (items.Count == 0)
        {
            SetStatus("no editable objects in selection");
            return;
        }
        _undo.Push(new UndoEntry { Items = items });
        foreach (var it in items) _source.ResetItem(it.Idx);
        _canvas.Invalidate();
        var sign = dzCm >= 0 ? "+" : "";
        SetStatus($"height adjusted: {items.Count} object(s) by {sign}{dzM:0.###} m   |   undo: {_undo.Count}  (Ctrl+Z)");
    }

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

        var failures = new List<string>();
        foreach (var o in edited)
        {
            if (o.Deleted)
            {
                var r = UmapPatcher.PatchErase(data, o);
                if (r.Error.Length > 0) failures.Add($"{o.Name}: erase: {r.Error}");
                continue;
            }
            if (o.Edited)
            {
                var r = UmapPatcher.PatchLocation(data, o, o.X, o.Y, o.Z);
                if (r.Error.Length > 0) failures.Add($"{o.Name}: move: {r.Error}");
            }
            if (o.EditedRotation)
            {
                var r = UmapPatcher.PatchRotation(data, o, o.QX, o.QY, o.QZ, o.QW);
                if (r.Error.Length > 0) failures.Add($"{o.Name}: rotate: {r.Error}");
            }
            if (o.EditedScale)
            {
                var r = UmapPatcher.PatchScale(data, o, o.SX, o.SY, o.SZ);
                if (r.Error.Length > 0) failures.Add($"{o.Name}: scale: {r.Error}");
            }
        }

        if (failures.Count > 0)
            SetStatus($"export: {failures.Count} object(s) could not be patched — details in the dialog");

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
        var chunkPak = MapChunk.GetChunkPakFileName(_map.Path);
        var isChunkPatch = chunkPak != null;
        using var dlg = new SaveFileDialog
        {
            Title = "Save mod pak",
            Filter = "PAK archive (*.pak)|*.pak",
            FileName = chunkPak ?? $"mod_{mapName}_P.pak",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            PakWriter.Write(dlg.FileName, mountPoint, new[] { new PakWriter.Entry { Path = rel, Data = data } });

            // reset edit flags so re-export doesn't re-patch the same objects
            foreach (var o in _map.Objects)
                o.Edited = o.EditedRotation = o.EditedScale = o.Deleted = false;

            SetStatus($"exported {edited.Count - failures.Count}/{edited.Count:N0} object(s) -> {dlg.FileName}");
            var installHint = isChunkPatch
                ? "To use it in the game, copy the file into the game's Content\\Paks folder as a chunk patch\n" +
                  "(same folder as pakchunk*.pak, e.g. <game>\\StateOfDecay2\\Content\\Paks).\n" +
                  "Use the exact chunk-patch filename so SoD2 loads it for this map tile.\n" +
                  "Remove the file again to revert."
                : "To use it in the game, copy the file to:\n" +
                  "%LocalAppData%\\StateOfDecay2\\Saved\\Paks\n" +
                  "(Steam/Epic mod folder — Win+R: %LocalAppData%\\StateOfDecay2\\Saved\\Paks)";
            MessageBox.Show(this,
                $"Mod pak written:\n{dlg.FileName}\n\n" +
                $"Objects modified: {edited.Count - failures.Count:N0} / {edited.Count:N0}\n" +
                (failures.Count > 0
                    ? "\nUn-patched (stayed at original position):\n" + string.Join("\n", failures.Take(8)) + (failures.Count > 8 ? $"\n... and {failures.Count - 8} more" : "")
                    : "") +
                "\n\n" + installHint + "\n\n" +
                "The '_P' suffix makes the game prefer this pak over the original map.",
                failures.Count > 0 ? "Export complete (with warnings)" : "Export complete",
                MessageBoxButtons.OK,
                failures.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
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
        string first = "";
        var restored = 0;
        foreach (var it in entry.Items)
        {
            if (it.Idx < 0 || it.Idx >= _visible.Count) continue;
            var o = _visible[it.Idx];
            o.X = it.OldX;
            o.Y = it.OldY;
            o.Z = it.OldZ;
            o.QX = it.OldQX;
            o.QY = it.OldQY;
            o.QZ = it.OldQZ;
            o.QW = it.OldQW;
            o.SX = it.OldSX;
            o.SY = it.OldSY;
            o.SZ = it.OldSZ;
            o.Deleted = it.OldDeleted;
            o.Edited = o.EditedRotation = o.EditedScale = false;
            _source.ResetItem(it.Idx);
            if (first.Length == 0) first = o.Name;
            restored++;
        }
        _canvas.Invalidate();
        SetStatus($"undo: {restored} object(s) restored ({first}{(restored > 1 ? $" +{restored - 1} more" : "")})   |   undo: {_undo.Count}");
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
