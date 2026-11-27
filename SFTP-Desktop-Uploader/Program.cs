using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using Renci.SshNet;

namespace SftpDesktopUploader;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args));
    }
}

internal sealed class SftpSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string UserName { get; set; } = "";
    public string EncryptedPassword { get; set; } = "";
    // Only for files dropped onto the desktop shortcut (or before browsing).
    public string RemoteDirectory { get; set; } = "/";
    public string HostKeySha256 { get; set; } = "";

    [JsonIgnore]
    public string Password
    {
        get
        {
            if (string.IsNullOrEmpty(EncryptedPassword)) return "";
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(EncryptedPassword), null,
                DataProtectionScope.CurrentUser));
        }
        set => EncryptedPassword = Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }

    public bool IsComplete => !string.IsNullOrWhiteSpace(Host) &&
                              !string.IsNullOrWhiteSpace(UserName) &&
                              !string.IsNullOrEmpty(EncryptedPassword);

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SftpDesktopUploader", "settings.json");

    public static SftpSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<SftpSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Einstellungen konnten nicht geladen werden:\n" + ex.Message,
                "SFTP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this,
            new JsonSerializerOptions { WriteIndented = true }));
    }
}

internal sealed record RemoteEntry(string Name, string FullPath, bool IsDirectory, long Size, DateTime Modified);

internal sealed class MainForm : Form
{
    private SftpSettings settings = SftpSettings.Load();
    private readonly string[] startupFiles;
    private string currentFolder = "/";
    private bool browserConnected;
    private bool busy;

    private readonly ListView fileList = new();
    private readonly Label pathLabel = new();
    private readonly Label statusLabel = new();
    private readonly Label serverLabel = new();
    private readonly Button connectButton = NewButton("Verbinden", 100);
    private readonly Button settingsButton = NewButton("Einstellungen", 120);
    private readonly Button upButton = NewButton("↑ Hoch", 80);
    private readonly Button rootButton = NewButton("⌂ Hauptverzeichnis", 155);
    private readonly Button refreshButton = NewButton("Aktualisieren", 110);
    private readonly Button uploadButton = NewButton("↑ Upload", 100);
    private readonly Button downloadButton = NewButton("↓ Download", 115);
    private readonly Button deleteButton = NewButton("Löschen", 90);

