using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using CUE4Parse.FileProvider;

namespace SoD2MapViewer;

/// <summary>UI: erzeugt und installiert ein Mod-Pak, das eine flache Betonplattform an der Spieler-Save-Position platziert.</summary>
public sealed class PlatformInjectDialog : Form
{
    private readonly DefaultFileProvider _provider;
    private readonly string _gameDir;

    private readonly TextBox _txtSizeX = new() { Width = 70, Text = "30" };
    private readonly TextBox _txtSizeY = new() { Width = 70, Text = "30" };
    private readonly TextBox _txtYaw = new() { Width = 70, Text = "0" };
    private readonly TextBox _txtHeight = new() { Width = 70, Text = "50" };
    private readonly TextBox _txtSave = new() { Width = 420, ReadOnly = true };
    private readonly Button _btnBrowseSave = new() { Text = "…", AutoSize = true };
    private readonly Button _btnRun = new() { Text = "Plattform erzeugen", AutoSize = true };
    private readonly Button _btnInstall = new() { Text = "In Spiel installieren", AutoSize = true, Enabled = false };
    private readonly Button _btnSavePak = new() { Text = "Pak nur erzeugen…", AutoSize = true, Enabled = false };
    private readonly Button _btnClose = new() { Text = "Schließen", AutoSize = true };
    private readonly TextBox _txtLog = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Dock = DockStyle.Fill,
        Font = new Font("Consolas", 9f),
        BackColor = Color.FromArgb(30, 32, 38),
        ForeColor = Color.Gainsboro,
    };

    private PlatformInjector.Result? _lastResult;
    private string? _customSave;

    public PlatformInjectDialog(DefaultFileProvider provider, string gameDir)
    {
        _provider = provider;
        _gameDir = gameDir;

        Text = "Plattform an Spieler-Position";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(720, 520);
        ClientSize = new Size(780, 560);
        Font = new Font("Segoe UI", 9f);

        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10), ColumnCount = 6 };
        for (int i = 0; i < 6; i++) top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        int row = 0;
        void L(string t, int c, int r) => top.Controls.Add(new Label { Text = t, AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 6, 6, 0) }, c, r);

        L("Savegame:", 0, row);
        var savePanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty };
        savePanel.Controls.Add(_txtSave);
        savePanel.Controls.Add(_btnBrowseSave);
        top.Controls.Add(savePanel, 1, row);
        top.SetColumnSpan(savePanel, 5);
        row++;

        L("Größe (m):", 0, row);
        var sizePanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty };
        sizePanel.Controls.Add(_txtSizeX);
        sizePanel.Controls.Add(new Label { Text = "x", AutoSize = true, Margin = new Padding(4, 6, 4, 0) });
        sizePanel.Controls.Add(_txtSizeY);
        sizePanel.Controls.Add(new Label { Text = "(Standard 30x30)", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(8, 6, 0, 0) });
        top.Controls.Add(sizePanel, 1, row);
        L("Yaw (°):", 2, row); top.Controls.Add(_txtYaw, 3, row);
        L("Höhenoffset (cm):", 4, row); top.Controls.Add(_txtHeight, 5, row);
        row++;

        var hint = new Label
        {
            Text = "Erzeugt ein Mod-Pak, das eine flache Betonplattform an der Spieler-Position im Save platziert (In-Place-Patch, Originalkarte unverändert). " +
                   "Hinweis: Zum Zurücksetzen das erzeugte/installierte Pak wieder aus dem Paks-Ordner entfernen.",
            AutoSize = true,
            MaximumSize = new Size(740, 0),
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 8, 0, 4),
        };
        top.Controls.Add(hint, 0, row);
        top.SetColumnSpan(hint, 6);
        row++;

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(10) };
        buttons.Controls.AddRange(new Control[] { _btnRun, _btnInstall, _btnSavePak, _btnClose });

        Controls.Add(_txtLog);
        Controls.Add(buttons);
        Controls.Add(top);

        AcceptButton = _btnRun;
        CancelButton = _btnClose;

        _btnBrowseSave.Click += (s, e) => BrowseSave();
        _btnRun.Click += (s, e) => RunPipeline(installToGame: false, saveOnly: false, interactive: true);
        _btnInstall.Click += (s, e) => InstallToGame();
        _btnSavePak.Click += (s, e) => SavePakAs();
        _btnClose.Click += (s, e) => Close();

        InitDefaultSave();
    }

    private void InitDefaultSave()
    {
        var (p, err) = PlatformInjector.ResolveSavePath(null);
        _txtSave.Text = p ?? ("(kein Save gefunden) " + (err ?? ""));
    }

    private void BrowseSave()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Savegame auswählen (.sav)",
            Filter = "Savegame (*.sav)|*.sav|Alle Dateien (*.*)|*.*",
            CheckFileExists = true,
        };
        try
        {
            var saves = Savegame.FindSavegames();
            if (saves.Length > 0) dlg.InitialDirectory = Path.GetDirectoryName(saves[0].FullName);
        }
        catch { }
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _customSave = Path.GetFullPath(dlg.FileName);
        _txtSave.Text = _customSave;
    }

    private static bool ParseFloat(string? s, out float v)
    {
        s = (s ?? "").Trim().Replace(',', '.');
        return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    private void RunPipeline(bool installToGame, bool saveOnly, bool interactive)
    {
        _lastResult = null;
        _btnInstall.Enabled = _btnSavePak.Enabled = false;
        _txtLog.Clear();
        Cursor = Cursors.WaitCursor;
        try
        {
            if (!ParseFloat(_txtSizeX.Text, out var sx) || !ParseFloat(_txtSizeY.Text, out var sy) || sx <= 0 || sy <= 0)
            { MessageBox.Show(this, "Ungültige Größe (m).", "Plattform", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (!ParseFloat(_txtYaw.Text, out var yaw)) yaw = 0f;
            if (!ParseFloat(_txtHeight.Text, out var h)) h = 50f;

            var save = _customSave;
            var res = PlatformInjector.Run(_provider, save, sx * 100f, sy * 100f, yaw, h, outPakPath: null);
            _lastResult = res;
            _txtLog.Lines = res.Log.ToArray();
            if (!res.Ok)
            {
                MessageBox.Show(this, "Fehler:\n" + res.Error + (res.Warning != null ? "\n\nWarnung:\n" + res.Warning : ""),
                    "Plattform", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            _btnInstall.Enabled = true;
            _btnSavePak.Enabled = true;

            var summary = $"Kachel: {res.TileName}\nKandidat: {res.Chosen?.ActorName} (Comp#{res.Chosen?.ComponentExport1Based})\n" +
                          $"Mesh: {res.Chosen?.MeshLabel}\nGröße: {sx:0.##}x{sy:0.##} m, Yaw {yaw:0.#}°, Höhenoffset {h:0.#} cm\n" +
                          $"Position lokal: ({res.Chosen?.FinalX:0.##},{res.Chosen?.FinalY:0.##},{res.Chosen?.FinalZ:0.##}) cm\n" +
                          $"Pak: {res.PakPath}\nEintrag: {res.PakEntryPath}" +
                          (res.Warning != null ? "\n\nWarnung: " + res.Warning : "") +
                          "\n\nHinweis: Zum Zurücksetzen das Pak aus dem Paks-Ordner entfernen.";
            MessageBox.Show(this, summary, "Plattform erzeugt", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private void InstallToGame()
    {
        if (_lastResult == null || !_lastResult.Ok || string.IsNullOrEmpty(_lastResult.PakPath) || !File.Exists(_lastResult.PakPath))
            return;
        var paksDir = Path.Combine(_gameDir, "Content", "Paks");
        if (!Directory.Exists(paksDir))
        {
            MessageBox.Show(this, "Paks-Ordner nicht gefunden:\n" + paksDir, "Installation", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        var targetName = PlatformInjector.DefaultInstallPakName(_lastResult);
        var targetPath = Path.Combine(paksDir, targetName);
        if (File.Exists(targetPath))
        {
            var ow = MessageBox.Show(this, $"Datei existiert bereits:\n{targetPath}\n\nÜberschreiben?", "Installation",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (ow != DialogResult.Yes) return;
        }
        else
        {
            var conf = MessageBox.Show(this, $"Pak nach\n{targetPath}\nkopieren?", "Installation bestätigen",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (conf != DialogResult.Yes) return;
        }
        try
        {
            File.Copy(_lastResult.PakPath, targetPath, overwrite: true);
            MessageBox.Show(this, "Installiert:\n" + targetPath + "\n\nHinweis: Zum Zurücksetzen diese Datei wieder löschen.",
                "Installation", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Installation fehlgeschlagen:\n" + ex.Message, "Installation", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SavePakAs()
    {
        if (_lastResult == null || !_lastResult.Ok || string.IsNullOrEmpty(_lastResult.PakPath) || !File.Exists(_lastResult.PakPath))
            return;
        using var dlg = new SaveFileDialog
        {
            Title = "Pak speichern unter",
            Filter = "Pak (*.pak)|*.pak|Alle Dateien (*.*)|*.*",
            FileName = Path.GetFileName(_lastResult.PakPath),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.Copy(_lastResult.PakPath, dlg.FileName, overwrite: true);
            MessageBox.Show(this, "Gespeichert:\n" + dlg.FileName, "Pak speichern", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Speichern fehlgeschlagen:\n" + ex.Message, "Pak speichern", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
