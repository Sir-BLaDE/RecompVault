using System.IO;
using System.Text.Json;
using RecompVault.Core;
using RecompVault.Models;

namespace RecompVault.Services;

/// <summary>
/// Copia en disco de la última biblioteca escaneada (%APPDATA%\RecompVault\library_cache.json).
/// Al arrancar se muestra al instante y el escaneo posterior solo corrige lo que haya cambiado.
/// </summary>
public static class LibraryCacheService
{
    private const int FormatVersion = 1;
    public static readonly string FilePath = Path.Combine(ConfigService.Dir, "library_cache.json");
    private static readonly object FileLock = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, PropertyNameCaseInsensitive = true };

    public sealed class CacheFile
    {
        public int Format { get; set; } = FormatVersion;
        public string RootPath { get; set; } = "";
        public DateTime SavedUtc { get; set; }
        public List<CachedGame> Games { get; set; } = new();
    }

    public sealed class CachedGame
    {
        public string FolderPath { get; set; } = "";
        public string System { get; set; } = "";
        public string Name { get; set; } = "";
        public string RecompName { get; set; } = "";
        public string NameSource { get; set; } = "";
        public bool NameFromTitleMap { get; set; }
        public string LaunchPath { get; set; } = "";
        public string LaunchSource { get; set; } = "";
        public string InstalledVersion { get; set; } = "";
        public string? InstalledAssetName { get; set; }
        public string LatestVersion { get; set; } = "";
        public string? LatestNotes { get; set; }
        public string? Repository { get; set; }
        public string Provider { get; set; } = "github";
        public string? Project { get; set; }
        public string? QuiverFolderName { get; set; }
        public string? InstallDir { get; set; }
        public string? QuiverAppIconUrl { get; set; }
        public string? QuiverLocalIconPath { get; set; }
        public string? AssetFilter { get; set; }
        public string LinkSource { get; set; } = "";
        public bool IsExternal { get; set; }
        public bool HasUndo { get; set; }
        public bool UpdatedByOldVersion { get; set; }
        public string Fingerprint { get; set; } = "";
        public string PinnedVersion { get; set; } = "";
        public DateTime LastPlayed { get; set; }
        public long PlaySeconds { get; set; }
        public string? CoverPath { get; set; }
        public long DirStampTicks { get; set; }
    }

    public static void Save(IEnumerable<GameEntry> games, string root)
    {
        try
        {
            var file = new CacheFile
            {
                RootPath = root,
                SavedUtc = DateTime.UtcNow,
                Games = games.Select(g => new CachedGame
                {
                    FolderPath = g.FolderPath, System = g.System, Name = g.Name, RecompName = g.RecompName,
                    NameSource = g.NameSource, NameFromTitleMap = g.NameFromTitleMap, LaunchPath = g.LaunchPath,
                    LaunchSource = g.LaunchSource, InstalledVersion = g.InstalledVersion, InstalledAssetName = g.InstalledAssetName,
                    LatestVersion = g.LatestVersion, LatestNotes = g.LatestNotes, Repository = g.Repository, Provider = g.Provider,
                    Project = g.Project, QuiverFolderName = g.QuiverFolderName, InstallDir = g.InstallDir,
                    QuiverAppIconUrl = g.QuiverAppIconUrl, QuiverLocalIconPath = g.QuiverLocalIconPath, AssetFilter = g.AssetFilter,
                    LinkSource = g.LinkSource, IsExternal = g.IsExternal, HasUndo = g.HasUndo, UpdatedByOldVersion = g.UpdatedByOldVersion,
                    Fingerprint = g.Fingerprint, PinnedVersion = g.PinnedVersion, LastPlayed = g.LastPlayed, PlaySeconds = g.PlaySeconds,
                    CoverPath = g.CoverPath, DirStampTicks = g.DirStampTicks
                }).ToList()
            };
            var json = JsonSerializer.Serialize(file, Json);
            lock (FileLock)
            {
                Directory.CreateDirectory(ConfigService.Dir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null, true);
                else File.Move(tmp, FilePath);
            }
        }
        catch (Exception ex) { Log.Warn("No se pudo guardar la caché de la biblioteca: " + ex.Message); }
    }

    /// <summary>Juegos guardados para esta carpeta raíz (vacío si no hay caché o es de otra raíz).</summary>
    public static List<GameEntry> Load(string root, AppConfig cfg)
    {
        try
        {
            CacheFile? file;
            lock (FileLock)
            {
                if (!File.Exists(FilePath)) return new();
                file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(FilePath), Json);
            }
            if (file == null || file.Format != FormatVersion || PathUtil.Norm(file.RootPath) != PathUtil.Norm(root)) return new();
            var list = new List<GameEntry>(file.Games.Count);
            foreach (var c in file.Games)
            {
                if (string.IsNullOrWhiteSpace(c.FolderPath)) continue;
                var g = new GameEntry
                {
                    FolderPath = c.FolderPath, System = c.System, Name = c.Name, RecompName = c.RecompName,
                    NameSource = c.NameSource, NameFromTitleMap = c.NameFromTitleMap, LaunchPath = c.LaunchPath,
                    LaunchSource = c.LaunchSource, InstalledAssetName = c.InstalledAssetName, LatestNotes = c.LatestNotes,
                    Repository = c.Repository, Provider = c.Provider, Project = c.Project, QuiverFolderName = c.QuiverFolderName,
                    InstallDir = c.InstallDir, QuiverAppIconUrl = c.QuiverAppIconUrl, QuiverLocalIconPath = c.QuiverLocalIconPath,
                    AssetFilter = c.AssetFilter, IsExternal = c.IsExternal, HasUndo = c.HasUndo,
                    UpdatedByOldVersion = c.UpdatedByOldVersion, Fingerprint = c.Fingerprint, LastPlayed = c.LastPlayed,
                    PlaySeconds = c.PlaySeconds, CoverPath = c.CoverPath, DirStampTicks = c.DirStampTicks
                };
                g.InstalledVersion = c.InstalledVersion;
                g.LatestVersion = c.LatestVersion;
                g.PinnedVersion = c.PinnedVersion;
                g.LinkSource = string.IsNullOrEmpty(c.LinkSource) ? "Sin vincular" : c.LinkSource;
                // lo que el usuario cambie en ajustes manda sobre la caché
                lock (ConfigService.Sync)
                {
                    g.Favorite = ConfigService.ListContains(cfg, cfg.Favorites, g.FolderPath);
                    g.IsHidden = ConfigService.ListContains(cfg, cfg.Hidden, g.FolderPath);
                    var custom = ConfigService.Get(cfg.NameOverrides, g.FolderPath);
                    if (!string.IsNullOrWhiteSpace(custom)) { g.Name = custom.Trim(); g.NameSource = "Manual"; }
                }
                list.Add(g);
            }
            return list;
        }
        catch (Exception ex)
        {
            Log.Warn("Caché de la biblioteca ilegible, se escaneará de cero: " + ex.Message);
            return new();
        }
    }

    /// <summary>Carga portadas e iconos de los juegos de la caché en segundo plano (no bloquea el arranque).</summary>
    public static Task LoadArtworkAsync(IReadOnlyList<GameEntry> games, AppConfig cfg, CancellationToken ct) => Task.Run(() =>
    {
        var opts = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 6), CancellationToken = ct };
        try
        {
            Parallel.ForEach(games, opts, g =>
            {
                if (g.CoverImage != null) return;
                try { ArtworkService.Apply(g, cfg); } catch { }
            });
        }
        catch (OperationCanceledException) { }
    }, ct);

    public static void Delete()
    {
        try { lock (FileLock) if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }
}
