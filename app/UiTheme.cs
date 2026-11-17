using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SoD2MapViewer;

/// <summary>Central dark theme (colors, fonts, control factories) shared by the main form and all dialogs.</summary>
public static class UiTheme
{
    public static readonly Color Panel = Color.FromArgb(30, 32, 38);
    public static readonly Color Surface = Color.FromArgb(38, 40, 48);
    public static readonly Color SurfaceAlt = Color.FromArgb(32, 34, 40);
    public static readonly Color Text = Color.Gainsboro;
    public static readonly Color TextDim = Color.FromArgb(150, 155, 170);
    public static readonly Color Accent = Color.FromArgb(255, 200, 120);
    public static readonly Color Active = Color.FromArgb(80, 120, 170);
    public static readonly Color Danger = Color.FromArgb(220, 90, 90);

    public static readonly Font UiFont = new("Segoe UI", 9f);
    public static readonly Font DataFont = new("Consolas", 9f);
    public static readonly Font GroupFont = new("Segoe UI", 9f, FontStyle.Bold);

    /// <summary>Apply the common form background/font. Safe to call from any dialog constructor.</summary>
    public static void Apply(Form form)
    {
        form.BackColor = Surface;
        form.ForeColor = Text;
        form.Font = UiFont;
    }

    /// <summary>Recursively theme all controls of a dialog (one call instead of touching every control).</summary>
    public static void ApplyTree(Control root)
    {
        foreach (var c in Walk(root))
        {
            switch (c)
            {
                case TextBox tb:
                    tb.BackColor = Panel; tb.ForeColor = Text; tb.Font = DataFont; break;
                case ComboBox cb:
                    cb.BackColor = Panel; cb.ForeColor = Text; cb.Font = UiFont; cb.FlatStyle = FlatStyle.Flat; break;
                case Button b:
                    b.BackColor = Surface; b.ForeColor = Text; b.Font = UiFont; b.FlatStyle = FlatStyle.Flat; break;
                case CheckBox ch:
                    ch.ForeColor = ch.ForeColor == Color.Red ? Danger : Text; ch.Font = UiFont; break;
                case Label l:
                    l.ForeColor = l.ForeColor == Color.Red ? Danger : l.Font.Bold ? Accent : Text;
                    l.Font = l.Font.Bold ? GroupFont : UiFont;
                    break;
                case DataGridView dg:
                    dg.BackgroundColor = Panel; dg.ForeColor = Text; dg.Font = DataFont;
                    dg.ColumnHeadersDefaultCellStyle.BackColor = Surface;
                    dg.ColumnHeadersDefaultCellStyle.ForeColor = Text;
                    dg.DefaultCellStyle.BackColor = Panel;
                    dg.DefaultCellStyle.ForeColor = Text;
                    break;
                case FlowLayoutPanel or TableLayoutPanel:
                    if (c.BackColor != Color.Transparent)
                        c.BackColor = SurfaceAlt;
                    break;
                case Panel p when p.BackColor == Color.Transparent:
                    break;
                case Panel p:
                    p.BackColor = SurfaceAlt; break;
                case GroupBox g:
                    g.BackColor = Surface; g.ForeColor = Text; g.Font = UiFont; break;
            }
        }
    }

    private static IEnumerable<Control> Walk(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls)
            foreach (var c in Walk(child))
                yield return c;
    }

    public static Button Button(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Height = 30,
        FlatStyle = FlatStyle.Flat,
        BackColor = Surface,
        ForeColor = Text,
        Font = UiFont,
        Margin = new Padding(2, 2, 8, 2),
        Padding = new Padding(6, 0, 6, 0),
        Cursor = Cursors.Hand,
    };

    public static Label Label(string text, bool bold = false) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = bold ? Accent : TextDim,
        Font = bold ? GroupFont : UiFont,
        Margin = new Padding(4, 8, 4, 0),
    };

    public static CheckBox Check(string text, bool checkedInitially = false) => new()
    {
        Text = text,
        AutoSize = true,
        Checked = checkedInitially,
        ForeColor = Text,
        Font = UiFont,
        Margin = new Padding(4, 6, 8, 2),
        Padding = new Padding(0, 2, 0, 0),
    };

    public static Label InfoLabel(string text, int width) => new()
    {
        Text = text,
        AutoSize = false,
        Width = width,
        Dock = DockStyle.Left,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = TextDim,
        Font = UiFont,
        Padding = new Padding(8, 0, 2, 0),
    };

    public static TextBox Field(int width, string placeholder = "") => new()
    {
        Width = width,
        Height = 30,
        PlaceholderText = placeholder,
        Font = UiFont,
        BackColor = Panel,
        ForeColor = Text,
        BorderStyle = BorderStyle.FixedSingle,
        Margin = new Padding(2, 2, 8, 2),
    };

    private sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Surface;
        public override Color MenuStripGradientBegin => Surface;
        public override Color MenuStripGradientEnd => Surface;
        public override Color MenuItemBorder => Active;
        public override Color MenuItemSelected => Active;
        public override Color MenuItemSelectedGradientBegin => Active;
        public override Color MenuItemSelectedGradientEnd => Active;
        public override Color MenuItemPressedGradientBegin => Surface;
        public override Color MenuItemPressedGradientEnd => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color SeparatorDark => Panel;
        public override Color SeparatorLight => Color.FromArgb(60, 64, 74);
        public override Color CheckBackground => Active;
    }

    /// <summary>Re-apply grid colors after DataGridView re-creates its columns.</summary>
    public static void ApplyGrid(DataGridView dg)
    {
        dg.BackgroundColor = Panel;
        dg.ForeColor = Text;
        dg.Font = DataFont;
        dg.EnableHeadersVisualStyles = false;
        dg.ColumnHeadersDefaultCellStyle.BackColor = Surface;
        dg.ColumnHeadersDefaultCellStyle.ForeColor = Accent;
        dg.RowHeadersDefaultCellStyle.BackColor = Surface;
        dg.DefaultCellStyle.BackColor = Panel;
        dg.DefaultCellStyle.ForeColor = Text;
        dg.DefaultCellStyle.SelectionBackColor = Accent;
        dg.DefaultCellStyle.SelectionForeColor = Color.Black;
    }

    /// <summary>Shared renderer so all context menus use the same dark design.</summary>
    public static ToolStripRenderer MenuRenderer { get; } =
        new ToolStripProfessionalRenderer(new DarkMenuColors()) { RoundedEdges = false };

    public static ComboBox Combo(int width, params string[] items)
    {
        var cb = new ComboBox
        {
            Width = width,
            Height = 30,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = Panel,
            ForeColor = Text,
            Font = UiFont,
            Margin = new Padding(2, 2, 8, 2),
        };
        cb.Items.AddRange(items);
        return cb;
    }
}

