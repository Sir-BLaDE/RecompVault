using System.IO;
using System.Text.Json;

namespace RecompVault.Services;

public class AppConfig
{
    public string RootPath { get; set; } = @"D:\Juegos\-RECOMPILED-DECOMPILED-";
    public List<string> Favorites { get; set; } = new();
    public Dictionary<string, string> LaunchOverrides { get; set; } = new(); // folderPath -> launchPath
    public Dictionary<string, string> CoverOverrides { get; set; } = new(); // folderPath -> imagePath
    public Dictionary<string, string> NameOverrides { get; set; } = new(); // folderPath -> custom display name
    public string? SteamGridDbApiKey { get; set; }
    public string? RawgApiKey { get; set; }
    public string? IgdbClientId { get; set; }
    public string? IgdbClientSecret { get; set; }
    public bool AutoCoverAttempted { get; set; }
    public List<string> Hidden { get; set; } = new();
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;

    // Quiver integration
    public string QuiverPath { get; set; } = ""; // auto-detect if empty
    public bool QuiverSyncEnabled { get; set; } = true;
    public bool AutoUpdateRecomps { get; set; } = true;
    public bool AutoCheckUpdates { get; set; } = true;
    public string? GitHubToken { get; set; } // opcional, para evitar límites de la API de GitHub
}

public static class ConfigService
{
    private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RecompVault");
    private static readonly string File = Path.Combine(Dir, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (System.IO.File.Exists(File))
            {
                var json = System.IO.File.ReadAllText(File);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                if (cfg != null) return cfg;
            }
        }
        catch { }
        return new AppConfig();
    }

    public static void Save(AppConfig cfg)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(File, json);
        }
        catch { }
    }
}