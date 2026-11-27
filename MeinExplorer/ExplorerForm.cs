using System.Collections;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using ComTypes = System.Runtime.InteropServices.ComTypes;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace MeinExplorer;

public partial class ExplorerForm : Form
{
    private readonly UserSettings settings = UserSettings.Load();
    private TreeView activeTree;
    private readonly ImageList icons = new() { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(20, 20) };
    private readonly Dictionary<string, int> iconIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> back = new();
    private readonly List<string> forward = new();
    private readonly ContextMenuStrip fileMenu = new();
    private readonly ContextMenuStrip folderMenu = new();
    private readonly ContextMenuStrip favoriteMenu = new();
    private string currentPath = "";
    private bool fillingTree;
    private bool busy;
    private bool refreshingFolder;
    private bool refreshRequested;
    private int sortColumn = 3;
    private bool sortAscending = false;
    private readonly Color surface = Color.FromArgb(248, 250, 253);
    private readonly Color ink = Color.FromArgb(30, 41, 59);

    public ExplorerForm()
    {
        Text = "MeinExplorer – Meine Ordner im Blick";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(780, 500);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.White;
        ForeColor = ink;
        KeyPreview = true;
        InitializeComponent();
        activeTree = tree;
        // Im Windows-Forms-Designer ausschließlich die Oberfläche anzeigen.
        if (System.ComponentModel.LicenseManager.UsageMode == System.ComponentModel.LicenseUsageMode.Designtime)
            return;
        Size = new Size(Math.Max(780, settings.WindowWidth), Math.Max(500, settings.WindowHeight));
        WireEvents();
        BuildMenus();
        ReloadFavorites();
        // Ein ausdrücklich gewählter Startordner hat Vorrang vor allen Favoriten.
        var start = (!string.IsNullOrWhiteSpace(settings.StartFolder) && Directory.Exists(settings.StartFolder))
            ? settings.StartFolder
            : settings.Favorites.FirstOrDefault(s => Directory.Exists(s.Path))?.Path
              ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Navigate(start);
        FormClosing += (_, _) =>
        {
            settings.WindowWidth = Width;
            settings.WindowHeight = Height;
            settings.SidebarWidth = split.SplitterDistance;
            try { settings.Save(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Einstellungen speichern"); }
        };
        KeyDown += GlobalKeys;
        Shown += (_, _) =>
        {
            // Größe ist nach dem ersten Layout gültig; Mindestgrößen erst jetzt setzen.
            if (split.ClientSize.Width >= 505)
            {
                split.SplitterDistance = Math.Clamp(settings.SidebarWidth, 170,
                    split.ClientSize.Width - 5 - 330);
                split.Panel1MinSize = 170;
                split.Panel2MinSize = 330;
            }
        };
    }

    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 500 };
    private FileSystemWatcher? folderWatcher;

    private void WireEvents()
    {
        backButton.Click += (_, _) => GoBack();
        forwardButton.Click += (_, _) => GoForward();
        upButton.Click += (_, _) => GoUp();
        refreshButton.Click += (_, _) => RefreshFolder();
        startFolderButton.Click += (_, _) => SetCurrentAsStartFolder();
        addFavoriteButton.Click += (_, _) => AddFavorite();
        newFolderButton.Click += (_, _) => NewFolder();
        copyButton.Click += (_, _) => CopySelected(false);
        cutButton.Click += (_, _) => CopySelected(true);
        pasteButton.Click += (_, _) => Paste();
        renameButton.Click += (_, _) => RenameSelected();
        deleteButton.Click += (_, _) => DeleteSelected();
        address.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Navigate(address.Text); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; SetAddressPath(currentPath); }
        };
        address.MouseClick += (_, _) => address.SelectAll();
        address.Enter += (_, _) => address.SelectAll();
        filter.TextChanged += (_, _) => RefreshFolder();
        list.SmallImageList = icons;
        ConfigureFavoriteTree(tree, tree.BackColor);
        ConfigureFavoriteTree(superTree, superTree.BackColor);
        list.ListViewItemSorter = new ExplorerListComparer(() => sortColumn, () => sortAscending);
        list.DoubleClick += (_, _) => OpenSelected();
        list.KeyDown += ListKeys;
        list.ItemSelectionChanged += (_, _) => UpdateStatus();
        // Dateien und Ordner aus MeinExplorer in den Windows-Explorer, auf den
        // Desktop oder in andere Programme ziehen. Mehrfachauswahl wird unterstützt.
        list.ItemDrag += (_, e) =>
        {
            if (e.Item is not ListViewItem draggedItem) return;
            if (!draggedItem.Selected)
            {
                list.SelectedItems.Clear();
                draggedItem.Selected = true;
            }

            string[] paths = list.SelectedItems.Cast<ListViewItem>()
                .Select(item => (item.Tag as FileRow)?.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path) &&
                               (File.Exists(path) || Directory.Exists(path)))
                .Select(path => path!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (paths.Length == 0) return;

            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, paths);
            // Zielanwendung und Zusatztasten entscheiden über Kopieren/Verschieben.
            list.DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        };
        list.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
                (list.SelectedItems.Count > 0 ? fileMenu : folderMenu).Show(list, e.Location);
        };
        list.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        list.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] entries)
            {
                string target = currentPath;
                Point local = list.PointToClient(new Point(e.X, e.Y));
                if (list.GetItemAt(local.X, local.Y) is ListViewItem hit && hit.Tag is FileRow { IsFolder: true } hitRow && Directory.Exists(hitRow.Path)) target = hitRow.Path;
                CopyPaths(entries, target, false);
            }
        };
        // Sicherheit: Die Sortierköpfe sind im Designer definiert und müssen
        // vollständig erstellt sein, bevor Ereignisse gebunden werden.
        if (sortableLabels is { Length: 4 })
        {
            for (int i = 0; i < sortableLabels.Length; i++)
            {
                int column = i;
                if (sortableLabels[i] != null)
                    sortableLabels[i].Click += (_, _) => SortByColumn(column);
            }
        }
        sortableHeader.Resize += (_, _) => LayoutSortHeader();
        split.Panel2.Resize += (_, _) => LayoutSortHeader();
        UpdateSortHeaders();
        // Nach dem ersten abgeschlossenen Layout berechnen statt während
        // der Initialisierung und bevor die Steuerelemente bereit sind.
        Shown += (_, _) => LayoutSortHeader();
        refreshTimer.Tick += (_, _) =>
        {
            refreshTimer.Stop();
            if (Visible && !IsDisposed && !busy) RefreshFolder();
            else refreshRequested = true;
        };
        Activated += (_, _) => ScheduleRefresh();
        FormClosed += (_, _) => { folderWatcher?.Dispose(); refreshTimer.Dispose(); };
    }

    private void ScheduleRefresh()
    {
        if (IsDisposed || !IsHandleCreated || string.IsNullOrEmpty(currentPath)) return;
        if (InvokeRequired)
        {
            try { BeginInvoke((Action)ScheduleRefresh); } catch (InvalidOperationException) { }
            return;
        }
        refreshTimer.Stop();
        refreshTimer.Start();
    }

    private void WatchCurrentFolder()
    {
        folderWatcher?.Dispose();
        folderWatcher = null;
        if (!Directory.Exists(currentPath)) return;
        try
        {
            folderWatcher = new FileSystemWatcher(currentPath)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            folderWatcher.Changed += (_, _) => ScheduleRefresh();
            folderWatcher.Created += (_, _) => ScheduleRefresh();
            folderWatcher.Deleted += (_, _) => ScheduleRefresh();
            folderWatcher.Renamed += (_, _) => ScheduleRefresh();
            folderWatcher.Error += (_, _) => ScheduleRefresh();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void SortByColumn(int column)
    {
        if (sortColumn == column)
            sortAscending = !sortAscending;
        else
        {
            sortColumn = column;
            sortAscending = true;
        }
        UpdateSortHeaders();
        list.Sort();
    }

    private void LayoutSortHeader()
    {
        if (sortableLabels is not { Length: 4 } ||
            sortableLabels.Any(label => label is null) ||
            list is null || list.Columns.Count < 4 ||
            sortableHeader is null || sortableHeader.IsDisposed)
            return;
        // Mindestbreite der Namensspalte, die anderen Spalten behalten feste Breiten.
        int nameWidth = Math.Max(225, sortableHeader.ClientSize.Width - 432);
        int[] widths = { nameWidth, 150, 110, 172 };
        int x = 0;
        for (int i = 0; i < 4; i++)
        {
            sortableLabels[i].SetBounds(x, 0, widths[i], 33);
            list.Columns[i].Width = widths[i];
            x += widths[i];
        }
    }

    private void UpdateSortHeaders()
    {
        if (list is null || list.Columns.Count < 4) return;
        string[] names = { "Name", "Typ", "Größe", "Geändert" };
        for (int i = 0; i < names.Length; i++)
        {
            string text = names[i] + (sortColumn == i ? (sortAscending ? "  ▲" : "  ▼") : "");
            list.Columns[i].Text = text;
            if (sortableLabels is { Length: 4 } && sortableLabels[i] != null)
            {
                sortableLabels[i].Text = text;
                sortableLabels[i].BackColor = sortColumn == i
                    ? Color.FromArgb(220, 234, 253)
                    : Color.FromArgb(241, 245, 251);
            }
        }
    }

    // Die Sortierung verwendet ausschließlich beim Einlesen erfasste Werte.
    // Während Compare() findet keinerlei Zugriff auf das Dateisystem statt.
    private sealed record FileRow(string Path, bool IsFolder, string Type,
        long Size, DateTime ChangedUtc);

    private sealed class ExplorerListComparer : IComparer
    {
        private readonly Func<int> column;
        private readonly Func<bool> ascending;

        public ExplorerListComparer(Func<int> column, Func<bool> ascending)
        {
            this.column = column;
            this.ascending = ascending;
        }

        public int Compare(object? a, object? b)
        {
            if (a is not ListViewItem x || b is not ListViewItem y) return 0;
            var fx = x.Tag as FileRow;
            var fy = y.Tag as FileRow;
            if (fx is null || fy is null) return StringComparer.CurrentCultureIgnoreCase.Compare(x.Text, y.Text);
            // Ordner immer vor Dateien – auch bei absteigender Sortierung.
            if (fx.IsFolder != fy.IsFolder) return fx.IsFolder ? -1 : 1;

            int result = column() switch
            {
                1 => StringComparer.CurrentCultureIgnoreCase.Compare(fx.Type, fy.Type),
                2 => fx.Size.CompareTo(fy.Size),
                3 => fx.ChangedUtc.CompareTo(fy.ChangedUtc),
                _ => StringComparer.CurrentCultureIgnoreCase.Compare(x.Text, y.Text)
            };
            if (result != 0 && !ascending()) result = -result;
            if (result == 0) result = StringComparer.CurrentCultureIgnoreCase.Compare(x.Text, y.Text);
            return result;
        }
    }

    private void BuildMenus()
    {
        void Item(ContextMenuStrip menu, string label, Action fn, Keys shortcut = Keys.None)
        {
            var item = new ToolStripMenuItem(label);
            if (shortcut != Keys.None) item.ShortcutKeys = shortcut;
            item.Click += (_, _) => fn();
            menu.Items.Add(item);
        }
        Item(fileMenu, "Öffnen", OpenSelected);
        fileMenu.Items.Add(new ToolStripSeparator());
        Item(fileMenu, "Kopieren", () => CopySelected(false), Keys.Control | Keys.C);
        Item(fileMenu, "Ausschneiden", () => CopySelected(true), Keys.Control | Keys.X);
        Item(fileMenu, "Einfügen", Paste, Keys.Control | Keys.V);
        fileMenu.Items.Add(new ToolStripSeparator());
        Item(fileMenu, "Umbenennen", RenameSelected, Keys.F2);
        Item(fileMenu, "In den Papierkorb", DeleteSelected, Keys.Delete);
        fileMenu.Items.Add(new ToolStripSeparator());
        Item(fileMenu, "Im Windows-Explorer anzeigen", ShowInWindowsExplorer);
        Item(folderMenu, "Neuer Ordner", NewFolder);
        Item(folderMenu, "Einfügen", Paste);
        Item(folderMenu, "Aktualisieren", RefreshFolder);
        Item(favoriteMenu, "Öffnen", () => { if (SelectedFolderPath() is string path) Navigate(path); });
        favoriteMenu.Items.Add(new ToolStripSeparator());
        Item(favoriteMenu, "Favorit hinzufügen", AddFavorite);
        Item(favoriteMenu, "Favorit bearbeiten", EditFavorite);
        Item(favoriteMenu, "Favorit entfernen", RemoveFavorite);
        favoriteMenu.Items.Add(new ToolStripSeparator());
        Item(favoriteMenu, "★ Als Super-Favorit markieren", () => SetSuperFavorite(true));
        Item(favoriteMenu, "☆ Zu normalen Favoriten verschieben", () => SetSuperFavorite(false));
        favoriteMenu.Opening += (_, _) =>
        {
            var entry = SelectedFavorite();
            foreach (ToolStripItem item in favoriteMenu.Items)
            {
                if (item.Text is "Favorit bearbeiten" or "Favorit entfernen") item.Enabled = entry is not null;
                if (item.Text == "★ Als Super-Favorit markieren") item.Enabled = entry is not null && !entry.IsSuperFavorite;
                if (item.Text == "☆ Zu normalen Favoriten verschieben") item.Enabled = entry is not null && entry.IsSuperFavorite;
            }
        };
    }

    private void ConfigureFavoriteTree(TreeView view, Color backColor)
    {
        view.BorderStyle = BorderStyle.None;
        view.BackColor = backColor;
        view.ForeColor = ink;
        view.Font = new Font("Segoe UI", 10);
        view.ItemHeight = 29;
        view.HideSelection = false;
        // Jede Favoritenliste scrollt unabhängig, sobald ihre Höhe nicht reicht.
        view.Scrollable = true;
        view.ShowLines = false;
        view.ShowRootLines = false;
        view.ShowPlusMinus = false;
        view.ShowNodeToolTips = true;
        view.ImageList = icons;
        // Links sind nur fest gespeicherte Favoriten sichtbar, keine Unterordner.
        view.BeforeExpand += (_, e) => e.Cancel = true;
        view.AfterSelect += (_, e) =>
        {
            if (fillingTree || e.Node is null) return;
            activeTree = view;
            // Auswahl der beiden Ansichten gegenseitig aufheben.
            fillingTree = true;
            try { if (view != tree) tree.SelectedNode = null; if (view != superTree) superTree.SelectedNode = null; }
            finally { fillingTree = false; }
            if (SelectedFolderPath() is string dir) Navigate(dir, selectTree: false);
        };
        view.NodeMouseClick += (_, e) =>
        {
            activeTree = view;
            if (e.Button == MouseButtons.Right)
            {
                view.SelectedNode = e.Node;
                favoriteMenu.Show(view, e.Location);
            }
        };
    }

    private TreeNode CreateFavoriteNode(Shortcut entry)
    {
        var node = new TreeNode(entry.Name)
        {
            Tag = entry, ImageIndex = 0, SelectedImageIndex = 0,
            ToolTipText = entry.Path
        };
        if (!Directory.Exists(entry.Path)) node.ForeColor = Color.FromArgb(170, 95, 95);
        return node;
    }

    private void ReloadFavorites(string? select = null)
    {
        fillingTree = true;
        try
        {
            tree.BeginUpdate();
            superTree.BeginUpdate();
            tree.Nodes.Clear();
            superTree.Nodes.Clear();
            var sorted = settings.Favorites.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(f => f.Path, StringComparer.CurrentCultureIgnoreCase);
            foreach (var entry in sorted)
            {
                var view = entry.IsSuperFavorite ? superTree : tree;
                var node = CreateFavoriteNode(entry);
                view.Nodes.Add(node);
                if (select is not null && string.Equals(entry.Path, select, StringComparison.OrdinalIgnoreCase))
                {
                    activeTree = view;
                    view.SelectedNode = node;
                }
            }
            // Eigener Bereich bleibt sichtbar, auch wenn er noch leer ist.
            // Ab mehr als ca. sieben Einträgen scrollt die Super-Favoriten-Liste
            // innerhalb ihres eigenen (maximal 225 Pixel hohen) Bereichs.
            // Normale Favoriten scrollen unabhängig im restlichen Platz.
            favoritesLayout.RowStyles[0].Height = 37 +
                (superTree.Nodes.Count == 0 ? 0 : Math.Min(superTree.Nodes.Count * superTree.ItemHeight + 5, 225));
        }
        finally
        {
            superTree.EndUpdate();
            tree.EndUpdate();
            fillingTree = false;
        }
    }

    private Shortcut? SelectedFavorite() =>
        activeTree.SelectedNode is { Parent: null, Tag: Shortcut entry } ? entry : null;

    private string? SelectedFolderPath() => activeTree.SelectedNode?.Tag switch
    {
        Shortcut favorite => favorite.Path,
        string path => path,
        _ => null
    };

    private bool IsDuplicateFavorite(string name, string path, Shortcut? except = null)
    {
        string normalizedPath = UserSettings.NormalizeFavoritePath(path);
        bool duplicate = settings.Favorites.Any(f => !ReferenceEquals(f, except) &&
            (string.Equals(f.Name.Trim(), name.Trim(), StringComparison.CurrentCultureIgnoreCase) ||
             string.Equals(UserSettings.NormalizeFavoritePath(f.Path), normalizedPath,
                 StringComparison.OrdinalIgnoreCase)));
        if (duplicate)
            MessageBox.Show(this,
                "Dieser Name oder dieser Ordner ist bereits als Favorit vorhanden.",
                "Doppelter Favorit", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return duplicate;
    }

    private void AddFavorite()
    {
        using var dialog = new EntryForm();
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (IsDuplicateFavorite(dialog.EntryName, dialog.EntryPath)) return;
        settings.Favorites.Add(new Shortcut { Name = dialog.EntryName, Path = dialog.EntryPath });
        SaveFavorites(dialog.EntryPath);
    }

    private void EditFavorite()
    {
        var entry = SelectedFavorite();
        if (entry is null) { MessageBox.Show(this, "Bitte zuerst einen Favoriten links auswählen (keinen Unterordner)."); return; }
        using var dialog = new EntryForm(entry.Name, entry.Path);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (IsDuplicateFavorite(dialog.EntryName, dialog.EntryPath, entry)) return;
        entry.Name = dialog.EntryName;
        entry.Path = dialog.EntryPath;
        SaveFavorites(entry.Path);
    }

    private void RemoveFavorite()
    {
        var entry = SelectedFavorite();
        if (entry is null) return;
        if (MessageBox.Show(this, $"Favorit „{entry.Name}“ entfernen?\nDie Dateien bleiben unverändert.", "Favorit entfernen",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        settings.Favorites.Remove(entry);
        SaveFavorites();
    }

    private void SetSuperFavorite(bool value)
    {
        var entry = SelectedFavorite();
        if (entry is null || entry.IsSuperFavorite == value) return;
        entry.IsSuperFavorite = value;
        SaveFavorites(entry.Path);
    }

    private void SetCurrentAsStartFolder()
    {
        if (string.IsNullOrWhiteSpace(currentPath) || !Directory.Exists(currentPath))
        {
            MessageBox.Show(this, "Bitte zunächst einen vorhandenen Ordner öffnen.", "Startordner");
            return;
        }

        settings.StartFolder = currentPath;
        try
        {
            settings.Save();
            status.Text = $"Startordner gespeichert: {currentPath}";
            MessageBox.Show(this, $"Dieser Ordner wird ab jetzt beim Start geöffnet:\n{currentPath}",
                "Startordner gespeichert", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { ShowError("Startordner speichern", ex); }
    }

    // Der Wartecursor wird während der tatsächlichen Datenübertragung angezeigt.
    // Diese Klammer ändert keine Dateioperation und greift nicht in die Auflistung ein.
    private int copyFeedbackDepth;
    private string? statusBeforeCopy;

    private void BeginCopyFeedback(string message)
    {
        if (copyFeedbackDepth++ != 0) return;
        statusBeforeCopy = status.Text;
        UseWaitCursor = true;
        Application.UseWaitCursor = true;
        Cursor.Current = Cursors.WaitCursor;
        status.Text = message;
        status.Refresh();
    }

    private void EndCopyFeedback()
    {
        if (copyFeedbackDepth <= 0 || --copyFeedbackDepth != 0) return;
        UseWaitCursor = false;
        Application.UseWaitCursor = false;
        Cursor.Current = Cursors.Default;
        // Erfolg und Fehlermeldungen überschreiben diesen Text anschließend.
        status.Text = statusBeforeCopy ?? "Bereit";
        statusBeforeCopy = null;
    }

    private void SaveFavorites(string? select = null)
    {
        try { settings.Save(); } catch (Exception ex) { ShowError("Favoriten speichern", ex); }
        ReloadFavorites(select);
    }

    private void SetAddressPath(string path)
    {
        address.Text = path;
        // Den Text am Anfang statt am Ende anzeigen, auch nach einem Layoutwechsel.
        address.SelectionStart = 0;
        address.SelectionLength = 0;
        if (address.IsHandleCreated)
        {
            address.BeginInvoke((Action)(() =>
            {
                if (address.IsDisposed || address.Text != path) return;
                address.SelectionStart = 0;
                address.SelectionLength = 0;
            }));
        }
    }

    private void Navigate(string input, bool recordHistory = true, bool selectTree = true)
    {
        string target;
        try { target = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(input.Trim().Trim('"'))); }
        catch (Exception ex) { ShowError("Ungültiger Pfad", ex); SetAddressPath(currentPath); return; }
        if (!Directory.Exists(target))
        {
            MessageBox.Show(this, $"Dieser Ordner ist nicht erreichbar:\n{target}", "Verzeichnis nicht gefunden",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            SetAddressPath(currentPath);
            return;
        }
        if (recordHistory && !string.IsNullOrEmpty(currentPath) && !string.Equals(currentPath, target, StringComparison.OrdinalIgnoreCase))
        {
            back.Add(currentPath);
            forward.Clear();
        }
        currentPath = target;
        WatchCurrentFolder();
        SetAddressPath(currentPath);
        filter.Clear();
        RefreshFolder();
        backButton.Enabled = back.Count > 0;
        forwardButton.Enabled = forward.Count > 0;
        if (selectTree)
        {
            fillingTree = true;
            try
            {
                foreach (var view in new[] { superTree, tree })
                {
                    TreeNode? match = view.Nodes.Cast<TreeNode>().FirstOrDefault(n =>
                        n.Tag is Shortcut f && string.Equals(f.Path, currentPath, StringComparison.OrdinalIgnoreCase));
                    if (match is null) continue;
                    activeTree = view;
                    view.SelectedNode = match;
                    if (view != tree) tree.SelectedNode = null;
                    if (view != superTree) superTree.SelectedNode = null;
                    break;
                }
            }
            finally { fillingTree = false; }
        }
    }

    private void GoBack()
    {
        if (back.Count == 0) return;
        var previous = back[^1]; back.RemoveAt(back.Count - 1);
        forward.Add(currentPath);
        Navigate(previous, false);
    }
    private void GoForward()
    {
        if (forward.Count == 0) return;
        var next = forward[^1]; forward.RemoveAt(forward.Count - 1);
        back.Add(currentPath);
        Navigate(next, false);
    }
    private void GoUp()
    {
        if (string.IsNullOrEmpty(currentPath)) return;
        var parent = Directory.GetParent(currentPath);
        if (parent is not null) Navigate(parent.FullName);
    }

    private void RefreshFolder()
    {
        if (string.IsNullOrWhiteSpace(currentPath) || list.IsDisposed) return;
        if (busy || refreshingFolder)
        {
            refreshRequested = true;
            return;
        }

        refreshingFolder = true;
        refreshTimer.Stop();
        try
        {
            if (!Directory.Exists(currentPath))
            {
                status.Text = $"Verzeichnis nicht erreichbar: {currentPath}";
                return; // Alte Liste sichtbar lassen.
            }

            // Filter nur aus dem sichtbaren Suchfeld lesen.
            var selected = SelectedPaths().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var newItems = new List<ListViewItem>();
            int skipped = 0;
            string query = filter.Text.Trim();
            bool Matches(string name) => query.Length == 0 ||
                name.Contains(query, StringComparison.CurrentCultureIgnoreCase);

            // Falls das Verzeichnis nicht gelesen werden kann, wird die alte
            // Anzeige NICHT gelöscht. Fehler werden im Status + Log sichtbar.
            foreach (var path in Directory.EnumerateFileSystemEntries(currentPath))
            {
                try
                {
                    var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path));
                    if (!Matches(name)) continue;
                    bool isFolder;
                    try { isFolder = (File.GetAttributes(path) & FileAttributes.Directory) != 0; }
                    catch (IOException) { isFolder = Directory.Exists(path); }
                    catch (UnauthorizedAccessException) { isFolder = Directory.Exists(path); }

                    string type = isFolder ? "Dateiordner" :
                        (string.IsNullOrWhiteSpace(System.IO.Path.GetExtension(path))
                            ? "Datei" : System.IO.Path.GetExtension(path).ToUpperInvariant() + "-Datei");
                    long size = 0;
                    DateTime changed = DateTime.MinValue;
                    if (isFolder)
                    {
                        try { changed = Directory.GetLastWriteTimeUtc(path); } catch (IOException) { }
                    }
                    else
                    {
                        try
                        {
                            var file = new FileInfo(path);
                            size = file.Length;
                            changed = file.LastWriteTimeUtc;
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }

                    int imageIndex;
                    try { imageIndex = GetIconIndex(path, isFolder); }
                    catch { imageIndex = -1; }
                    var item = new ListViewItem(name, imageIndex)
                    {
                        Tag = new FileRow(path, isFolder, type, size, changed)
                    };
                    item.SubItems.Add(type);
                    item.SubItems.Add(isFolder ? "" : FormatBytes(size));
                    item.SubItems.Add(changed == DateTime.MinValue ? "" : changed.ToLocalTime().ToString("dd.MM.yyyy HH:mm"));
                    item.Selected = selected.Contains(path);
                    newItems.Add(item);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or System.Security.SecurityException or ArgumentException)
                {
                    skipped++;
                    LogFolderError($"Eintrag {path}", ex);
                }
            }

            // ListView-Sortierung erst nach dem vollständigen Einlesen und
            // ohne IO im Comparer. ListViewItem.Tag enthält die Metadaten.
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                list.Items.AddRange(newItems.ToArray());
                list.Sort();
            }
            finally { list.EndUpdate(); }
            UpdateStatus();
            if (skipped > 0)
                status.Text += $"  ·  {skipped} Einträge nicht lesbar (siehe Fehlerprotokoll)";
        }
        catch (Exception ex)
        {
            LogFolderError(currentPath, ex);
            status.Text = $"Fehler beim Laden von {currentPath}: {ex.Message}";
            // Keine künstliche leere Liste bei fehlgeschlagenem Ordnerzugriff.
        }
        finally
        {
            refreshingFolder = false;
            if (refreshRequested && !busy && !IsDisposed)
            {
                refreshRequested = false;
                ScheduleRefresh();
            }
        }
    }

    private static void LogFolderError(string location, Exception ex)
    {
        try
        {
            var folder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeinExplorer");
            Directory.CreateDirectory(folder);
            File.AppendAllText(System.IO.Path.Combine(folder, "errors.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {location}: {ex}\n");
        }
        catch { /* Logging darf nie die Dateianzeige blockieren. */ }
    }

    private static string FormatBytes(long value)
    {
        if (value < 1024) return value + " B";
        string[] units = ["KB", "MB", "GB", "TB"];
        double size = value;
        int i = -1;
        do { size /= 1024; i++; } while (size >= 1024 && i < units.Length - 1);
        return size.ToString("N1") + " " + units[i];
    }

    private void UpdateStatus()
    {
        status.Text = $"{list.Items.Count} Elemente  ·  {list.SelectedItems.Count} ausgewählt  ·  {currentPath}";
    }

    private List<string> SelectedPaths() => list.SelectedItems.Cast<ListViewItem>()
        .Select(i => (i.Tag as FileRow)?.Path).Where(s => !string.IsNullOrWhiteSpace(s)).Cast<string>().ToList();

    private void OpenSelected()
    {
        if (list.SelectedItems.Count == 0) return;
        foreach (var p in SelectedPaths())
        {
            try
            {
                if (Directory.Exists(p)) { Navigate(p); break; }
                if (File.Exists(p)) Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
            }
            catch (Exception ex) { ShowError("Öffnen", ex); }
        }
    }

    private void ShowInWindowsExplorer()
    {
        var selected = SelectedPaths().FirstOrDefault();
        if (selected is null) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{selected}\"") { UseShellExecute = true }); }
        catch (Exception ex) { ShowError("Windows-Explorer", ex); }
    }

    private void CopySelected(bool cut)
    {
        var files = SelectedPaths();
        if (files.Count == 0) return;
        try
        {
            var collection = new StringCollection(); collection.AddRange(files.ToArray());
            var data = new DataObject();
            data.SetFileDropList(collection);
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
            Clipboard.SetDataObject(data, true);
            status.Text = $"{files.Count} Elemente {(cut ? "ausgeschnitten" : "kopiert")} – Ziel öffnen und Strg+V drücken";
        }
        catch (Exception ex) { ShowError("Zwischenablage", ex); }
    }

    private void Paste()
    {
        if (busy) return;
        try
        {
            // CF_HDROP kann bei Remote Desktop nur Platzhalter-Pfade enthalten.
            // Existieren die Quellen nicht wirklich, zuerst virtuelle Datei-Streams versuchen.
            if (Clipboard.ContainsFileDropList())
            {
                var paths = Clipboard.GetFileDropList().Cast<string>().ToArray();
                if (paths.Length == 0)
                {
                    if (PasteVirtualFiles()) return;
                    MessageBox.Show(this, "Die Zwischenablage enthält keine lesbaren Dateipfade.", "Einfügen");
                    return;
                }
                if (paths.Any(p => !File.Exists(p) && !Directory.Exists(p)))
                {
                    if (PasteVirtualFiles()) return;
                    MessageBox.Show(this,
                        "Windows App hat nur virtuelle oder nicht erreichbare Dateipfade geliefert. " +
                        "Es sind aber keine lesbaren Datei-Streams verfügbar. Bitte Datei-Umleitung " +
                        "in Windows App prüfen.", "Einfügen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                bool move = false;
                var data = Clipboard.GetDataObject();
                if (data?.GetData("Preferred DropEffect") is MemoryStream stream)
                {
                    stream.Position = 0;
                    byte[] buffer = new byte[4];
                    if (stream.Read(buffer, 0, 4) == 4)
                        move = (BitConverter.ToInt32(buffer, 0) & 2) != 0;
                }
                else if (data?.GetData("Preferred DropEffect") is byte[] b && b.Length >= 4)
                    move = (BitConverter.ToInt32(b, 0) & 2) != 0;
                CopyPaths(paths, currentPath, move);
                return;
            }

            // Remote Desktop / Windows App (z.B. Mac -> Windows): virtuelle Dateien
            // werden als FileGroupDescriptorW + indexierte FileContents-Streams angeboten.
            if (PasteVirtualFiles()) return;

            MessageBox.Show(this,
                "Die Zwischenablage enthält keine unterstützten Dateien.\n\n" +
                "Kopiere auf dem Mac die Datei (nicht ihren Namen) und prüfe in " +
                "Windows App, ob die Zwischenablage-Umleitung aktiviert ist.",
                "Einfügen", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { ShowError("Einfügen", ex); }
    }

    private const string FileDescriptorFormat = "FileGroupDescriptorW";
    private const string FileContentsFormat = "FileContents";
    private const int FileDescriptorSize = 592;
    private const int FileDescriptorNameOffset = 72;

    [DllImport("ole32.dll")]
    private static extern int OleGetClipboard(out ComTypes.IDataObject dataObject);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref ComTypes.STGMEDIUM medium);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr hMem);

    private static ComTypes.FORMATETC ClipboardFormat(uint format, int index, ComTypes.TYMED medium) => new()
    {
        cfFormat = unchecked((short)format),
        dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
        lindex = index,
        ptd = IntPtr.Zero,
        tymed = medium
    };

    private bool PasteVirtualFiles()
    {
        int hr = OleGetClipboard(out var dataObject);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        try
        {
            uint descriptorId = RegisterClipboardFormat(FileDescriptorFormat);
            uint contentsId = RegisterClipboardFormat(FileContentsFormat);
            var descriptorFormat = ClipboardFormat(descriptorId, -1, ComTypes.TYMED.TYMED_HGLOBAL);
            if (dataObject.QueryGetData(ref descriptorFormat) != 0)
                return false;

            dataObject.GetData(ref descriptorFormat, out var descriptors);
            try
            {
                if (descriptors.tymed != ComTypes.TYMED.TYMED_HGLOBAL || descriptors.unionmember == IntPtr.Zero)
                    throw new IOException("Die virtuellen Dateinamen konnten nicht gelesen werden.");

                IntPtr data = GlobalLock(descriptors.unionmember);
                if (data == IntPtr.Zero) throw new IOException("Die Zwischenablage ist vorübergehend nicht verfügbar.");
                var names = new List<(string Name, bool IsDirectory, int Index)>();
                try
                {
                    ulong size = GlobalSize(descriptors.unionmember).ToUInt64();
                    if (size < 4) throw new InvalidDataException("Ungültige Dateiliste in der Zwischenablage.");
                    int count = Marshal.ReadInt32(data);
                    if (count < 0 || count > 10000 || (ulong)count * FileDescriptorSize + 4 > size)
                        throw new InvalidDataException("Ungültige Anzahl virtueller Dateien.");

                    for (int i = 0; i < count; i++)
                    {
                        int offset = checked(4 + i * FileDescriptorSize);
                        uint flags = unchecked((uint)Marshal.ReadInt32(data, offset));
                        uint attributes = unchecked((uint)Marshal.ReadInt32(data, offset + 36));
                        string name = Marshal.PtrToStringUni(IntPtr.Add(data, offset + FileDescriptorNameOffset), 260)?
                            .TrimEnd('\0') ?? "";
                        bool isDirectory = (flags & 4) != 0 && (attributes & (uint)FileAttributes.Directory) != 0;
                        names.Add((name, isDirectory, i));
                    }
                }
                finally { GlobalUnlock(descriptors.unionmember); }

                int successful = 0;
                var errors = new List<string>();
                busy = true;
                BeginCopyFeedback($"Dateien vom Mac werden eingefügt ({names.Count}) …");
                try
                {
                    foreach (var (name, isDirectory, index) in names)
                    {
                        try
                        {
                            // Die Namen stammen aus einer externen Zwischenablage.
                            // Absolute Pfade und '..' dürfen das Zielverzeichnis nie verlassen.
                            string relativeName = name.Replace('/', '\\');
                            string[] parts = relativeName.Split('\\');
                            if (Path.IsPathRooted(relativeName) || parts.Length == 0 ||
                                parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".." ||
                                    part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                                throw new IOException("Unsicherer Dateiname in der Zwischenablage.");

                            string destination = Path.Combine(new[] { currentPath }.Concat(parts).ToArray());
                            if (isDirectory)
                            {
                                if (File.Exists(destination)) throw new IOException("An dieser Stelle existiert bereits eine Datei.");
                                Directory.CreateDirectory(destination);
                                successful++;
                                continue;
                            }

                            if (Directory.Exists(destination)) throw new IOException("An dieser Stelle existiert bereits ein Ordner.");
                            if (File.Exists(destination) && !ConfirmOverwrite(relativeName)) continue;
                            string? parent = Path.GetDirectoryName(destination);
                            if (parent is not null) Directory.CreateDirectory(parent);

                            // Datei zuerst temporär schreiben: Bei Abbruch bleibt das alte Ziel erhalten.
                            string temp = Path.Combine(parent ?? currentPath, ".meinexplorer-" + Guid.NewGuid().ToString("N") + ".tmp");
                            try
                            {
                                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                                    CopyVirtualFileContent(dataObject, contentsId, index, output);
                                File.Move(temp, destination, overwrite: true);
                                successful++;
                            }
                            finally { if (File.Exists(temp)) File.Delete(temp); }
                        }
                        catch (Exception ex) { errors.Add($"{name}: {ex.Message}"); }
                    }
                }
                finally { EndCopyFeedback(); busy = false; RefreshFolder(); }
                if (errors.Count > 0)
                    MessageBox.Show(this, $"{successful} erfolgreich übertragen.\n\n" + string.Join("\n", errors.Take(10)),
                        "Einfügen vom Mac", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else
                    status.Text = $"{successful} Elemente aus der Remote-Zwischenablage eingefügt";
                return true;
            }
            finally { ReleaseStgMedium(ref descriptors); }
        }
        finally { Marshal.ReleaseComObject(dataObject); }
    }

    private static void CopyVirtualFileContent(ComTypes.IDataObject dataObject, uint formatId, int index, Stream destination)
    {
        ComTypes.STGMEDIUM medium = default;
        bool received = false;
        try
        {
            // RDP verwendet überwiegend IStream. Manche Anbieter verwenden HGLOBAL.
            try
            {
                var streamFormat = ClipboardFormat(formatId, index, ComTypes.TYMED.TYMED_ISTREAM);
                dataObject.GetData(ref streamFormat, out medium);
                received = true;
            }
            catch (COMException)
            {
                var memoryFormat = ClipboardFormat(formatId, index, ComTypes.TYMED.TYMED_HGLOBAL);
                dataObject.GetData(ref memoryFormat, out medium);
                received = true;
            }

            if (medium.tymed == ComTypes.TYMED.TYMED_ISTREAM)
            {
                var input = (ComTypes.IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
                try
                {
                    byte[] buffer = new byte[64 * 1024];
                    IntPtr countPtr = Marshal.AllocHGlobal(4);
                    try
                    {
                        while (true)
                        {
                            Marshal.WriteInt32(countPtr, 0);
                            input.Read(buffer, buffer.Length, countPtr);
                            int bytes = Marshal.ReadInt32(countPtr);
                            if (bytes <= 0) break;
                            destination.Write(buffer, 0, bytes);
                        }
                    }
                    finally { Marshal.FreeHGlobal(countPtr); }
                }
                finally { Marshal.ReleaseComObject(input); }
            }
            else if (medium.tymed == ComTypes.TYMED.TYMED_HGLOBAL)
            {
                IntPtr locked = GlobalLock(medium.unionmember);
                if (locked == IntPtr.Zero) throw new IOException("Dateistream konnte nicht gelesen werden.");
                try
                {
                    ulong length = GlobalSize(medium.unionmember).ToUInt64();
                    byte[] buffer = new byte[64 * 1024];
                    ulong position = 0;
                    while (position < length)
                    {
                        int n = (int)Math.Min((ulong)buffer.Length, length - position);
                        if (position > int.MaxValue) throw new IOException("Zu große HGLOBAL-Datei.");
                        Marshal.Copy(IntPtr.Add(locked, (int)position), buffer, 0, n);
                        destination.Write(buffer, 0, n);
                        position += (ulong)n;
                    }
                }
                finally { GlobalUnlock(medium.unionmember); }
            }
            else throw new IOException("Nicht unterstütztes Dateistream-Format.");
        }
        finally { if (received) ReleaseStgMedium(ref medium); }
    }

    private void CopyPaths(IEnumerable<string> sources, string targetFolder, bool move)
    {
        if (busy) return;
        busy = true;
        BeginCopyFeedback(move ? "Dateien werden verschoben …" : "Dateien werden kopiert …");
        int done = 0;
        var errors = new List<string>();
        try
        {
            foreach (string source in sources)
            {
                try
                {
                    if (!File.Exists(source) && !Directory.Exists(source)) throw new FileNotFoundException("Quelle nicht vorhanden", source);
                    var name = System.IO.Path.GetFileName(source.TrimEnd('\\', '/'));
                    string dest = System.IO.Path.Combine(targetFolder, name);
                    if (string.Equals(System.IO.Path.GetFullPath(source).TrimEnd('\\'), System.IO.Path.GetFullPath(dest).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException("Quelle und Ziel sind identisch.");
                    }
                    if (Directory.Exists(source))
                    {
                        if (System.IO.Path.GetFullPath(targetFolder).TrimEnd('\\').StartsWith(System.IO.Path.GetFullPath(source).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                            throw new IOException("Ein Ordner kann nicht in sich selbst kopiert werden.");
                        if (Directory.Exists(dest) || File.Exists(dest))
                        {
                            if (!ConfirmOverwrite(name)) continue;
                            if (File.Exists(dest)) throw new IOException("Im Ziel existiert eine Datei mit demselben Namen.");
                        }
                        CopyDirectory(source, dest);
                        if (move) Directory.Delete(source, true);
                    }
                    else
                    {
                        if ((File.Exists(dest) || Directory.Exists(dest)) && !ConfirmOverwrite(name)) continue;
                        if (Directory.Exists(dest)) throw new IOException("Im Ziel existiert ein Ordner mit demselben Namen.");
                        File.Copy(source, dest, true);
                        if (move) File.Delete(source);
                    }
                    done++;
                }
                catch (Exception ex) { errors.Add($"{System.IO.Path.GetFileName(source)}: {ex.Message}"); }
            }
        }
        finally { EndCopyFeedback(); busy = false; RefreshFolder(); }
        if (errors.Count > 0) MessageBox.Show(this, $"{done} erfolgreich.\n\n" + string.Join("\n", errors.Take(12)),
            "Dateivorgang", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else status.Text = $"{done} Elemente {(move ? "verschoben" : "kopiert")}";
    }

    private bool ConfirmOverwrite(string name) => MessageBox.Show(this,
        $"„{name}“ ist im Ziel bereits vorhanden. Überschreiben bzw. Ordnerinhalte zusammenführen?",
        "Vorhandenes Element", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
        MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    private static void CopyDirectory(string source, string destination)
    {
        var info = new DirectoryInfo(source);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Verknüpfte Verzeichnisse werden aus Sicherheitsgründen nicht rekursiv kopiert.");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, System.IO.Path.Combine(destination, System.IO.Path.GetFileName(file)), true);
        foreach (var dir in Directory.EnumerateDirectories(source)) CopyDirectory(dir, System.IO.Path.Combine(destination, System.IO.Path.GetFileName(dir)));
    }

    private void NewFolder()
    {
        var name = Prompt("Neuer Ordner", "Name des neuen Ordners:", "Neuer Ordner");
        if (name is null) return;
        if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
        {
            MessageBox.Show(this, "Ungültiger Ordnername."); return;
        }
        try { Directory.CreateDirectory(System.IO.Path.Combine(currentPath, name)); RefreshFolder(); }
        catch (Exception ex) { ShowError("Neuer Ordner", ex); }
    }

    private void RenameSelected()
    {
        var paths = SelectedPaths();
        if (paths.Count != 1) { MessageBox.Show(this, "Bitte genau ein Element auswählen."); return; }
        var source = paths[0];
        var name = Prompt("Umbenennen", "Neuer Name:", System.IO.Path.GetFileName(source));
        if (name is null) return;
        if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
        { MessageBox.Show(this, "Ungültiger Name."); return; }
        var dest = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source)!, name);
        try
        {
            if (File.Exists(dest) || Directory.Exists(dest)) throw new IOException("Der Name ist bereits vergeben.");
            if (Directory.Exists(source)) Directory.Move(source, dest);
            else File.Move(source, dest);
            RefreshFolder();
        }
        catch (Exception ex) { ShowError("Umbenennen", ex); }
    }

    private void DeleteSelected()
    {
        var paths = SelectedPaths();
        if (paths.Count == 0) return;
        if (MessageBox.Show(this, $"{paths.Count} ausgewählte Elemente in den Papierkorb verschieben?",
            "Löschen bestätigen", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        var errors = new List<string>();
        foreach (var file in paths)
        {
            try
            {
                if (Directory.Exists(file)) FileSystem.DeleteDirectory(file, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                else if (File.Exists(file)) FileSystem.DeleteFile(file, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }
            catch (Exception ex) { errors.Add($"{System.IO.Path.GetFileName(file)}: {ex.Message}"); }
        }
        RefreshFolder();
        if (errors.Count > 0) MessageBox.Show(this, string.Join("\n", errors), "Löschen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private static string? Prompt(string title, string instruction, string initial)
    {
        using var form = new Form { Text = title, StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, ClientSize = new Size(420, 145),
            MinimizeBox = false, MaximizeBox = false, Font = new Font("Segoe UI", 10) };
        form.Controls.Add(new Label { Text = instruction, Location = new Point(16, 15), AutoSize = true });
        var box = new TextBox { Text = initial }; box.SetBounds(16, 47, 388, 29); form.Controls.Add(box);
        var save = new Button { Text = "OK", DialogResult = DialogResult.OK }; save.SetBounds(307, 94, 97, 32); form.Controls.Add(save);
        var cancel = new Button { Text = "Abbrechen", DialogResult = DialogResult.Cancel }; cancel.SetBounds(195, 94, 105, 32); form.Controls.Add(cancel);
        form.AcceptButton = save; form.CancelButton = cancel;
        box.SelectAll();
        return form.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(box.Text) ? box.Text.Trim() : null;
    }

    private void GlobalKeys(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.L) { address.Focus(); address.SelectAll(); e.SuppressKeyPress = true; }
        else if (e.Alt && e.KeyCode == Keys.Left) { GoBack(); e.SuppressKeyPress = true; }
        else if (e.Alt && e.KeyCode == Keys.Right) { GoForward(); e.SuppressKeyPress = true; }
    }

    private void ListKeys(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.C) CopySelected(false);
        else if (e.Control && e.KeyCode == Keys.X) CopySelected(true);
        else if (e.Control && e.KeyCode == Keys.V) Paste();
        else if (e.Control && e.KeyCode == Keys.A) { foreach (ListViewItem item in list.Items) item.Selected = true; }
        else if (e.KeyCode == Keys.Delete) DeleteSelected();
        else if (e.KeyCode == Keys.F2) RenameSelected();
        else if (e.KeyCode == Keys.F5) RefreshFolder();
        else if (e.KeyCode == Keys.Enter) OpenSelected();
        else if (e.KeyCode == Keys.Back) GoUp();
        else return;
        e.SuppressKeyPress = true;
    }

    private void ShowError(string title, Exception ex) => MessageBox.Show(this, ex.Message, title,
        MessageBoxButtons.OK, MessageBoxIcon.Error);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint attrs, ref SHFILEINFO info,
        uint cbSizeFileInfo, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    private int GetIconIndex(string path, bool isFolder)
    {
        string key = isFolder ? "__folder__" : System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(key)) key = "__file__";
        if (iconIndex.TryGetValue(key, out var found)) return found;
        var info = new SHFILEINFO();
        const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x1, SHGFI_USEFILEATTRIBUTES = 0x10;
        const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;
        try
        {
            string lookup = isFolder ? "folder" : "sample" + System.IO.Path.GetExtension(path);
            SHGetFileInfo(lookup, isFolder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL,
                ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);
            if (info.hIcon != IntPtr.Zero)
            {
                using var icon = (Icon)Icon.FromHandle(info.hIcon).Clone();
                icons.Images.Add(icon);
                int idx = icons.Images.Count - 1;
                iconIndex[key] = idx;
                return idx;
            }
        }
        catch { }
        finally { if (info.hIcon != IntPtr.Zero) DestroyIcon(info.hIcon); }
        if (icons.Images.Count == 0) icons.Images.Add(SystemIcons.Application);
        iconIndex[key] = 0;
        return 0;
    }
}