/// <summary>Small cheat sheet for the viewer/editor shortcuts.</summary>
public sealed class ShortcutHelpDialog : Form
{
    private static (string Key, string Meaning)[] GetEntries() => new[]
    {
        ("M", L10n.T("help.move", "Modus: Objekte bewegen")),
        ("R", L10n.T("help.rotate", "Modus: Objekte drehen")),
        ("S", L10n.T("help.scale", "Modus: Objekte skalieren")),
        ("P", L10n.T("help.showCharacter", "Figur/Spielerposition anzeigen")),
        ("F", L10n.T("help.fit", "Ansicht einpassen (Fit)")),
        (L10n.T("keys.ctrlZ", "Strg+Z"), L10n.T("help.undo", "Letzte Änderung rückgängig machen")),
        (L10n.T("keys.del", "Entf"), L10n.T("help.delete", "Auswahl löschen")),
        (L10n.T("keys.esc", "Esc"), L10n.T("help.clearSelection", "Auswahl aufheben")),
        (L10n.T("keys.wheel", "Mausrad / Doppelklick"), L10n.T("help.zoom", "Zoomen")),
        (L10n.T("keys.drag", "Ziehen"), L10n.T("help.pan", "Ansicht verschieben (bzw. Objekt im Bearbeitungsmodus)")),
        (L10n.T("keys.ctrlClick", "Strg+Klick"), L10n.T("help.multiSelect", "Mehrfachauswahl umschalten")),
        (L10n.T("keys.shiftDrag", "Shift+Ziehen"), L10n.T("help.boxSelect", "Auswahlrechteck")),
        (L10n.T("keys.rightClick", "Rechtsklick"), L10n.T("help.rightClick", "Hausgruppe auswählen / Objektmenü / Position einfügen")),
        (L10n.T("keys.editCheck", "Bearbeiten-Checkbox"), L10n.T("help.editEnable", "Objekte ziehen erst aktivieren")),
    };

    public ShortcutHelpDialog()
    {
        UiTheme.Apply(this);
        Text = L10n.T("dialog.helpTitle", "Bedienung & Tastenkürzel");
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(470, 440);
        ClientSize = new Size(520, 470);

        var entries = GetEntries();
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = entries.Length + 1,
            Padding = new Padding(12),
            AutoScroll = true,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        table.Controls.Add(UiTheme.Label(L10n.T("help.key", "Taste"), bold: true), 0, 0);
        table.Controls.Add(UiTheme.Label(L10n.T("help.function", "Funktion"), bold: true), 1, 0);
        for (int i = 0; i < entries.Length; i++)
        {
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            var key = new Label
            {
                Text = entries[i].Key,
                AutoSize = true,
                Font = UiTheme.DataFont,
                ForeColor = UiTheme.Accent,
                Margin = new Padding(4, 6, 4, 2),
            };
            var desc = new Label
            {
                Text = entries[i].Meaning,
                AutoSize = true,
                Font = UiTheme.UiFont,
                ForeColor = UiTheme.Text,
                Margin = new Padding(4, 6, 4, 2),
            };
            table.Controls.Add(key, 0, i + 1);
            table.Controls.Add(desc, 1, i + 1);
        }
        var close = UiTheme.Button(L10n.T("dialog.close", "Schließen"));
        close.DialogResult = DialogResult.OK;
        close.Dock = DockStyle.Bottom;
        close.Height = 34;
        Controls.Add(table);
        Controls.Add(close);
        CancelButton = close;
        AcceptButton = close;
    }
}
