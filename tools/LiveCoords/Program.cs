using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace LiveCoords;

/// <summary>
/// Standalone in-game coordinate overlay (skeleton). The position source will be
/// connected after REA identifies the player's coordinate fields in the game process.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
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
            return;
        }

        Application.Run(new OverlayForm());
    }
}

internal sealed class OverlayForm : Form
{
    private readonly Label _coords;
    private readonly Label _hint;
    private readonly System.Windows.Forms.Timer _timer;

    public OverlayForm()
    {
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
            Text = "LiveCoords 0.1 — Position quasi REA",
            ForeColor = Color.FromArgb(180, 200, 210),
            BackColor = Color.FromArgb(200, 20, 24, 30),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 8, 0),
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Beenden", null, (_, _) => Close());

        ContextMenuStrip = menu;
        Controls.Add(_hint);
        Controls.Add(_coords);

        _timer = new System.Windows.Forms.Timer { Interval = 250 };
        _timer.Tick += (_, _) => UpdateCoords();
        _timer.Start();
    }

    private void UpdateCoords()
    {
        // Placeholder until the REA-discovered memory reader is wired in.
        _coords.Text = "--,-- m  (N/O)";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
