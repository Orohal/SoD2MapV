using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LiveCoords;

/// <summary>
/// Standalone in-game coordinate overlay (skeleton). The position source will be
/// connected after REA identifies the player's coordinate fields in the game process.
/// </summary>
internal static class Program
{
    /// <summary>Find the running game process (Shipping EXE, not the elevated loader).</summary>
    private static int FindGamePid()
    {
        // Prefer the actual game binary; the launcher "StateOfDecay2.exe" runs
        // elevated and OpenProcess would fail with access denied.
        var game = Process.GetProcessesByName("StateOfDecay2-Win64-Shipping")
                          .OrderByDescending(p => p.StartTime).FirstOrDefault();
        if (game != null) return game.Id;
        var loader = Process.GetProcessesByName("StateOfDecay2")
                            .OrderByDescending(p => p.StartTime).FirstOrDefault();
        return loader?.Id ?? 0;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // Headless memory scan: LiveCoords.exe --scan <pid> <x> <y> <z>
        if (args.Length == 5 && args[0] == "--scan")
        {
            int pid = int.Parse(args[1], CultureInfo.InvariantCulture);
            float x = float.Parse(args[2], CultureInfo.InvariantCulture);
            float y = float.Parse(args[3], CultureInfo.InvariantCulture);
            float z = float.Parse(args[4], CultureInfo.InvariantCulture);
            var hits = MemoryScanner.FindFloatTriple(pid, x, y, z);
            Console.WriteLine($"[scan] {hits.Count} Treffer für ({x:0.##}, {y:0.##}, {z:0.##}) cm");
            foreach (var hit in hits)
            {
                Console.WriteLine($"[scan] 0x{hit.Address:X} Nachbarn: {string.Join(" | ", Array.ConvertAll(hit.Neighbours, v => v.ToString("0.##", CultureInfo.InvariantCulture)))}");
            }
            return 0;
        }

        // Tolerant scan: LiveCoords.exe --scannear <pid> <x> <y> <z> <tolCm> [maxHits]
        if (args.Length >= 6 && args[0] == "--scannear")
        {
            int pid = int.Parse(args[1], CultureInfo.InvariantCulture);
            float x = float.Parse(args[2], CultureInfo.InvariantCulture);
            float y = float.Parse(args[3], CultureInfo.InvariantCulture);
            float z = float.Parse(args[4], CultureInfo.InvariantCulture);
            float tol = float.Parse(args[5], CultureInfo.InvariantCulture);
            int maxHits = args.Length > 6 ? int.Parse(args[6], CultureInfo.InvariantCulture) : 5000;
            var hits = MemoryScanner.FindNearTriple(pid, x, y, z, tol, maxHits);
            Console.WriteLine($"[scannear] {hits.Count} Treffer (±{tol:0.#} cm) für ({x:0.##}, {y:0.##}, {z:0.##})");
            foreach (var hit in hits)
            {
                Console.WriteLine($"[scannear] 0x{hit.Address:X}  Nachbarn: {string.Join(" | ", Array.ConvertAll(hit.Neighbours, v => v.ToString("0.##", CultureInfo.InvariantCulture)))}");
            }
            return 0;
        }

        // Auto-rescan: LiveCoords.exe --auto <pid> <x> <y> <z> <tolCm>
        if (args.Length >= 6 && args[0] == "--auto")
        {
            int pid = int.Parse(args[1], CultureInfo.InvariantCulture);
            float x = float.Parse(args[2], CultureInfo.InvariantCulture);
            float y = float.Parse(args[3], CultureInfo.InvariantCulture);
            float z = float.Parse(args[4], CultureInfo.InvariantCulture);
            float tol = float.Parse(args[5], CultureInfo.InvariantCulture);
            var found = MemoryScanner.FindPlayerCluster(pid, x, y, z, tol);
            if (found == null)
            {
                Console.Error.WriteLine("[auto] kein Player-Cluster gefunden");
                return 3;
            }
            Console.WriteLine($"[auto] Player-Cluster bei 0x{found.Value:X}");
            Application.Run(new OverlayForm(pid, found.Value));
            return 0;
        }

        // Live overlay: LiveCoords.exe --addr <hexAddress> [pid]
        if (args.Length >= 2 && args[0] == "--addr")
        {
            long address = Convert.ToInt64(args[1].Replace("0x", ""), 16);
            int pid = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : FindGamePid();
            if (pid == 0)
            {
                Console.Error.WriteLine("[addr] kein SoD2-Prozess gefunden");
                return 1;
            }
            Application.Run(new OverlayForm(pid, address));
            return 0;
        }

        Application.Run(new OverlayForm(0, 0));
        return 0;
    }
}

internal sealed class OverlayForm : Form
{
    private readonly Label _coords;
    private readonly Label _hint;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly int _pid;
    private readonly long _address;
    private bool _keepTopMost;

    public OverlayForm(int pid, long address)
    {
        _pid = pid;
        _address = address;
        Text = "LiveCoords";
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        Opacity = 0.9;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(20, 20);
        Size = new Size(280, 74);
        Font = new Font("Consolas", 10f);

        _coords = new Label
        {
            Text = "--,-- m  (N/O)",
            ForeColor = Color.FromArgb(255, 220, 150),
            BackColor = Color.FromArgb(200, 20, 24, 30),
            Dock = DockStyle.Top,
            Height = 34,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 8, 0),
        };
        _hint = new Label
        {
            Text = $"LiveCoords 0.1 — PID {pid} @ 0x{address:X}",
            ForeColor = Color.FromArgb(180, 200, 210),
            BackColor = Color.FromArgb(200, 20, 24, 30),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 8, 0),
        };

        var menu = new ContextMenuStrip();
        var topItem = new ToolStripMenuItem("Immer oben: AUS") { CheckOnClick = true };
        topItem.CheckedChanged += (_, _) =>
        {
            _keepTopMost = topItem.Checked;
            topItem.Text = _keepTopMost ? "Immer oben: AN" : "Immer oben: AUS";
            TopMost = _keepTopMost;
            if (!_keepTopMost)
                SetWindowPos(Handle, new IntPtr(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040); // HWND_NOTOPMOST
        };
        menu.Items.Add(topItem);
        menu.Items.Add("Beenden", null, (_, _) => Close());

        ContextMenuStrip = menu;
        Controls.Add(_hint);
        Controls.Add(_coords);

        _timer = new System.Windows.Forms.Timer { Interval = 250 };
        _timer.Tick += (_, _) => UpdateCoords();
        _timer.Start();
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private void ReassertTopMost()
    {
        // HWND_TOPMOST = -1; SWP_NOSIZE | SWP_NOMOVE | SWP_SHOWWINDOW
        SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);
    }

    private static readonly string[] Compass =
    {
        "N", "NNO", "NO", "ONO", "O", "OSO", "SO", "SSO",
        "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW",
    };

    private static string Direction(float x, float y)
    {
        if (MathF.Abs(x) < 1f && MathF.Abs(y) < 1f) return "–";
        double deg = Math.Atan2(y, x) * 180.0 / Math.PI;
        if (deg < 0) deg += 360.0;
        return Compass[(int)Math.Round(deg / 22.5) % 16];
    }

    private void UpdateCoords()
    {
        // TopMost re-assert is opt-in so the overlay can be sent behind windows / closed.
        if (_keepTopMost) ReassertTopMost();
        try
        {
            var v = MemoryScanner.ReadTriple(_pid, _address);
            float xm = v[0] / 100f, ym = v[1] / 100f, zm = v[2] / 100f;
            _coords.Text = $"N {xm,9:0.#} m   O {ym,9:0.#} m   H {zm,7:0.#} m  ({Direction(v[0], v[1])})";
        }
        catch (Exception e)
        {
            _coords.Text = "Fehler: " + e.Message;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