    public MainForm(string[] startupFiles)
    {
        this.startupFiles = startupFiles;
        Text = "SFTP Desktop Uploader – Dateimanager";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1010, 660);
        MinimumSize = new Size(760, 480);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(246, 249, 253);
        AllowDrop = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 75));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        Controls.Add(layout);

        var header = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(18, 48, 92) };
        var title = new Label
        {
            Text = "SFTP Dateimanager",
            Font = new Font("Segoe UI", 17, FontStyle.Bold),
            ForeColor = Color.White,
            Location = new Point(18, 7),
            AutoSize = true
        };
        serverLabel.ForeColor = Color.FromArgb(196, 224, 255);
        serverLabel.Location = new Point(20, 44);
        serverLabel.AutoSize = true;
        header.Controls.Add(title);
        header.Controls.Add(serverLabel);
        layout.Controls.Add(header, 0, 0);

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 10, 6, 5),
            WrapContents = false,
            AutoScroll = true
        };
        toolbar.Controls.AddRange(new Control[]
        {
            connectButton, settingsButton, upButton, rootButton,
            refreshButton, uploadButton, downloadButton, deleteButton
        });
        layout.Controls.Add(toolbar, 0, 1);

        var address = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        var addressTitle = new Label { Text = "Serverpfad:", AutoSize = true, Location = new Point(17, 10) };
        pathLabel.AutoSize = false;
        pathLabel.Location = new Point(125, 9);
        pathLabel.Size = new Size(850, 25);
        pathLabel.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        pathLabel.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        address.Controls.Add(addressTitle);
        address.Controls.Add(pathLabel);
        layout.Controls.Add(address, 0, 2);

        fileList.Dock = DockStyle.Fill;
        fileList.View = View.Details;
        fileList.FullRowSelect = true;
        fileList.MultiSelect = true;
        fileList.HideSelection = false;
        fileList.GridLines = false;
        fileList.AllowDrop = true;
        fileList.Columns.Add("Name", 430);
        fileList.Columns.Add("Typ", 145);
        fileList.Columns.Add("Größe", 145);
        fileList.Columns.Add("Geändert", 205);
        fileList.DoubleClick += async (_, _) => await OpenSelectedDirectoryAsync();
        fileList.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                await OpenSelectedDirectoryAsync();
            }
            if (e.KeyCode == Keys.Delete)
            {
                e.Handled = true;
                await DeleteSelectedAsync();
            }
        };
        layout.Controls.Add(fileList, 0, 3);

        statusLabel.Dock = DockStyle.Fill;
        statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        statusLabel.Padding = new Padding(14, 0, 0, 0);
        statusLabel.BackColor = Color.FromArgb(231, 239, 250);
        layout.Controls.Add(statusLabel, 0, 4);

        connectButton.Click += async (_, _) => await ConnectAndBrowseAsync();
        settingsButton.Click += (_, _) => OpenSettings();
        upButton.Click += async (_, _) => await BrowseAsync(ParentFolder(currentFolder));
        rootButton.Click += async (_, _) => await BrowseAsync("/");
        refreshButton.Click += async (_, _) => await BrowseAsync(currentFolder);
        uploadButton.Click += async (_, _) => await PickFilesAndUploadAsync();
        downloadButton.Click += async (_, _) => await DownloadSelectedAsync();
        deleteButton.Click += async (_, _) => await DeleteSelectedAsync();

        // In-window file drops upload to the current folder. Desktop shortcut drops
        // are received as command-line arguments and use RemoteDirectory instead.
        DragEnter += FileDragEnter;
        fileList.DragEnter += FileDragEnter;
        DragDrop += async (_, e) => await HandleDroppedFilesAsync(e);
        fileList.DragDrop += async (_, e) => await HandleDroppedFilesAsync(e);

        SetStatus("Bereit. Über 'Verbinden' das Serververzeichnis anzeigen.");
        UpdateUi();

        Shown += async (_, _) =>
        {
            if (startupFiles.Length > 0)
            {
                // Important: existing desktop shortcut workflow unchanged.
                await UploadFilesAsync(startupFiles, settings.RemoteDirectory, false);
            }
            else if (settings.IsComplete)
            {
                await ConnectAndBrowseAsync();
            }
            else
            {
                OpenSettings();
                if (settings.IsComplete)
                    await ConnectAndBrowseAsync();
            }
        };
    }

    private static Button NewButton(string text, int width) => new()
    {
        Text = text,
        Width = width,
        Height = 34,
        Margin = new Padding(3, 0, 3, 0),
        FlatStyle = FlatStyle.System
    };

    private void SetStatus(string value) => statusLabel.Text = value;

    private void UpdateUi()
    {
        serverLabel.Text = settings.IsComplete
            ? $"{settings.Host}:{settings.Port} · {settings.UserName}"
            : "Noch keine Verbindung eingerichtet";
        pathLabel.Text = browserConnected ? currentFolder : "Nicht verbunden";
        bool canBrowse = browserConnected && !busy;
        connectButton.Enabled = !busy;
        settingsButton.Enabled = !busy;
        upButton.Enabled = canBrowse && currentFolder != "/";
        rootButton.Enabled = canBrowse;
        refreshButton.Enabled = canBrowse;
        uploadButton.Enabled = canBrowse;
        downloadButton.Enabled = canBrowse;
        deleteButton.Enabled = canBrowse;
        fileList.Enabled = !busy;
    }

    private void OpenSettings()
    {
        if (busy) return;
        using var dialog = new SettingsForm(settings);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            dialog.Result.Save();
            settings = dialog.Result;
            browserConnected = false;
            currentFolder = "/";
            fileList.Items.Clear();
            SetStatus("Einstellungen gespeichert. Bitte erneut verbinden.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Einstellungen", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        UpdateUi();
    }

    // The first use of a server asks the user to verify its SSH fingerprint.
    // Subsequent connections reject any unexpected change automatically.
    private bool VerifyHostKey(SftpSettings config, string fingerprint)
    {
        var actual = NormalizeFingerprint(fingerprint);
        if (!string.IsNullOrWhiteSpace(config.HostKeySha256))
        {
            var expected = NormalizeFingerprint(config.HostKeySha256);
            bool matches = CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expected));
            if (!matches)
            {
                MessageBox.Show(this,
                    "Der SSH-Server-Fingerabdruck hat sich geändert!\n\n" +
                    $"Gespeichert: SHA256:{expected}\nAktuell: SHA256:{actual}\n\n" +
                    "Die Verbindung wird aus Sicherheitsgründen blockiert. " +
                    "Bitte beim Serveranbieter prüfen.",
                    "SSH-Sicherheitswarnung", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return matches;
        }

        var choice = MessageBox.Show(this,
            $"Erste Verbindung zu {config.Host}:{config.Port}.\n\n" +
            $"Server-Fingerabdruck:\nSHA256:{actual}\n\n" +
            "Vergleiche ihn vor dem Bestätigen mit einer vertrauenswürdigen " +
            "Angabe des Serveranbieters oder Administrators.\n\n" +
            "Ist der Fingerabdruck korrekt und soll er gespeichert werden?",
            "SSH-Server bestätigen", MessageBoxButtons.YesNo,
            MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (choice != DialogResult.Yes) return false;

        try
        {
            config.HostKeySha256 = "SHA256:" + actual;
            config.Save();
            return true;
        }
        catch (Exception ex)
        {
            config.HostKeySha256 = "";
            MessageBox.Show(this, "Fingerabdruck konnte nicht gespeichert werden:\n" + ex.Message,
                "SFTP", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private SftpClient CreateClient(SftpSettings config)
    {
        string host = config.Host.Trim();
        if (host.Contains('/') || host.Contains('\\'))
            throw new ArgumentException("Bitte nur den Servernamen eingeben, ohne Protokoll oder Pfad.");

        var client = new SftpClient(host, config.Port, config.UserName, config.Password);
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(30);
        client.HostKeyReceived += (_, e) =>
        {
            string fingerprint = Convert.ToBase64String(SHA256.HashData(e.HostKey)).TrimEnd('=');
            bool trusted = false;
            if (IsHandleCreated && !IsDisposed)
            {
                // Operations run on a worker thread; Windows dialogs must use UI thread.
                Invoke((Action)(() => trusted = VerifyHostKey(config, fingerprint)));
            }
            e.CanTrust = trusted;
        };
        return client;
    }

    private static string NormalizeFingerprint(string value)
    {
        string result = value.Trim();
        if (result.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
            result = result[7..];
        return result.TrimEnd('=');
    }

    private static string NormalizeDirectory(string path)
    {
        string[] parts = (path ?? "/").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>();
        foreach (var part in parts)
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
            }
            else stack.Add(part);
        }
        return "/" + string.Join("/", stack);
    }

    private static string ParentFolder(string path)
    {
        path = NormalizeDirectory(path);
        int slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }

    private static string CombineRemote(string folder, string name) =>
        NormalizeDirectory(folder).TrimEnd('/') + "/" + name;

    private async Task ExecuteAsync(Func<Task> operation)
    {
        if (busy) return;
        busy = true;
        UpdateUi();
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            SetStatus("Fehler: " + ex.Message);
            MessageBox.Show(this, ex.Message, "SFTP-Fehler", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            busy = false;
            UpdateUi();
        }
    }

    private Task ConnectAndBrowseAsync()
    {
        if (!settings.IsComplete)
        {
            OpenSettings();
            if (!settings.IsComplete) return Task.CompletedTask;
        }
        return BrowseAsync("/"); // SFTP account's root, typically chroot on shared hosting.
    }

    private Task BrowseAsync(string path) => ExecuteAsync(async () =>
    {
        var target = NormalizeDirectory(path);
        SetStatus("Lese Serververzeichnis " + target + " ...");
        var config = settings;
        var entries = await Task.Run(() =>
        {
            using var client = CreateClient(config);
            client.Connect();
            try
            {
                return client.ListDirectory(target)
                    .Where(f => f.Name != "." && f.Name != "..")
                    .Select(f => new RemoteEntry(f.Name, f.FullName, f.IsDirectory,
                        f.Attributes.Size, f.LastWriteTime))
                    .OrderByDescending(f => f.IsDirectory)
                    .ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            finally { if (client.IsConnected) client.Disconnect(); }
        });

        currentFolder = target;
        browserConnected = true;
        fileList.BeginUpdate();
        try
        {
            fileList.Items.Clear();
            foreach (var entry in entries)
            {
                var item = new ListViewItem(entry.IsDirectory ? "📁 " + entry.Name : entry.Name)
                {
                    Tag = entry
                };
                item.SubItems.Add(entry.IsDirectory ? "Ordner" : "Datei");
                item.SubItems.Add(entry.IsDirectory ? "" : FormatSize(entry.Size));
                item.SubItems.Add(entry.Modified.ToString("dd.MM.yyyy HH:mm"));
                fileList.Items.Add(item);
            }
        }
        finally { fileList.EndUpdate(); }
        SetStatus($"Verbunden · {entries.Count} Einträge · Doppelklick öffnet einen Ordner.");
    });

    private static string FormatSize(long size)
    {
        if (size < 1024) return size + " B";
        if (size < 1024 * 1024) return (size / 1024d).ToString("N1") + " KB";
        if (size < 1024L * 1024 * 1024) return (size / (1024d * 1024)).ToString("N1") + " MB";
        return (size / (1024d * 1024 * 1024)).ToString("N1") + " GB";
    }

    private async Task OpenSelectedDirectoryAsync()
    {
        if (busy || fileList.SelectedItems.Count != 1) return;
        if (fileList.SelectedItems[0].Tag is RemoteEntry entry && entry.IsDirectory)
            await BrowseAsync(entry.FullPath);
    }

    private void FileDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async Task HandleDroppedFilesAsync(DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths) return;
        await UploadFilesAsync(paths, browserConnected ? currentFolder : settings.RemoteDirectory, browserConnected);
    }

    private async Task PickFilesAndUploadAsync()
    {
        if (!browserConnected || busy) return;
        using var dialog = new OpenFileDialog
        {
            Title = "Dateien für den SFTP-Upload auswählen",
            Filter = "Alle Dateien (*.*)|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            await UploadFilesAsync(dialog.FileNames, currentFolder, true);
    }

    private async Task UploadFilesAsync(IEnumerable<string> filePaths, string destination, bool refreshBrowser)
    {
        var paths = filePaths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0)
        {
            MessageBox.Show(this, "Bitte eine oder mehrere Dateien auswählen. Ordner werden nicht hochgeladen.",
                "SFTP", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!settings.IsComplete)
        {
            OpenSettings();
            if (!settings.IsComplete) return;
        }

        destination = NormalizeDirectory(destination);
        var config = settings;
        await ExecuteAsync(async () =>
        {
            var errors = new List<string>();
            int uploaded = 0;
            foreach (string file in paths)
            {
                SetStatus("Lade hoch: " + Path.GetFileName(file));
                try
                {
                    await Task.Run(() =>
                    {
                        using var client = CreateClient(config);
                        client.Connect();
                        try
                        {
                            if (!client.Exists(destination))
                                throw new DirectoryNotFoundException("Serververzeichnis nicht gefunden: " + destination);
                            using var input = File.OpenRead(file);
                            client.UploadFile(input, CombineRemote(destination, Path.GetFileName(file)), true);
                        }
                        finally { if (client.IsConnected) client.Disconnect(); }
                    });
                    uploaded++;
                }
                catch (Exception ex) { errors.Add(Path.GetFileName(file) + ": " + ex.Message); }
            }
            SetStatus($"Upload abgeschlossen: {uploaded} erfolgreich, {errors.Count} Fehler. Ziel: {destination}");
            MessageBox.Show(this,
                $"Zielordner: {destination}\nErfolgreich: {uploaded}\nFehler: {errors.Count}" +
                (errors.Count == 0 ? "\n\nGleichnamige Dateien wurden überschrieben." :
                 "\n\n" + string.Join("\n", errors)),
                "SFTP-Upload", MessageBoxButtons.OK,
                errors.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        });
        if (refreshBrowser && browserConnected) await BrowseAsync(currentFolder);
    }

    private List<RemoteEntry> SelectedFiles() => fileList.SelectedItems
        .Cast<ListViewItem>()
        .Select(i => i.Tag)
        .OfType<RemoteEntry>()
        .Where(e => !e.IsDirectory)
        .ToList();

    private async Task DownloadSelectedAsync()
    {
        if (!browserConnected || busy) return;
        var files = SelectedFiles();
        if (files.Count == 0)
        {
            MessageBox.Show(this, "Bitte eine oder mehrere Dateien markieren. Downloads ganzer Ordner werden nicht unterstützt.",
                "SFTP", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var folder = new FolderBrowserDialog { Description = "Lokalen Download-Ordner auswählen" };
        if (folder.ShowDialog(this) != DialogResult.OK) return;
        string localFolder = folder.SelectedPath;
        var config = settings;

        await ExecuteAsync(async () =>
        {
            int downloaded = 0;
            var errors = new List<string>();
            foreach (var file in files)
            {
                SetStatus("Lade herunter: " + file.Name);
                var localPath = Path.Combine(localFolder, file.Name);
                if (File.Exists(localPath) && MessageBox.Show(this,
                        $"Die lokale Datei '{file.Name}' existiert bereits. Überschreiben?",
                        "Datei vorhanden", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    continue;
                try
                {
                    await Task.Run(() =>
                    {
                        // Temporary file prevents a failed download from truncating the destination.
                        string temp = Path.Combine(localFolder, ".sftp-" + Guid.NewGuid().ToString("N") + ".tmp");
                        try
                        {
                            using var client = CreateClient(config);
                            client.Connect();
                            try
                            {
                                using (var output = File.Create(temp))
                                    client.DownloadFile(file.FullPath, output);
                            }
                            finally { if (client.IsConnected) client.Disconnect(); }
                            File.Move(temp, localPath, true);
                        }
                        finally { if (File.Exists(temp)) File.Delete(temp); }
                    });
                    downloaded++;
                }
                catch (Exception ex) { errors.Add(file.Name + ": " + ex.Message); }
            }
            SetStatus($"Download abgeschlossen: {downloaded} erfolgreich, {errors.Count} Fehler.");
            MessageBox.Show(this, $"Heruntergeladen: {downloaded}\nFehler: {errors.Count}\n\n{localFolder}" +
                (errors.Count > 0 ? "\n\n" + string.Join("\n", errors) : ""),
                "SFTP-Download", MessageBoxButtons.OK,
                errors.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        });
    }

    private async Task DeleteSelectedAsync()
    {
        if (!browserConnected || busy) return;
        var files = SelectedFiles();
        if (files.Count == 0)
        {
            MessageBox.Show(this, "Bitte Dateien markieren. Ordner werden aus Sicherheitsgründen nicht gelöscht.",
                "SFTP", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var preview = string.Join("\n", files.Take(8).Select(f => "• " + f.Name));
        if (files.Count > 8) preview += $"\n... und {files.Count - 8} weitere";
        if (MessageBox.Show(this,
                $"Diese {files.Count} Datei(en) auf dem Server unwiderruflich löschen?\n\n{preview}",
                "Löschen bestätigen", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        var config = settings;
        await ExecuteAsync(async () =>
        {
            int deleted = 0;
            var errors = new List<string>();
            foreach (var file in files)
            {
                SetStatus("Lösche: " + file.Name);
                try
                {
                    await Task.Run(() =>
                    {
                        using var client = CreateClient(config);
                        client.Connect();
                        try { client.DeleteFile(file.FullPath); }
                        finally { if (client.IsConnected) client.Disconnect(); }
                    });
                    deleted++;
                }
                catch (Exception ex) { errors.Add(file.Name + ": " + ex.Message); }
            }
            SetStatus($"Gelöscht: {deleted} Datei(en); Fehler: {errors.Count}.");
            if (errors.Count > 0)
                MessageBox.Show(this, string.Join("\n", errors), "Löschfehler",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
        });
        await BrowseAsync(currentFolder);
    }
}

internal sealed class SettingsForm : Form
{
    public SftpSettings Result { get; private set; }
    private readonly TextBox host = new(), user = new(), password = new(), directory = new();
    private readonly NumericUpDown port = new();

    public SettingsForm(SftpSettings current)
    {
        Result = current;
        Text = "SSH / SFTP-Einstellungen";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(580, 354);
        Font = new Font("Segoe UI", 10);
        AddRow("SSH-Server", host, 20);
        AddRow("Port", port, 66);
        AddRow("Benutzername", user, 112);
        AddRow("Passwort", password, 158);
        AddRow("Standard-Uploadordner", directory, 204);

        password.UseSystemPasswordChar = true;
        port.Minimum = 1;
        port.Maximum = 65535;
        host.Text = current.Host;
        port.Value = Math.Clamp(current.Port, 1, 65535);
        user.Text = current.UserName;
        directory.Text = current.RemoteDirectory;
        try { password.Text = current.Password; }
        catch (Exception)
        {
            MessageBox.Show(this, "Gespeichertes Passwort konnte nicht entschlüsselt werden. Bitte erneut eingeben.");
        }
        Controls.Add(new Label
        {
            Text = "Desktop-Drag-&-Drop nutzt den Standard-Uploadordner. Der Dateibrowser beginnt bei /.\n" +
                   "Den SSH-Fingerabdruck musst du nicht eintippen: Er wird beim ersten Verbinden abgefragt.",
            Location = new Point(165, 243),
            Size = new Size(398, 58),
            Font = new Font("Segoe UI", 8.5f)
        });

        var save = new Button { Text = "Speichern", Bounds = new Rectangle(446, 306, 118, 35) };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(host.Text) || string.IsNullOrWhiteSpace(user.Text) ||
                string.IsNullOrEmpty(password.Text))
            {
                MessageBox.Show(this, "Bitte Server, Benutzername und Passwort angeben.");
                return;
            }
            try
            {
                var next = new SftpSettings
                {
                    Host = host.Text.Trim(),
                    Port = (int)port.Value,
                    UserName = user.Text.Trim(),
                    RemoteDirectory = string.IsNullOrWhiteSpace(directory.Text) ? "/" : directory.Text.Trim(),
                    // Retain server trust only if host and port have not changed.
                    HostKeySha256 = string.Equals(host.Text.Trim(), current.Host,
                        StringComparison.OrdinalIgnoreCase) && (int)port.Value == current.Port
                        ? current.HostKeySha256 : ""
                };
                next.Password = password.Text;
                Result = next;
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Einstellungen konnten nicht übernommen werden:\n" + ex.Message);
            }
        };
        Controls.Add(save);
        AcceptButton = save;
    }

    private void AddRow(string title, Control input, int y)
    {
        Controls.Add(new Label { Text = title, Location = new Point(18, y + 4), Size = new Size(145, 28) });
        input.Bounds = new Rectangle(165, y, 399, 30);
        Controls.Add(input);
    }
}
