using System.Collections;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace MeinExplorer;

internal sealed class Shortcut
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsSuperFavorite { get; set; }
}

internal sealed class UserSettings
{
    public List<Shortcut> Favorites { get; set; } = new();
    public string StartFolder { get; set; } = "";
    public int SidebarWidth { get; set; } = 260;
    public int WindowWidth { get; set; } = 1180;
    public int WindowHeight { get; set; } = 740;

    public static string SettingsFile => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MeinExplorer", "settings.json");

    public static UserSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
                {
                    var loaded = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(SettingsFile)) ?? Default();
                    loaded.RemoveDuplicateFavorites();
                    return loaded;
                }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Einstellungen konnten nicht gelesen werden:\n{ex.Message}",
                "MeinExplorer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        return Default();
    }

    private static UserSettings Default()
    {
        var x = new UserSettings();
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var downloads = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        x.Favorites.Add(new Shortcut { Name = "Downloads", Path = downloads });
        x.Favorites.Add(new Shortcut { Name = "Dokumente", Path = docs });
        foreach (var pair in new[] { ("Daten", @"C:\Daten"), ("Projekte", @"C:\Projekte"), ("Formulare", @"C:\Daten\Formulare") })
            x.Favorites.Add(new Shortcut { Name = pair.Item1, Path = pair.Item2 });
        return x;
    }

    // Jeder Anzeigename und jeder Zielordner darf in der Favoritenliste nur einmal vorkommen.
    // Wenn eine Dublette als Super-Favorit markiert war, bleibt diese Markierung erhalten.
    public void RemoveDuplicateFavorites()
    {
        var unique = new List<Shortcut>();
        foreach (var favorite in Favorites ?? new List<Shortcut>())
        {
            if (string.IsNullOrWhiteSpace(favorite.Name) || string.IsNullOrWhiteSpace(favorite.Path))
                continue;

            string name = favorite.Name.Trim();
            string path = NormalizeFavoritePath(favorite.Path);
            var existing = unique.FirstOrDefault(x =>
                string.Equals(x.Name, name, StringComparison.CurrentCultureIgnoreCase) ||
                string.Equals(NormalizeFavoritePath(x.Path), path, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                existing.IsSuperFavorite |= favorite.IsSuperFavorite;
                continue;
            }

            favorite.Name = name;
            favorite.Path = favorite.Path.Trim();
            unique.Add(favorite);
        }
        Favorites = unique;
    }

    public static string NormalizeFavoritePath(string path)
    {
        try
        {
            string full = System.IO.Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
            return System.IO.Path.TrimEndingDirectorySeparator(full);
        }
        catch { return path.Trim().TrimEnd('\\', '/'); }
    }

    public void Save()
    {
        RemoveDuplicateFavorites();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsFile)!);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        var temp = SettingsFile + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, SettingsFile, true);
    }
}

