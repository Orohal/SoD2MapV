using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SoD2MapViewer;

/// <summary>Injects item stacks into a survivor's backpack in a live savegame.</summary>
public sealed class ItemInjectDialog : Form
{
    private sealed class Preset
    {
        public string Label { get; init; } = "";
        public string ItemType { get; init; } = "Miscellaneous";
        public string ClassPath { get; init; } = "";
        public bool Persistent { get; init; }
        public override string ToString() => Label;
    }

    private sealed class PresetDto
    {
        public string Label { get; set; } = "";
        public string ItemType { get; set; } = "Miscellaneous";
        public string ClassPath { get; set; } = "";
    }

    private static readonly Preset[] BuiltinPresets =
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
    private readonly Button _btnLoadFromSave = new() { Text = "Items aus Save laden", AutoSize = true };
    private readonly Button _btnLoadFromAllSaves = new() { Text = "Presets aus allen Saves", AutoSize = true };
    private readonly Button _btnSavePreset = new() { Text = "Preset speichern", AutoSize = true };
    private readonly Button _btnDeletePreset = new() { Text = "Preset löschen", AutoSize = true };
    private readonly Button _btnCopySave = new() { Text = "Kopie erstellen", AutoSize = true };
    private readonly Button _btnCancel = new() { Text = "Schließen", AutoSize = true, DialogResult = DialogResult.Cancel };

    private FileInfo[] _saves = Array.Empty<FileInfo>();
    private FileInfo? _customSave;
    private readonly List<(string ItemType, string ClassPath, int Stack)> _queue = new();
    private readonly HashSet<string> _seenPresetKeys = new(StringComparer.Ordinal);
    private readonly List<Preset> _persistentPresets = new();

    public ItemInjectDialog()
    {
        Text = "Inject item into survivor backpack";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        Padding = new Padding(10);
        MinimumSize = new Size(620, 480);
        ClientSize = new Size(680, 540);
        LoadSize();
        FormClosing += (s, e) => SaveSize();

        var root = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill };
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
        var btnBrowse = new Button { Text = "📁…", AutoSize = true, Height = _cboSave.PreferredHeight };
        btnRefresh.Click += (s, e) => LoadSaves();
        btnBrowse.Click += (s, e) => BrowseSave();
        _btnCopySave.Click += (s, e) => CopySelectedSave();
        saveRow.Controls.AddRange(new Control[] { _cboSave, btnRefresh, btnBrowse, _btnCopySave });
        AddRow("Savegame:", saveRow);

        _cboSave.SelectedIndexChanged += (s, e) => { UpdateInfo(); LoadItemsFromSelectedSave(auto: true); };
        AddRow("Survivor-ID:", _txtSurvivor);
        AddRow("", new Label { Text = "(-1 = LastPlayedSurvivorID)", AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(0, 0, 0, 4) });

