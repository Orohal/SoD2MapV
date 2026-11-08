using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SoD2MapViewer;

/// <summary>Injects a vehicle into a live savegame at a world position picked on the map.
/// Handles the per-save save↔world calibration (probe inject + observed offset).</summary>
public sealed class InjectDialog : Form
{
    private readonly ComboBox _cboSave = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380, DropDownWidth = 500 };
    private readonly ComboBox _cboClass = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380, DropDownWidth = 500 };
    private readonly TextBox _txtX = new() { Width = 90 };
    private readonly TextBox _txtY = new() { Width = 90 };
    private readonly TextBox _txtZ = new() { Width = 90 };
    private readonly TextBox _txtYaw = new() { Width = 90, Text = "0" };
    private readonly TextBox _txtDx = new() { Width = 90 };
    private readonly TextBox _txtDy = new() { Width = 90 };
    private readonly TextBox _txtDz = new() { Width = 90 };
    private readonly Label _lblInfo = new()
    {
        Dock = DockStyle.Fill,
        ForeColor = Color.DimGray,
        TextAlign = ContentAlignment.MiddleLeft,
        Height = 40,
    };
    private readonly Button _btnInject = new() { Text = "Inject", AutoSize = true };
    private readonly Button _btnCalibrate = new() { Text = "Calibrate...", AutoSize = true };
    private readonly Button _btnDeleteProbe = new() { Text = "Delete probe vehicle", AutoSize = true, Visible = false };
    private readonly Button _btnCancel = new() { Text = "Close", AutoSize = true };

    private FileInfo[] _saves = Array.Empty<FileInfo>();
    private int _probeIndex = -1;   // last injected calibration probe (for cleanup)

    public InjectDialog(PointF? worldCm)
    {
        Text = "Inject vehicle into savegame";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9f);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(12), ColumnCount = 4, RowCount = 9 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void AddRow(int row, string label, Control c1, Control? c2 = null, Control? c3 = null)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            layout.Controls.Add(c1, 1, row);
            if (c2 != null) layout.Controls.Add(c2, 2, row);
            if (c3 != null) layout.Controls.Add(c3, 3, row);
        }

        AddRow(0, "Save slot:", _cboSave);
        AddRow(1, "Vehicle class:", _cboClass);
        AddRow(2, "World X/Y/Z (m):", _txtX, _txtY, _txtZ);
        AddRow(3, "Yaw (deg):", _txtYaw);
        AddRow(4, "Calibration shift (m):", _txtDx, _txtDy, _txtDz);
        layout.SetColumnSpan(_lblInfo, 4);
        layout.Controls.Add(_lblInfo, 0, 5);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Margin = new Padding(0) };
        buttons.Controls.AddRange(new Control[] { _btnInject, _btnCalibrate, _btnDeleteProbe, _btnCancel });
        layout.SetColumnSpan(buttons, 4);
        layout.Controls.Add(buttons, 0, 6);

        Controls.Add(layout);
        AcceptButton = _btnInject;
        CancelButton = _btnCancel;

        _cboSave.SelectedIndexChanged += (s, e) => LoadClasses();
        _btnInject.Click += (s, e) => Inject(calibrating: false);
        _btnCalibrate.Click += (s, e) => Inject(calibrating: true);
        _btnDeleteProbe.Click += (s, e) => DeleteProbe();
        _btnCancel.Click += (s, e) => Close();

        LoadSaves(worldCm);
    }

    private void LoadSaves(PointF? worldCm)
    {
        _saves = Savegame.FindSavegames();
        if (_saves.Length == 0)
        {
            _lblInfo.Text = "No savegame found under %LOCALAPPDATA%\\StateOfDecay2\\Saved\\Steam.";
            _btnInject.Enabled = _btnCalibrate.Enabled = false;
            return;
        }
        foreach (var f in _saves)
        {
            var info = Savegame.ReadInfo(f.FullName);
            _cboSave.Items.Add($"{f.Name}  —  {info?.VehicleCount ?? 0} vehicles, saved {f.LastWriteTime:dd.MM. HH:mm}");
        }
        _cboSave.SelectedIndex = 0;
        LoadClasses();

        if (worldCm is { } w)
        {
            _txtX.Text = (w.X / 100).ToString("0.##");
            _txtY.Text = (w.Y / 100).ToString("0.##");
        }
        else
        {
            _lblInfo.Text = "Tip: right-click the map to pick the injection position.";
        }
        LoadShift();
    }

    private FileInfo? CurrentSave => _cboSave.SelectedIndex >= 0 && _cboSave.SelectedIndex < _saves.Length ? _saves[_cboSave.SelectedIndex] : null;

    private void LoadClasses()
    {
        _cboClass.Items.Clear();
        var save = CurrentSave;
        if (save == null) return;
        var info = Savegame.ReadInfo(save.FullName);
        if (info == null) { _lblInfo.Text = "Failed to parse " + save.Name; return; }
        foreach (var c in info.VehicleClasses) _cboClass.Items.Add(c);
        if (_cboClass.Items.Count > 0) _cboClass.SelectedIndex = 0;
        LoadShift();
    }

    private void LoadShift()
    {
        var save = CurrentSave;
        if (save == null) return;
        var e = Calibration.Get(save.FullName);
        _txtDx.Text = e == null ? "0" : (e.ShiftX / 100).ToString("0.###");
        _txtDy.Text = e == null ? "0" : (e.ShiftY / 100).ToString("0.###");
        _txtDz.Text = e == null ? "0" : (e.ShiftZ / 100).ToString("0.###");
        if (e == null)
            _lblInfo.Text = "Not calibrated for this save yet — use 'Calibrate...' once (inject a probe, enter where it landed).";
    }

    private static bool GameRunning()
    {
        try { return Process.GetProcessesByName("StateOfDecay2").Length > 0; }
        catch { return false; }
    }

    private bool ParseFields(out int classIdx, out double x, out double y, out double z, out double yaw,
                             out double dx, out double dy, out double dz)
    {
        classIdx = 0; x = y = z = yaw = dx = dy = dz = 0;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var ok = double.TryParse(_txtX.Text, System.Globalization.NumberStyles.Float, inv, out x)
              & double.TryParse(_txtY.Text, System.Globalization.NumberStyles.Float, inv, out y)
              & double.TryParse(_txtZ.Text, System.Globalization.NumberStyles.Float, inv, out z)
              & double.TryParse(_txtYaw.Text, System.Globalization.NumberStyles.Float, inv, out yaw)
              & double.TryParse(_txtDx.Text, System.Globalization.NumberStyles.Float, inv, out dx)
              & double.TryParse(_txtDy.Text, System.Globalization.NumberStyles.Float, inv, out dy)
              & double.TryParse(_txtDz.Text, System.Globalization.NumberStyles.Float, inv, out dz);
        if (!ok) { _lblInfo.Text = "Please enter valid numbers."; return false; }
        classIdx = _cboClass.SelectedIndex;
        if (classIdx < 0) { _lblInfo.Text = "No vehicle class selected."; return false; }
        return true;
    }

    private void Inject(bool calibrating)
    {
        var save = CurrentSave;
        if (save == null) return;
        if (GameRunning())
        {
            MessageBox.Show(this, "State of Decay 2 is running. Close the game first — it overwrites the savegame on exit.",
                "Game running", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!ParseFields(out var classIdx, out var x, out var y, out var z, out var yaw, out var dx, out var dy, out var dz))
            return;

        // save coords (cm) = world coords (cm) + shift (cm)
        double sx = x * 100 + dx * 100, sy = y * 100 + dy * 100, sz = z * 100 + dz * 100;

        var temp = Path.Combine(Path.GetTempPath(), "sod2_inject_" + Guid.NewGuid().ToString("N") + ".sav");
        _lblInfo.Text = "Injecting...";
        Cursor = Cursors.WaitCursor;
        var rc = Savegame.SaveInject(save.FullName, temp, classIdx, (float)sx, (float)sy, (float)sz, (float)yaw, -1, setGuid: true);
        Cursor = Cursors.Default;
        if (rc != 0)
        {
            _lblInfo.Text = "Injection failed (rc=" + rc + ").";
            try { File.Delete(temp); } catch { }
            return;
        }

        // backup + install
        var backupDir = Path.Combine(save.DirectoryName!, "Backups");
        Directory.CreateDirectory(backupDir);
        var backup = Path.Combine(backupDir, save.Name + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        File.Copy(save.FullName, backup, overwrite: true);
        File.Copy(temp, save.FullName, overwrite: true);
        File.Delete(temp);

        var newCount = Savegame.ReadInfo(save.FullName)?.VehicleCount ?? -1;
        _probeIndex = newCount - 1;
        if (calibrating)
        {
            _btnDeleteProbe.Visible = true;
            _lblInfo.Text = $"Probe injected as entry [{_probeIndex}]. Start the game, find the probe near the clicked point, then enter the offset.";
            if (ShowOffsetPrompt(out var east, out var north))
            {
                // observed displacement (world) = (east, -north) cm  ->  shift_new = shift_old - displacement
                var nx = dx - east;
                var ny = dy + north;
                Calibration.Set(save.FullName, new Calibration.Entry(nx * 100, ny * 100, dz * 100, DateTime.Now));
                _txtDx.Text = nx.ToString("0.###");
                _txtDy.Text = ny.ToString("0.###");
                _lblInfo.Text = $"Calibration saved: shift = ({nx:0.###}, {ny:0.###}, {dz:0.###}) m. " +
                                "Inject the real vehicle(s) now — the probe can stay or be deleted.";
            }
        }
        else
        {
            _lblInfo.Text = $"Injected as entry [{_probeIndex}] (count now {newCount}). Backup: {Path.GetFileName(backup)}";
            _btnDeleteProbe.Visible = true;
        }
    }

    private bool ShowOffsetPrompt(out double east, out double north)
    {
        east = north = 0;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var txtE = new TextBox { Width = 90, Text = "0" };
        var txtN = new TextBox { Width = 90, Text = "0" };
        var form = new Form
        {
            Text = "Probe offset",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ClientSize = new Size(430, 150),
        };
        var lbl = new Label
        {
            Text = "Where did the probe vehicle end up, relative to the clicked map point?\n" +
                   "East: positive = east, negative = west.   North: positive = north, negative = south.",
            AutoSize = true,
            Location = new Point(12, 10),
        };
        var lblE = new Label { Text = "East (m):", AutoSize = true, Location = new Point(12, 68) };
        var lblN = new Label { Text = "North (m):", AutoSize = true, Location = new Point(160, 68) };
        txtE.Location = new Point(70, 65);
        txtN.Location = new Point(225, 65);
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(330, 105) };
        var cancel = new Button { Text = "Skip", DialogResult = DialogResult.Cancel, Location = new Point(330, 65) };
        form.Controls.AddRange(new Control[] { lbl, lblE, txtE, lblN, txtN, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        if (form.ShowDialog(this) != DialogResult.OK) return false;
        return double.TryParse(txtE.Text, System.Globalization.NumberStyles.Float, inv, out east)
            & double.TryParse(txtN.Text, System.Globalization.NumberStyles.Float, inv, out north);
    }

    private void DeleteProbe()
    {
        if (_probeIndex < 0) return;
        var save = CurrentSave;
        if (save == null) return;
        if (GameRunning())
        {
            MessageBox.Show(this, "State of Decay 2 is running. Close the game first.", "Game running",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var temp = Path.Combine(Path.GetTempPath(), "sod2_delete_" + Guid.NewGuid().ToString("N") + ".sav");
        var rc = Savegame.SaveDelete(save.FullName, temp, new[] { _probeIndex });
        if (rc != 0) { _lblInfo.Text = "Delete failed (rc=" + rc + ")."; try { File.Delete(temp); } catch { } return; }
        var backupDir = Path.Combine(save.DirectoryName!, "Backups");
        Directory.CreateDirectory(backupDir);
        File.Copy(save.FullName, Path.Combine(backupDir, save.Name + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")), overwrite: true);
        File.Copy(temp, save.FullName, overwrite: true);
        File.Delete(temp);
        _btnDeleteProbe.Visible = false;
        _probeIndex = -1;
        _lblInfo.Text = "Probe deleted.";
        // refresh slot list counts
        var sel = _cboSave.SelectedIndex;
        _cboSave.Items.Clear();
        _saves = Savegame.FindSavegames();
        foreach (var f in _saves)
        {
            var info = Savegame.ReadInfo(f.FullName);
            _cboSave.Items.Add($"{f.Name}  —  {info?.VehicleCount ?? 0} vehicles, saved {f.LastWriteTime:dd.MM. HH:mm}");
        }
        if (sel >= 0 && sel < _cboSave.Items.Count) _cboSave.SelectedIndex = sel;
    }
}
