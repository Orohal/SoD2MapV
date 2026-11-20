using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SoD2MapViewer;

/// <summary>Kleiner Informationsdialog: Version prüfen, Link öffnen, Start-Prüfung umschalten.</summary>
internal sealed class UpdateDialog : Form
{
    private readonly SessionStateData _session;
    private readonly Action _onSettingChanged;
    private readonly Label _lblCurrent;
    private readonly Label _lblLatest;
    private readonly Label _lblStatus;
    private readonly CheckBox _chkStartup;
    private readonly Button _btnCheck;
    private readonly Button _btnOpen;
    private UpdateInfo? _info;

    public UpdateDialog(SessionStateData session, Action onSettingChanged,
                        UpdateInfo? prefetched = null, bool checkImmediately = false)
    {
        _session = session;
        _onSettingChanged = onSettingChanged;

        Text = "Update prüfen";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(420, 235);
        UiTheme.Apply(this);

        _lblCurrent = UiTheme.Label($"Aktuelle Version: v{UpdateChecker.CurrentVersion}");
        _lblCurrent.Location = new Point(20, 18);
        _lblLatest = UiTheme.Label("Neueste Version: noch nicht geprüft");
        _lblLatest.Location = new Point(20, 44);
        _lblStatus = UiTheme.Label("");
        _lblStatus.Location = new Point(20, 70);
        _lblStatus.MaximumSize = new Size(380, 60);

        _chkStartup = UiTheme.Check("Beim Start nach Updates suchen", session.CheckUpdatesOnStartup);
        _chkStartup.Location = new Point(20, 120);
        _chkStartup.CheckedChanged += (_, _) =>
        {
            _session.CheckUpdatesOnStartup = _chkStartup.Checked;
            _onSettingChanged();
        };

        _btnCheck = UiTheme.Button("Jetzt prüfen");
        _btnCheck.Location = new Point(20, 158);
        _btnCheck.Click += async (_, _) => await RunCheck();

        _btnOpen = UiTheme.Button("Release-Seite öffnen");
        _btnOpen.Location = new Point(150, 158);
        _btnOpen.Enabled = false;
        _btnOpen.Click += (_, _) =>
        {
            if (_info == null) return;
            try
            {
                Process.Start(new ProcessStartInfo(_info.HtmlUrl) { UseShellExecute = true });
            }
            catch (Exception ex) { _lblStatus.Text = "Konnte Browser nicht öffnen: " + ex.Message; }
        };

        var btnClose = UiTheme.Button("Schließen");
        btnClose.Location = new Point(310, 158);
        btnClose.Click += (_, _) => Close();

        Controls.AddRange(new Control[] { _lblCurrent, _lblLatest, _lblStatus,
                                          _chkStartup, _btnCheck, _btnOpen, btnClose });
        if (prefetched != null) ApplyInfo(prefetched, null);
        if (checkImmediately) Shown += async (_, _) => await RunCheck();
    }

    private void ApplyInfo(UpdateInfo? info, string? error)
    {
        if (info == null)
        {
            _lblLatest.Text = "Neueste Version: nicht verfügbar";
            _lblStatus.Text = "Prüfung fehlgeschlagen (offline?): " + error;
            return;
        }
        _info = info;
        _lblLatest.Text = $"Neueste Version: v{info.LatestVersion}";
        _btnOpen.Enabled = true;
        _lblStatus.Text = UpdateChecker.IsNewer(info.LatestVersion, UpdateChecker.CurrentVersion)
            ? "Neuere Version verfügbar — Download erfolgt auf der Release-Seite."
            : "Du bist auf dem neuesten Stand.";
    }

    private async Task RunCheck()
    {
        _btnCheck.Enabled = false;
        _lblStatus.Text = "Prüfe GitHub …";
        var (info, error) = await UpdateChecker.CheckAsync();
        _btnCheck.Enabled = true;
        ApplyInfo(info, error);
    }
}
