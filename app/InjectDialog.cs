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
    private readonly Button _btnBrowseSave = new() { Text = "📁…", AutoSize = true, Margin = new Padding(4, 0, 0, 0) };
    private readonly Button _btnCopySave = new() { Text = L10n.T("inject.copySave", "Kopie erstellen"), AutoSize = true, Margin = new Padding(4, 0, 0, 0) };
    private readonly Button _btnNewGroup = new() { Text = L10n.T("inject.newGroup", "Neue Gruppe…"), AutoSize = true, Margin = new Padding(4, 0, 0, 0) };
    private readonly Button _btnInject = new() { Text = L10n.T("inject.inject", "Einfügen"), AutoSize = true };
    private readonly Button _btnCalibrate = new() { Text = L10n.T("inject.calibrate", "Kalibrieren..."), AutoSize = true };
    private readonly Button _btnDeleteProbe = new() { Text = L10n.T("inject.deleteProbe", "Testfahrzeug löschen"), AutoSize = true, Visible = false };
    private readonly Button _btnCancel = new() { Text = L10n.T("dialog.close", "Schließen"), AutoSize = true };

    private FileInfo[] _saves = Array.Empty<FileInfo>();
    private FileInfo? _customSave;
    private int _probeIndex = -1;   // last injected calibration probe (for cleanup)

    public InjectDialog(PointF? worldCm, float? zCm = null, float defaultYawDeg = 0f, string? hint = null)
    {
        Text = L10n.T("inject.title", "Fahrzeug in Savegame einfügen");
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9f);
        MinimumSize = new Size(560, 320);
        ClientSize = new Size(640, 380);
        LoadSize();
        FormClosing += (s, e) => SaveSize();

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 4, RowCount = 9 };
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

        var saveRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty };
        saveRow.Controls.AddRange(new Control[] { _cboSave, _btnBrowseSave, _btnCopySave, _btnNewGroup });
        AddRow(0, L10n.T("inject.savegame", "Savegame:"), saveRow);
        AddRow(1, L10n.T("inject.vehicleClass", "Fahrzeugklasse:"), _cboClass);
        AddRow(2, L10n.T("inject.worldXYZ", "Welt X/Y/Z (m):"), _txtX, _txtY, _txtZ);
        AddRow(3, L10n.T("inject.yaw", "Yaw (Grad):"), _txtYaw);
        AddRow(4, L10n.T("inject.calibOffset", "Kalibrier-Offset (m):"), _txtDx, _txtDy, _txtDz);
        layout.SetColumnSpan(_lblInfo, 4);
        layout.Controls.Add(_lblInfo, 0, 5);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Margin = new Padding(0) };
        buttons.Controls.AddRange(new Control[] { _btnInject, _btnCalibrate, _btnDeleteProbe, _btnCancel });
        layout.SetColumnSpan(buttons, 4);
        layout.Controls.Add(buttons, 0, 6);

        Controls.Add(layout);
        UiTheme.ApplyTree(this);
        AcceptButton = _btnInject;
        CancelButton = _btnCancel;

        _cboSave.SelectedIndexChanged += (s, e) => LoadClasses();
        _btnBrowseSave.Click += (s, e) => BrowseSave();
        _btnCopySave.Click += (s, e) => CopySelectedSave();
        _btnNewGroup.Click += (s, e) => NewGroupFromSelected();
        _btnInject.Click += (s, e) => Inject(calibrating: false);
        _btnCalibrate.Click += (s, e) => Inject(calibrating: true);
        _btnDeleteProbe.Click += (s, e) => DeleteProbe();
        _btnCancel.Click += (s, e) => Close();

        LoadSaves(worldCm, zCm, defaultYawDeg);
        if (!string.IsNullOrEmpty(hint)) _lblInfo.Text = hint;
    }

    private string SizePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoD2MapViewer", "injectdialog_size.txt");

    private void LoadSize()
    {
        try
        {
            if (File.Exists(SizePath))
            {
                var parts = File.ReadAllText(SizePath).Split('x');
                if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
                {
                    w = Math.Max(w, MinimumSize.Width);
                    h = Math.Max(h, MinimumSize.Height);
                    ClientSize = new Size(w, h);
                }
            }
        }
        catch { }
    }

    private void SaveSize()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SizePath)!);
            File.WriteAllText(SizePath, $"{ClientSize.Width}x{ClientSize.Height}");
        }
        catch { }
    }

    private void LoadSaves(PointF? worldCm, float? zCm, float defaultYawDeg)
    {
        _saves = Savegame.FindSavegames();
        RebuildSaveCombo(selectCustom: false);

        if (worldCm is { } w)
        {
            _txtX.Text = (w.X / 100).ToString("0.##");
            _txtY.Text = (w.Y / 100).ToString("0.##");
        }
        else if (_cboSave.Items.Count > 0)
        {
            _lblInfo.Text = "Tipp: Rechtsklick auf die Karte wählt die Einfügeposition.";
        }
        if (zCm.HasValue) _txtZ.Text = (zCm.Value / 100).ToString("0.##");
        _txtYaw.Text = defaultYawDeg.ToString("0.##");
        LoadShift();
    }

    private void RebuildSaveCombo(bool selectCustom)
    {
        var keepCustomSelected = selectCustom || (_customSave != null && _cboSave.SelectedIndex == 0);
        var prevSavePath = keepCustomSelected ? null : CurrentSave?.FullName;

        _cboSave.BeginUpdate();
        _cboSave.Items.Clear();
        if (_customSave != null)
            _cboSave.Items.Add(DescribeSave(_customSave, custom: true));
        foreach (var f in _saves)
            _cboSave.Items.Add(DescribeSave(f, custom: false));
        _cboSave.EndUpdate();

        if (_cboSave.Items.Count == 0)
        {
            _lblInfo.Text = "Kein Savegame unter %LOCALAPPDATA%\\StateOfDecay2\\Saved\\Steam gefunden. Mit \"📁…\" eine .sav-Datei wählen.";
            _btnInject.Enabled = _btnCalibrate.Enabled = false;
            return;
        }

        _btnInject.Enabled = _btnCalibrate.Enabled = true;
        var idx = 0;
        if (keepCustomSelected && _customSave != null)
            idx = 0;
        else if (prevSavePath != null)
        {
            var offset = _customSave != null ? 1 : 0;
            for (int i = 0; i < _saves.Length; i++)
            {
                if (string.Equals(_saves[i].FullName, prevSavePath, StringComparison.OrdinalIgnoreCase)) { idx = i + offset; break; }
            }
        }
        if (idx < 0 || idx >= _cboSave.Items.Count) idx = 0;
        _cboSave.SelectedIndex = idx;
        LoadClasses();
    }

    private static string DescribeSave(FileInfo f, bool custom)
    {
        string suffix;
        try
        {
            var info = Savegame.ReadInfo(f.FullName);
            suffix = $"{info?.VehicleCount ?? 0} vehicles, saved {f.LastWriteTime:dd.MM. HH:mm}";
        }
        catch { suffix = $"saved {f.LastWriteTime:dd.MM. HH:mm}"; }
        var label = custom ? "[Custom] " : "";
        return $"{label}{f.Name}  —  {suffix}";
    }

    private void BrowseSave()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Select savegame (.sav) to target",
            Filter = "Savegame (*.sav)|*.sav|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        try
        {
            var saves = Savegame.FindSavegames();
            if (saves.Length > 0)
                dlg.InitialDirectory = Path.GetDirectoryName(saves[0].FullName);
        }
        catch { }

        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var path = Path.GetFullPath(dlg.FileName);
            // Validate by parsing (filename does not need to match SaveGame_*.sav)
            Savegame.ReadInfo(path);
            _customSave = new FileInfo(path);
            RebuildSaveCombo(selectCustom: true);
            _lblInfo.Text = "Custom save selected: " + path;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Die ausgewählte Datei konnte nicht als SoD2-Save gelesen werden:\n" + ex.Message,
                "Ungültiges Savegame", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void CopySelectedSave()
    {
        var save = CurrentSave;
        if (save == null)
        {
            MessageBox.Show(this, "Kein Savegame ausgewählt.", "Kopie erstellen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            var dir = Path.GetDirectoryName(save.FullName) ?? "";
            var name = Path.GetFileNameWithoutExtension(save.FullName);
            var ext = Path.GetExtension(save.FullName);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var targetPath = Path.Combine(dir, $"{name}_copy_{stamp}{ext}");
            var counter = 1;
            while (File.Exists(targetPath))
                targetPath = Path.Combine(dir, $"{name}_copy_{stamp}_{counter++}{ext}");

            File.Copy(save.FullName, targetPath, overwrite: false);
            try
            {
                Savegame.ReadInfo(targetPath);
            }
            catch (Exception ex)
            {
                try { File.Delete(targetPath); } catch { }
                MessageBox.Show(this, "Die Kopie konnte nicht als gültiges Savegame gelesen werden und wurde gelöscht:\n" + ex.Message,
                    "Kopie erstellen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _customSave = new FileInfo(targetPath);
            RebuildSaveCombo(selectCustom: true);
            LoadClasses();
            _lblInfo.Text = "Kopie erstellt: " + Path.GetFileName(targetPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Kopie konnte nicht erstellt werden:\n" + ex.Message,
                "Kopie erstellen", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Creates a new community ("group"): copies the selected .sav under the next free
    /// numbered slot name and rewrites its identity GUIDs, so the game sees a separate community.</summary>
    private void NewGroupFromSelected()
    {
        var save = CurrentSave;
        if (save == null)
        {
            MessageBox.Show(this, "Kein Savegame ausgewählt.", "Neue Gruppe", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            var targetPath = Savegame.CloneNewIdentity(save.FullName);
            _customSave = new FileInfo(targetPath);
            RebuildSaveCombo(selectCustom: true);
            LoadClasses();
            _lblInfo.Text = "Neue Gruppe erstellt: " + Path.GetFileName(targetPath) + "  (Identität neu: ID + GameId)";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Neue Gruppe konnte nicht erstellt werden:\n" + ex.Message,
                "Neue Gruppe", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private FileInfo? CurrentSave
    {
        get
        {
            var i = _cboSave.SelectedIndex;
            if (i < 0) return null;
            if (_customSave != null)
            {
                if (i == 0) return _customSave;
                i--;
            }
            return i >= 0 && i < _saves.Length ? _saves[i] : null;
        }
    }

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
        try
        {
            // Check for multiple possible SoD2 process names (UWP, Steam, Xbox, etc.)
            var processNames = new[]
            {
                "StateOfDecay2",                    // Steam/Win32
                "StateOfDecay2-WinGDK-Shipping",   // Xbox/MS Store (GDK)
                "StateOfDecay2-WinGDK-Test",       // Xbox/MS Store test build
                "StateOfDecay2-WinGDK-Development", // Xbox/MS Store dev build
                "StateOfDecay2-Win64-Shipping",    // Alternative Win64 build name
            };
            foreach (var name in processNames)
            {
                if (Process.GetProcessesByName(name).Length > 0)
                    return true;
            }
            // Fallback: check for any process with "StateOfDecay2" in the name
            return Process.GetProcesses().Any(p =>
            {
                try { return p.ProcessName.Contains("StateOfDecay2", StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            });
        }
        catch { return false; }
    }

    private bool ParseFields(out int classIdx, out double x, out double y, out double z, out double yaw,
                             out double dx, out double dy, out double dz)
    {
        classIdx = 0; x = y = z = yaw = dx = dy = dz = 0;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        static string N(string? s) { var t = (s ?? "").Trim().Replace(',', '.'); return t.Length == 0 ? "0" : t; }
        var ok = double.TryParse(N(_txtX.Text), System.Globalization.NumberStyles.Float, inv, out x)
              & double.TryParse(N(_txtY.Text), System.Globalization.NumberStyles.Float, inv, out y)
              & double.TryParse(N(_txtZ.Text), System.Globalization.NumberStyles.Float, inv, out z)
              & double.TryParse(N(_txtYaw.Text), System.Globalization.NumberStyles.Float, inv, out yaw)
              & double.TryParse(N(_txtDx.Text), System.Globalization.NumberStyles.Float, inv, out dx)
              & double.TryParse(N(_txtDy.Text), System.Globalization.NumberStyles.Float, inv, out dy)
              & double.TryParse(N(_txtDz.Text), System.Globalization.NumberStyles.Float, inv, out dz);
        if (!ok) { _lblInfo.Text = "Please enter valid numbers (use . or , as decimal)."; return false; }
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
        try   // P24: always clean up the temp save, even on unexpected errors
        {
        _lblInfo.Text = "Injecting...";
        Cursor = Cursors.WaitCursor;
        var rc = Savegame.SaveInject(save.FullName, temp, classIdx, (float)sx, (float)sy, (float)sz, (float)yaw, -1, setGuid: true);
        Cursor = Cursors.Default;
        if (rc != 0)
        {
            _lblInfo.Text = "Injection failed (rc=" + rc + ").";
            return;
        }

        // backup + atomic install (P01: rollback on failure)
        var backupDir = Path.Combine(save.DirectoryName!, "Backups");
        Directory.CreateDirectory(backupDir);
        var backup = Path.Combine(backupDir, save.Name + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        try
        {
            SafeFile.ReplaceWithBackup(temp, save.FullName, backup);
        }
        catch (Exception ex)
        {
            _lblInfo.Text = "Install fehlgeschlagen (Original aus Backup wiederhergestellt): " + ex.Message;
            return;
        }

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
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
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
            ClientSize = new Size(500, 180),
        };
        var lbl = new Label
        {
            Text = "Where did the probe vehicle end up, relative to the clicked map point?\n" +
                   "East:  positive = East,  negative = West.\n" +
                   "North: positive = North, negative = South.\n" +
                   "Values are in meters. Leave as 0 if the probe appeared exactly at the clicked point.",
            AutoSize = true,
            Location = new Point(12, 10),
            MaximumSize = new Size(470, 60),
        };
        var lblE = new Label { Text = "East (m):", AutoSize = true, Location = new Point(12, 80) };
        var lblN = new Label { Text = "North (m):", AutoSize = true, Location = new Point(200, 80) };
        txtE.Location = new Point(70, 77);
        txtN.Location = new Point(260, 77);
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(395, 135) };
        var cancel = new Button { Text = "Skip calibration", DialogResult = DialogResult.Cancel, Location = new Point(395, 95) };
        form.Controls.AddRange(new Control[] { lbl, lblE, txtE, lblN, txtN, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        if (form.ShowDialog(this) != DialogResult.OK) return false;

        var parseOk = double.TryParse(txtE.Text, System.Globalization.NumberStyles.Float, inv, out east)
            & double.TryParse(txtN.Text, System.Globalization.NumberStyles.Float, inv, out north);

        if (!parseOk)
        {
            MessageBox.Show(this, "Invalid offset values. Please enter numeric values.", "Invalid Input",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        // Validate offset values are reasonable
        const double MaxOffsetM = 10000; // 10 km max offset
        if (Math.Abs(east) > MaxOffsetM || Math.Abs(north) > MaxOffsetM)
        {
            MessageBox.Show(this, $"Offset values exceed reasonable bounds (±{MaxOffsetM} m). Please re-enter.", "Invalid Offset",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        return true;
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
        // refresh slot list counts while preserving a custom-picked save
        _saves = Savegame.FindSavegames();
        if (_customSave != null)
            _customSave = new FileInfo(_customSave.FullName);
        RebuildSaveCombo(selectCustom: false);
    }
}
