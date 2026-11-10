using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SoD2MapViewer;

/// <summary>Injects item stacks into a survivor's backpack in a live savegame.</summary>
public sealed class ItemInjectDialog : Form
{
    private sealed class Preset
    {
        public string Label { get; init; } = "";
        public string ItemType { get; init; } = "Miscellaneous";
        public string ClassPath { get; init; } = "";
        public override string ToString() => Label;
    }

    private static readonly Preset[] Presets =
    {
        new() { Label = "Benzinkanister (Fuel)", ItemType = "Miscellaneous", ClassPath = "/Game/Items/MiscellaneousItems/MiscItemRefuelVehicle.MiscItemRefuelVehicle_C" },
        new() { Label = "Auto-Reparaturkasten (Repair)", ItemType = "Miscellaneous", ClassPath = "/Game/Items/MiscellaneousItems/MiscItemRepairVehicle.MiscItemRepairVehicle_C" },
    };

    private readonly ComboBox _cboSave = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380, DropDownWidth = 500 };
    private readonly TextBox _txtSurvivor = new() { Width = 80, Text = "-1" };
    private readonly ComboBox _cboPreset = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380, DropDownWidth = 500 };
    private readonly TextBox _txtItemType = new() { Width = 180, Text = "Miscellaneous" };
    private readonly TextBox _txtClass = new() { Width = 380 };
    private readonly NumericUpDown _numStack = new() { Minimum = 1, Maximum = 99, Value = 1, Width = 80 };
    private readonly ListBox _lstItems = new() { Width = 520, Height = 110, IntegralHeight = false };
    private readonly Label _lblInfo = new() { AutoSize = true, ForeColor = Color.Gray, MaximumSize = new Size(540, 0) };
    private readonly Button _btnInject = new() { Text = "Inject", AutoSize = true };
    private readonly Button _btnAdd = new() { Text = "Hinzufügen", AutoSize = true };
    private readonly Button _btnRemove = new() { Text = "Entfernen", AutoSize = true };
    private readonly Button _btnCancel = new() { Text = "Schließen", AutoSize = true, DialogResult = DialogResult.Cancel };

    private FileInfo[] _saves = Array.Empty<FileInfo>();
    private readonly List<(string ItemType, string ClassPath, int Stack)> _queue = new();

    public ItemInjectDialog()
    {
        Text = "Inject item into survivor backpack";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);

        var root = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        int row = 0;
        void AddRow(string label, Control c)
        {
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(new Label { Text = label, AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(0, 6, 6, 0) }, 0, row);
            root.Controls.Add(c, 1, row);
            row++;
        }

        var saveRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty };
        var btnRefresh = new Button { Text = "↻", Width = 28, Height = _cboSave.PreferredHeight };
        btnRefresh.Click += (s, e) => LoadSaves();
        saveRow.Controls.AddRange(new Control[] { _cboSave, btnRefresh });
        AddRow("Savegame:", saveRow);

        _cboSave.SelectedIndexChanged += (s, e) => UpdateInfo();
        AddRow("Survivor-ID:", _txtSurvivor);
        AddRow("", new Label { Text = "(-1 = LastPlayedSurvivorID)", AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(0, 0, 0, 4) });

        _cboPreset.Items.AddRange(Presets);
        _cboPreset.Items.Add("(Manuell)");
        _cboPreset.SelectedIndex = 0;
        _cboPreset.SelectedIndexChanged += (s, e) => ApplyPreset();
        AddRow("Preset:", _cboPreset);
        AddRow("Item-Typ:", _txtItemType);
        AddRow("Class-Path:", _txtClass);
        AddRow("Stack:", _numStack);

        var addRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty };
        addRow.Controls.AddRange(new Control[] { _btnAdd, _btnRemove });
        AddRow("", addRow);
        AddRow("Items:", _lstItems);
        AddRow("", _lblInfo);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 8, 0, 0) };
        buttons.Controls.AddRange(new Control[] { _btnInject, _btnCancel });
        AddRow("", buttons);

        Controls.Add(root);
        AcceptButton = _btnInject;
        CancelButton = _btnCancel;

        _btnAdd.Click += (s, e) => AddCurrent();
        _btnRemove.Click += (s, e) => RemoveSelected();
        _btnInject.Click += (s, e) => DoInject();

        ApplyPreset();
        LoadSaves();
    }

    private void ApplyPreset()
    {
        if (_cboPreset.SelectedItem is Preset p)
        {
            _txtItemType.Text = p.ItemType;
            _txtClass.Text = p.ClassPath;
            _txtItemType.ReadOnly = true;
            _txtClass.ReadOnly = true;
        }
        else
        {
            _txtItemType.ReadOnly = false;
            _txtClass.ReadOnly = false;
        }
    }

    private void AddCurrent()
    {
        var cls = _txtClass.Text.Trim();
        var type = _txtItemType.Text.Trim();
        if (cls.Length == 0 || type.Length == 0)
        {
            MessageBox.Show(this, "Item-Typ und Class-Path dürfen nicht leer sein.", "Inject item", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var stack = (int)_numStack.Value;
        _queue.Add((type, cls, stack));
        _lstItems.Items.Add($"{type} | stack={stack} | {ShortName(cls)}");
    }

    private void RemoveSelected()
    {
        var idx = _lstItems.SelectedIndex;
        if (idx < 0 || idx >= _queue.Count) return;
        _queue.RemoveAt(idx);
        _lstItems.Items.RemoveAt(idx);
    }

    private static string ShortName(string classPath)
    {
        var i = classPath.LastIndexOf('.');
        return i >= 0 && i + 1 < classPath.Length ? classPath[(i + 1)..] : classPath;
    }

    private void LoadSaves()
    {
        _saves = Savegame.FindSavegames();
        _cboSave.Items.Clear();
        foreach (var f in _saves)
            _cboSave.Items.Add($"{f.Name}  ({f.LastWriteTime:yyyy-MM-dd HH:mm}, {f.Length / 1024} KB)");
        if (_saves.Length == 0)
        {
            _lblInfo.Text = "Keine Savegames unter %LOCALAPPDATA%\\StateOfDecay2\\Saved\\Steam\\...\\Release gefunden.";
            _btnInject.Enabled = false;
        }
        else
        {
            _btnInject.Enabled = true;
            _cboSave.SelectedIndex = 0;
        }
    }

    private void UpdateInfo()
    {
        var sav = SelectedSave();
        if (sav == null) { _lblInfo.Text = ""; return; }
        try
        {
            var info = Savegame.ReadInfo(sav);
            _lblInfo.Text = info == null
                ? $"{sav} (Info nicht lesbar)"
                : $"{Path.GetFileName(sav)} — {info.VehicleCount} Fahrzeuge im Save, gespeichert {info.LastSaved:yyyy-MM-dd HH:mm:ss}";
        }
        catch { _lblInfo.Text = Path.GetFileName(sav); }
    }

    private string? SelectedSave() => _cboSave.SelectedIndex >= 0 && _cboSave.SelectedIndex < _saves.Length ? _saves[_cboSave.SelectedIndex].FullName : null;

    private static bool IsGameRunning()
    {
        try { return Process.GetProcessesByName("StateOfDecay2").Length > 0; }
        catch { return false; }
    }

    private void DoInject()
    {
        var sav = SelectedSave();
        if (sav == null) { MessageBox.Show(this, "Kein Savegame gewählt.", "Inject item", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (_queue.Count == 0) { MessageBox.Show(this, "Keine Items in der Liste. Bitte erst hinzufügen.", "Inject item", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (!int.TryParse(_txtSurvivor.Text.Trim(), out var survivorId)) survivorId = -1;

        var distinctTypes = _queue.Select(q => q.ItemType).Distinct(StringComparer.Ordinal).ToArray();
        if (distinctTypes.Length != 1)
        {
            MessageBox.Show(this, "Alle Items in einem Durchlauf müssen denselben Item-Typ haben (SaveItems-API-Limit). Bitte getrennt injizieren.", "Inject item", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var itemType = distinctTypes[0];

        if (IsGameRunning())
        {
            var r = MessageBox.Show(this,
                "State of Decay 2 scheint zu laufen. Das Savegame sollte nur im Hauptmenü/bei beendetem Spiel geändert werden.\n\nTrotzdem fortfahren?",
                "Spiel läuft", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;
        }

        var tmpOut = Path.Combine(Path.GetTempPath(), "sod2_iteminject_" + Guid.NewGuid().ToString("N") + ".sav");
        try
        {
            var paths = _queue.Select(q => q.ClassPath).ToArray();
            var stacks = _queue.Select(q => q.Stack).ToArray();
            int rc = Savegame.SaveItems(sav, tmpOut, survivorId, itemType, paths, stacks);
            if (rc != 0 || !File.Exists(tmpOut))
            {
                MessageBox.Show(this, $"SaveItems fehlgeschlagen (rc={rc}). Prüfe Survivor-ID, freie Rucksack-Slots und Class-Pfade.", "Inject item", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var bak = sav + ".bak-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            File.Copy(sav, bak, overwrite: false);
            File.Copy(tmpOut, sav, overwrite: true);

            var lines = string.Join(Environment.NewLine, _queue.Select(q => $"  - {q.Stack}x {ShortName(q.ClassPath)}"));
            MessageBox.Show(this,
                $"Erfolgreich injiziert (Survivor {survivorId}, Typ {itemType}):\n{lines}\n\nBackup: {bak}\n\nBitte im Spiel Rucksack prüfen (Spiel war währenddessen im Menü/beendet?).",
                "Inject item", MessageBoxButtons.OK, MessageBoxIcon.Information);
            LoadSaves();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Fehler beim Injizieren:\n" + ex.Message, "Inject item", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            try { if (File.Exists(tmpOut)) File.Delete(tmpOut); } catch { }
        }
    }
}
