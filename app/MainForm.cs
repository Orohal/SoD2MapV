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

    private readonly ListBox _mapList;
    private readonly DataGridView _grid;
    private readonly MapCanvas _canvas;
    private readonly TextBox _search;
    private readonly TextBox _status;
    private readonly Panel _legendHost;

    private MapModel? _map;
    private readonly BindingSource _source = new();
    private List<MapObject> _visible = new();

    public MainForm()
    {
        Text = "State of Decay 2 — Map Viewer (v0.1)";
        Size = new Size(1500, 950);
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;

        // left: map list + legend
        var left = new Panel { Dock = DockStyle.Left, Width = 360 };
        _mapList = new ListBox { Dock = DockStyle.Fill, Font = new Font("Consolas", 9f) };
        _mapList.SelectedIndexChanged += MapListSelected;
        _legendHost = new Panel { Dock = DockStyle.Bottom, Height = 180, BackColor = Color.FromArgb(32, 34, 40) };

        left.Controls.Add(_mapList);
        left.Controls.Add(_legendHost);

        // right: grid (top) + canvas (bottom)
        var right = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 420,
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
        _canvas = new MapCanvas { Dock = DockStyle.Fill };
        _canvas.SelectionChanged += CanvasSelection;
        right.Panel1.Controls.Add(_grid);
        right.Panel2.Controls.Add(_canvas);

        // top toolbar (added last so it docks above fill panel)
        var top = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Color.FromArgb(38, 40, 48) };
        var btnOpen = new Button { Left = 8, Top = 20, Text = "Choose game folder...", AutoSize = true };
        btnOpen.Click += (s, e) => PickGameDir();
        var labelMap = new Label { Left = 160, Top = 24, AutoSize = true, Text = "Object filter:", ForeColor = Color.Gainsboro };
        _search = new TextBox { Left = 250, Top = 20, Width = 320, PlaceholderText = "name/class/mesh contains..." };
        _search.TextChanged += (s, e) => ApplyFilter();
        top.Controls.AddRange(new Control[] { btnOpen, labelMap, _search });

        _status = new TextBox
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(38, 40, 48),
            ForeColor = Color.LightGray,
        };

        Controls.Add(right);
        Controls.Add(_status);
        Controls.Add(top);
        Controls.Add(left);

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
        _mapList.BeginUpdate();
        _mapList.Items.Clear();
        foreach (var m in _loader.MapPaths)
            _mapList.Items.Add(m);
        _mapList.EndUpdate();

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

    private async void MapListSelected(object? sender, EventArgs e)
    {
        if (_mapList.SelectedItem is not string path) return;
        SetStatus($"parsing {Path.GetFileName(path)} ...");
        var model = await _loader.LoadMapAsync(path);
        SetMap(model);
    }

    private void SetMap(MapModel model)
    {
        _map = model;
        var ok = model.Error == null;
        SetStatus(
            $"map: {Path.GetFileName(model.Path)}  |  exports: {model.TotalExports:N0}  objects: {model.Objects.Count:N0}  " +
            $"without position: {Math.Max(0, model.FailedExports):N0}  parse time: {model.LoadMs} ms" +
            (ok ? "" : $"  |  ERROR: {model.Error}"));
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