        LoadPersistentPresets();
        ResetPresets();
        _cboPreset.SelectedIndexChanged += (s, e) => ApplyPreset();
        var presetRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty };
        presetRow.Controls.AddRange(new Control[] { _cboPreset, _btnLoadFromSave, _btnLoadFromAllSaves, _btnSavePreset, _btnDeletePreset });
        AddRow("Preset:", presetRow);
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
        LoadSize();
        FormClosing += (s, e) => SaveSize();

        _btnCopySave.Click += (s, e) => CopySelectedSave();
        _btnAdd.Click += (s, e) => AddCurrent();
        _btnRemove.Click += (s, e) => RemoveSelected();
        _btnInject.Click += (s, e) => DoInject();
        _btnLoadFromSave.Click += (s, e) => LoadItemsFromSelectedSave(auto: false);
        _btnLoadFromAllSaves.Click += (s, e) => LoadPresetsFromAllSaves();
        _btnSavePreset.Click += (s, e) => SaveCurrentAsPreset();
        _btnDeletePreset.Click += (s, e) => DeleteSelectedPreset();

        ApplyPreset();
        LoadSaves();
    }

    private string SizePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoD2MapViewer", "iteminjectdialog_size.txt");

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

    // (size persistence helpers above)

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

    private static string Prompt(string text, string caption, string defaultValue)
    {
        using var f = new Form { Text = caption, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10), MinimizeBox = false, MaximizeBox = false };
        var lbl = new Label { Text = text, AutoSize = true, MaximumSize = new Size(400, 0) };
        var txt = new TextBox { Text = defaultValue, Width = 320 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Abbrechen", DialogResult = DialogResult.Cancel, AutoSize = true };
        var panel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown };
        panel.Controls.Add(lbl);
        panel.Controls.Add(txt);
        var btns = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        btns.Controls.AddRange(new[] { ok, cancel });
        panel.Controls.Add(btns);
        f.Controls.Add(panel);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        return f.ShowDialog() == DialogResult.OK ? txt.Text : "";
    }

    private void ResetPresets()
    {
        _cboPreset.BeginUpdate();
        try
        {
            _cboPreset.Items.Clear();
            _seenPresetKeys.Clear();
            var allPresets = new List<Preset>();
            allPresets.AddRange(BuiltinPresets);
            allPresets.AddRange(_persistentPresets.Select(p => new Preset { Label = p.Label, ItemType = p.ItemType, ClassPath = p.ClassPath, Persistent = true }));
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            var uniquePresets = new List<Preset>();
            foreach (var p in allPresets)
            {
                var key = p.ItemType + "\n" + p.ClassPath;
                if (!seenKeys.Add(key)) continue;
                uniquePresets.Add(p);
            }
            uniquePresets.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
            foreach (var p in uniquePresets)
            {
                _cboPreset.Items.Add(p);
                _seenPresetKeys.Add(p.ItemType + "\n" + p.ClassPath);
            }
            _cboPreset.Items.Add("(Manuell)");
            if (_cboPreset.Items.Count > 0) _cboPreset.SelectedIndex = 0;
        }
        finally { _cboPreset.EndUpdate(); }
    }

    private void LoadPersistentPresets()
    {
        _persistentPresets.Clear();
        try
        {
            var path = PresetsPath();
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);
            var dtos = JsonSerializer.Deserialize<List<PresetDto>>(json);
            if (dtos == null) return;
            foreach (var d in dtos)
            {
                if (string.IsNullOrWhiteSpace(d.ClassPath) || string.IsNullOrWhiteSpace(d.ItemType)) continue;
                _persistentPresets.Add(new Preset { Label = string.IsNullOrWhiteSpace(d.Label) ? ShortName(d.ClassPath) : d.Label, ItemType = d.ItemType, ClassPath = d.ClassPath, Persistent = true });
            }
        }
        catch { /* ignore corrupt file */ }
    }

    private void SavePersistentPresets()
    {
        try
        {
            var dir = Path.GetDirectoryName(PresetsPath());
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var dtos = _persistentPresets.Select(p => new PresetDto { Label = p.Label, ItemType = p.ItemType, ClassPath = p.ClassPath }).ToList();
            var json = JsonSerializer.Serialize(dtos, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(PresetsPath(), json);
        }
        catch { /* ignore */ }
    }

    private static string PresetsPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoD2MapViewer", "item_presets.json");

    private void SaveCurrentAsPreset()
    {
        var cls = _txtClass.Text.Trim();
        var type = _txtItemType.Text.Trim();
        if (cls.Length == 0 || type.Length == 0)
        {
            MessageBox.Show(this, "Item-Typ und Class-Path dürfen nicht leer sein.", "Preset speichern", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var label = Prompt("Bezeichnung für das Preset:", "Preset speichern", ShortName(cls));
        if (string.IsNullOrWhiteSpace(label)) return;
        // dedup
        if (_persistentPresets.Any(p => p.ItemType == type && p.ClassPath == cls))
        {
            MessageBox.Show(this, "Dieses Preset existiert bereits.", "Preset speichern", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _persistentPresets.Add(new Preset { Label = label.Trim(), ItemType = type, ClassPath = cls, Persistent = true });
        SavePersistentPresets();
        ResetPresets();
        // select the new one
        for (int i = 0; i < _cboPreset.Items.Count; i++)
            if (_cboPreset.Items[i] is Preset p && p.ItemType == type && p.ClassPath == cls) { _cboPreset.SelectedIndex = i; break; }
    }

    private void DeleteSelectedPreset()
    {
        if (_cboPreset.SelectedItem is not Preset p || !p.Persistent)
        {
            MessageBox.Show(this, "Bitte ein gespeichertes (persistentes) Preset auswählen.", "Preset löschen", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var r = MessageBox.Show(this, "Preset „" + p.Label + "\" wirklich löschen?", "Preset löschen", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes) return;
        _persistentPresets.RemoveAll(x => x.ItemType == p.ItemType && x.ClassPath == p.ClassPath);
        SavePersistentPresets();
        ResetPresets();
    }

    private void LoadPresetsFromAllSaves()
    {
        var saves = Savegame.FindSavegames();
        if (saves.Length == 0)
        {
            MessageBox.Show(this, "Keine Savegames gefunden.", "Presets aus allen Saves", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        int added = 0, scanned = 0, failed = 0;
        var seen = new HashSet<string>(_persistentPresets.Select(p => p.ItemType + "\n" + p.ClassPath), StringComparer.Ordinal);
        foreach (var f in saves)
        {
            scanned++;
            List<(string ItemType, string ClassPath)> items;
            try { items = Savegame.ReadItemClasses(f.FullName); }
            catch { failed++; continue; }
            foreach (var (itemType, classPath) in items)
            {
                var key = itemType + "\n" + classPath;
                if (!seen.Add(key)) continue;
                _persistentPresets.Add(new Preset { Label = ShortName(classPath), ItemType = itemType, ClassPath = classPath, Persistent = true });
                added++;
            }
        }
        SavePersistentPresets();
        ResetPresets();
        MessageBox.Show(this, $"Saves gescannt: {scanned} (fehlgeschlagen: {failed})\nNeue Presets hinzugefügt: {added}\nGesamt persistent: {_persistentPresets.Count}", "Presets aus allen Saves", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void LoadItemsFromSelectedSave(bool auto)
    {
        var sav = SelectedSave();
        if (sav == null)
        {
            if (!auto) MessageBox.Show(this, "Kein Savegame gewählt.", "Items aus Save", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            var items = Savegame.ReadItemClasses(sav);
            var manual = _cboPreset.Items.OfType<string>().FirstOrDefault(s => s == "(Manuell)");
            var selType = _txtItemType.Text.Trim();
            var selClass = _txtClass.Text.Trim();
            _cboPreset.BeginUpdate();
            try
            {
                // keep builtins, remove previous dynamic entries (Preset instances not in BuiltinPresets except identity)
                var keep = new List<object>();
                foreach (var it in _cboPreset.Items)
                {
                    if (it is Preset p && (BuiltinPresets.Contains(p) || p.Persistent)) keep.Add(it);
                }
                _cboPreset.Items.Clear();
                _seenPresetKeys.Clear();
                var keptPresets = new List<Preset>();
                foreach (var it in keep)
                    if (it is Preset p) { keptPresets.Add(p); _seenPresetKeys.Add(p.ItemType + "\n" + p.ClassPath); }
                int added = 0;
                foreach (var (itemType, classPath) in items)
                {
                    var key = itemType + "\n" + classPath;
                    if (!_seenPresetKeys.Add(key)) continue;
                    keptPresets.Add(new Preset { Label = $"[{itemType}] {ShortName(classPath)}", ItemType = itemType, ClassPath = classPath });
                    added++;
                }
                keptPresets.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
                foreach (var p in keptPresets) _cboPreset.Items.Add(p);
                if (manual != null) _cboPreset.Items.Add(manual); else _cboPreset.Items.Add("(Manuell)");
                // restore selection if possible
                int sel = -1;
                for (int i = 0; i < _cboPreset.Items.Count; i++)
                    if (_cboPreset.Items[i] is Preset p && p.ItemType == selType && p.ClassPath == selClass) { sel = i; break; }
                _cboPreset.SelectedIndex = sel >= 0 ? sel : 0;
            }
            finally { _cboPreset.EndUpdate(); }

            if (!auto)
            {
                UpdateInfo();
                var total = _cboPreset.Items.OfType<Preset>().Count();
                _lblInfo.Text = (_lblInfo.Text?.Length > 0 ? _lblInfo.Text + " | " : "") + $"{items.Count} Item-Klassen aus Save geladen ({total} auswählbar). Neue Items im Save erscheinen nach erneutem Laden.";
            }
        }
        catch (Exception ex)
        {
            if (!auto)
                MessageBox.Show(this, "Items konnten nicht aus dem Save gelesen werden:\n" + ex.Message, "Items aus Save", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void LoadSaves()
    {
        _saves = Savegame.FindSavegames();
        RebuildSaveCombo(selectCustom: false);
    }

    private void RebuildSaveCombo(bool selectCustom)
    {
        var keepCustomSelected = selectCustom || (_customSave != null && _cboSave.SelectedIndex == 0);
        var prevSavePath = keepCustomSelected ? null : SelectedSave();

        _cboSave.BeginUpdate();
        _cboSave.Items.Clear();
        if (_customSave != null)
            _cboSave.Items.Add(DescribeSave(_customSave, custom: true));
        foreach (var f in _saves)
            _cboSave.Items.Add(DescribeSave(f, custom: false));
        _cboSave.EndUpdate();

        if (_cboSave.Items.Count == 0)
        {
            _lblInfo.Text = "Keine Savegames gefunden. Nutze \"📁…\", um eine .sav-Datei auszuwählen.";
            _btnInject.Enabled = false;
            ResetPresets();
            return;
        }

        _btnInject.Enabled = true;
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
    }

    private static string DescribeSave(FileInfo f, bool custom)
    {
        var label = custom ? "[Custom] " : "";
        return $"{label}{f.Name}  ({f.LastWriteTime:yyyy-MM-dd HH:mm}, {f.Length / 1024} KB)";
    }

    private void BrowseSave()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Savegame (.sav) als Ziel auswählen",
            Filter = "Savegame (*.sav)|*.sav|Alle Dateien (*.*)|*.*",
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
            // Validate by parsing item classes/info (filename does not need to match SaveGame_*.sav)
            Savegame.ReadItemClasses(path);
            _customSave = new FileInfo(path);
            RebuildSaveCombo(selectCustom: true);
            _lblInfo.Text = "Custom-Save gewählt: " + path;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Die ausgewählte Datei konnte nicht als SoD2-Save gelesen werden:\n" + ex.Message,
                "Ungültiges Savegame", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void CopySelectedSave()
    {
        var sav = SelectedSave();
        if (sav == null)
        {
            MessageBox.Show(this, "Kein Savegame ausgewählt.", "Kopie erstellen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            var dir = Path.GetDirectoryName(sav) ?? "";
            var name = Path.GetFileNameWithoutExtension(sav);
            var ext = Path.GetExtension(sav);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var targetPath = Path.Combine(dir, $"{name}_copy_{stamp}{ext}");
            var counter = 1;
            while (File.Exists(targetPath))
                targetPath = Path.Combine(dir, $"{name}_copy_{stamp}_{counter++}{ext}");

            File.Copy(sav, targetPath, overwrite: false);
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
            UpdateInfo();
            LoadItemsFromSelectedSave(auto: true);
            _lblInfo.Text = "Kopie erstellt: " + Path.GetFileName(targetPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Kopie konnte nicht erstellt werden:\n" + ex.Message,
                "Kopie erstellen", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

    private string? SelectedSave()
    {
        var i = _cboSave.SelectedIndex;
        if (i < 0) return null;
        if (_customSave != null)
        {
            if (i == 0) return _customSave.FullName;
            i--;
        }
        return i >= 0 && i < _saves.Length ? _saves[i].FullName : null;
    }

    private static bool IsGameRunning()
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
            if (_customSave != null && string.Equals(_customSave.FullName, sav, StringComparison.OrdinalIgnoreCase))
                _customSave = new FileInfo(sav);

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
