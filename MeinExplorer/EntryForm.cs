using System.Collections;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace MeinExplorer;

internal sealed class EntryForm : Form
{
    private readonly TextBox name = new();
    private readonly TextBox path = new();
    public string EntryName => name.Text.Trim();
    public string EntryPath => path.Text.Trim();

    public EntryForm(string? initialName = null, string? initialPath = null)
    {
        Text = initialName is null ? "Favorit hinzufügen" : "Favorit bearbeiten";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ShowInTaskbar = false;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(540, 220);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(250, 251, 254);
        Controls.Add(new Label { Text = "Anzeigename", Location = new Point(20, 18), AutoSize = true });
        name.SetBounds(20, 44, 495, 30);
        name.Text = initialName ?? "";
        Controls.Add(name);
        Controls.Add(new Label { Text = "Verzeichnis", Location = new Point(20, 87), AutoSize = true });
        path.SetBounds(20, 112, 397, 30);
        path.Text = initialPath ?? "";
        Controls.Add(path);
        var browse = new Button { Text = "Wählen…" };
        browse.SetBounds(427, 111, 88, 32);
        browse.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog { Description = "Verzeichnis auswählen", UseDescriptionForTitle = true };
            if (Directory.Exists(path.Text)) picker.SelectedPath = path.Text;
            if (picker.ShowDialog(this) == DialogResult.OK)
            {
                path.Text = picker.SelectedPath;
                if (string.IsNullOrWhiteSpace(name.Text)) name.Text = System.IO.Path.GetFileName(picker.SelectedPath.TrimEnd('\\'));
            }
        };
        Controls.Add(browse);
        var cancel = new Button { Text = "Abbrechen", DialogResult = DialogResult.Cancel };
        cancel.SetBounds(295, 169, 105, 34);
        Controls.Add(cancel);
        var ok = new Button { Text = "Speichern", BackColor = Color.FromArgb(39, 101, 215), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        ok.FlatAppearance.BorderSize = 0;
        ok.SetBounds(410, 169, 105, 34);
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(EntryName) || string.IsNullOrWhiteSpace(EntryPath))
            {
                MessageBox.Show(this, "Bitte einen Namen und einen Ordnerpfad eingeben.");
                return;
            }
            if (!Directory.Exists(EntryPath))
            {
                MessageBox.Show(this, "Das Verzeichnis existiert nicht. Bitte einen gültigen Pfad wählen.",
                    "Ungültiges Verzeichnis", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DialogResult = DialogResult.OK;
        };
        Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}

