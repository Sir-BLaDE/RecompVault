using System.IO;
using System.Text.Json;
using RecompVault.Core;
using RecompVault.Models;

namespace RecompVault.Services;

/// <summary>
/// Escanea la carpeta raíz. Estructura esperada: Raíz\Sistema\Juego, pero también detecta
/// juegos un nivel más abajo (Sistema\Grupo\Juego), juegos sueltos dentro de la carpeta del
/// sistema y, si se activa, las apps instaladas por Quiver fuera de la raíz.
/// </summary>
public static class ScannerService
{
    private static readonly HashSet<string> ExcludedSystems = new(StringComparer.OrdinalIgnoreCase)
    { "native-console-windows-x64", "$recycle.bin", "system volume information", "_backup", "backup", "temp", "tmp" };

    private static Dictionary<string, string>? _titleMap;
    private static Dictionary<string, string>? _titleMapByKey;

    private static void EnsureTitleMap()
    {
        if (_titleMap != null) return;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var asm = typeof(ScannerService).Assembly;
            var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("gametitles.json", StringComparison.OrdinalIgnoreCase));
            string? json = null;
            if (resName != null)
            {
                using var s = asm.GetManifestResourceStream(resName);
                if (s != null) { using var r = new StreamReader(s); json = r.ReadToEnd(); }
            }
            // Un gametitles.json junto al exe o en %APPDATA%\RecompVault amplía/sobrescribe el embebido.
            foreach (var p in new[] { Path.Combine(AppContext.BaseDirectory, "gametitles.json"), Path.Combine(ConfigService.Dir, "gametitles.json") })
            {
                if (json != null) Merge(map, json);
                json = File.Exists(p) ? File.ReadAllText(p) : null;
            }
            if (json != null) Merge(map, json);
        }
        catch (Exception ex) { Log.Warn("gametitles.json: " + ex.Message); }
        var byKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in map)
        {
            var k = LaunchPicker.TitleKey(kv.Key);
            if (k.Length >= 4) byKey.TryAdd(k, kv.Value);
        }
        _titleMapByKey = byKey;
        _titleMap = map;
    }

    private static void Merge(Dictionary<string, string> map, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, JsonUtil.Options);
            foreach (var prop in doc.RootElement.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(prop.Value.GetString()))
                    map[prop.Name] = prop.Value.GetString()!;
        }
        catch { }
    }

    private static string? MappedTitle(string folderName)
    {
        EnsureTitleMap();
        if (_titleMap!.TryGetValue(folderName, out var t)) return t;
        var k = LaunchPicker.TitleKey(folderName);
        return k.Length >= 4 && _titleMapByKey!.TryGetValue(k, out t) ? t : null;
    }

    public static List<GameEntry> Scan(string root, AppConfig cfg, QuiverLibrary? quiver)
    {
        var result = new List<GameEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var quiverRootNorm = quiver != null ? PathUtil.Norm(quiver.Root) : "";

        if (Directory.Exists(root))
        {
            foreach (var sysDir in Safe(() => Directory.GetDirectories(root)).OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
            {
                var sysName = Path.GetFileName(sysDir);
                if (IsSkippedDir(sysName) || ExcludedSystems.Contains(sysName)) continue;
                if (sysName.StartsWith("QuiverLauncher", StringComparison.OrdinalIgnoreCase)) continue;
                if (quiverRootNorm.Length > 0 && PathUtil.IsSameOrInside(quiver!.Root, sysDir)) continue;

                foreach (var gameDir in FindGameDirs(sysDir))
                {
                    if (!seen.Add(PathUtil.Norm(gameDir))) continue;
                    var entry = CreateEntry(gameDir, sysName, cfg);
                    if (entry != null) result.Add(entry);
                }
            }
        }

        // Quiver: emparejar (primero exactos, luego difusos sin repetir app) y añadir instaladas fuera de la raíz
        if (quiver != null)
        {
            var taken = new HashSet<QuiverApp>();
            var pending = new List<GameEntry>();
            foreach (var g in result)
            {
                var m = QuiverMatcher.MatchExact(quiver, g.FolderPath);
                if (m != null) { QuiverService.Enrich(g, m, cfg); if (m.App != null) taken.Add(m.App); }
                else pending.Add(g);
            }
            foreach (var g in pending)
            {
                var m = QuiverMatcher.Match(quiver, g.FolderPath, g.Name, taken);
                if (m == null) continue;
                QuiverService.Enrich(g, m, cfg);
                if (m.App != null) taken.Add(m.App);
            }

            if (cfg.IncludeQuiverAppsOutsideRoot)
            {
                foreach (var app in quiver.Apps)
                {
                    if (app.InstallPath.Length == 0 || !Directory.Exists(app.InstallPath)) continue;
                    if (!seen.Add(PathUtil.Norm(app.InstallPath))) continue;
                    if (!string.IsNullOrWhiteSpace(root) && PathUtil.IsSameOrInside(app.InstallPath, root) && taken.Contains(app)) continue;
                    if (File.Exists(Path.Combine(app.InstallPath, "install-incomplete.txt"))) continue;
                    var entry = CreateEntry(app.InstallPath, SystemFromTags(app), cfg, allowEmpty: false);
                    if (entry == null) continue;
                    entry.IsExternal = !PathUtil.IsSameOrInside(app.InstallPath, root);
                    QuiverService.Enrich(entry, new QuiverMatch(app, quiver.CatalogByRepo(app.Repository)), cfg);
                    result.Add(entry);
                }
            }
        }

        foreach (var g in result)
        {
            g.Favorite = ConfigService.ListContains(cfg, cfg.Favorites, g.FolderPath);
            g.IsHidden = ConfigService.ListContains(cfg, cfg.Hidden, g.FolderPath);
            lock (ConfigService.Sync) { if (cfg.LastPlayed.TryGetValue(g.FolderPath, out var lp)) g.LastPlayed = lp; }
            var custom = ConfigService.Get(cfg.NameOverrides, g.FolderPath);
            if (!string.IsNullOrWhiteSpace(custom)) g.Name = custom.Trim();
            ArtworkService.Apply(g, cfg, detached: true);
        }

        return result.OrderBy(g => g.System, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Carpetas de juego dentro de un sistema:
    /// - cada subcarpeta con ejecutable (hasta 3 niveles) es un juego;
    /// - una subcarpeta sin ejecutable propio pero con subcarpetas que sí lo tienen es un "grupo";
    /// - si el sistema tiene ejecutables sueltos en su raíz y ninguna subcarpeta de juego, el sistema es el juego.
    /// </summary>
    private static IEnumerable<string> FindGameDirs(string sysDir)
    {
        var games = new List<string>();
        foreach (var dir in Safe(() => Directory.GetDirectories(sysDir)))
        {
            var name = Path.GetFileName(dir);
            if (IsSkippedDir(name)) continue;

            if (LaunchPicker.HasExecutable(dir, 3) || HasQuiverMarker(dir)) { games.Add(dir); continue; }

            var nested = Safe(() => Directory.GetDirectories(dir)).Where(d => !IsSkippedDir(Path.GetFileName(d)) && LaunchPicker.HasExecutable(d, 3)).ToList();
            if (nested.Count >= 2) games.AddRange(nested);           // grupo de juegos
            else if (nested.Count == 1) games.Add(dir);               // envoltorio de un solo juego
            else if (HasAnyContent(dir)) games.Add(dir);              // sin ejecutable: se lista para poder elegirlo a mano
        }
        if (games.Count == 0 && Safe(() => Directory.GetFiles(sysDir)).Any(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            games.Add(sysDir);
        return games;
    }

    private static bool HasQuiverMarker(string dir) => File.Exists(Path.Combine(dir, "version.txt")) || File.Exists(Path.Combine(dir, "selected_executable.txt"));

    private static bool HasAnyContent(string dir)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(dir).Any(e =>
                !Path.GetFileName(e).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) &&
                !Path.GetFileName(e).Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static bool IsSkippedDir(string name) =>
        name.StartsWith('.') || name.StartsWith('$') || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase);

    private static GameEntry? CreateEntry(string gameDir, string sysName, AppConfig cfg, bool allowEmpty = true)
    {
        var folderName = Path.GetFileName(gameDir.TrimEnd('\\', '/'));
        var launch = ResolveLaunch(gameDir, folderName, cfg);
        if (string.IsNullOrEmpty(launch) && (!allowEmpty || !HasAnyContent(gameDir))) return null;

        var mapped = MappedTitle(folderName);
        var entry = new GameEntry
        {
            Name = mapped ?? LaunchPicker.CleanTitle(folderName),
            RecompName = folderName,
            System = sysName,
            FolderPath = gameDir,
            LaunchPath = launch,
        };
        entry.InstalledVersion = UpdateService.ReadInstalledVersion(gameDir) ?? "";
        entry.InstalledAssetName = UpdateService.ReadInstalledAssetName(gameDir);
        return entry;
    }

    /// <summary>Ejecutable: elegido por el usuario aquí → elegido en Quiver (selected_executable.txt) → heurística.</summary>
    public static string ResolveLaunch(string gameDir, string folderName, AppConfig cfg)
    {
        var ov = ConfigService.Get(cfg.LaunchOverrides, gameDir);
        if (ov != null && File.Exists(ov)) return ov;
        try
        {
            var sel = Path.Combine(gameDir, "selected_executable.txt");
            if (File.Exists(sel))
            {
                var p = File.ReadAllText(sel).Trim().Trim('"');
                if (p.Length > 0)
                {
                    if (!Path.IsPathRooted(p)) p = Path.Combine(gameDir, p);
                    if (File.Exists(p)) return Path.GetFullPath(p);
                }
            }
        }
        catch { }
        return LaunchPicker.Pick(gameDir, folderName);
    }

    private static string SystemFromTags(QuiverApp app)
    {
        var known = new[] { "N64", "GC", "GBA", "SNES", "PSX", "PS2", "PS3", "PS4", "X360", "XBOX", "WII", "WIIU", "DS", "3DS", "SWITCH", "MEGADRIVE", "PCE", "Arcade", "PC", "GB", "GBC", "NES", "DREAMCAST", "SATURN" };
        foreach (var t in app.Tags)
        {
            var hit = known.FirstOrDefault(k => string.Equals(k, t, StringComparison.OrdinalIgnoreCase) || string.Equals(PathUtil.NormName(k), PathUtil.NormName(t), StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
            var nt = PathUtil.NormName(t);
            if (nt is "nintendo64") return "N64";
            if (nt is "gamecube") return "GC";
            if (nt is "gameboyadvance") return "GBA";
            if (nt is "playstation" or "ps1") return "PSX";
            if (nt is "playstation2") return "PS2";
            if (nt is "xbox360") return "X360";
            if (nt is "genesis" or "segagenesis") return "MEGADRIVE";
        }
        return "Quiver";
    }

    private static string[] Safe(Func<string[]> f)
    {
        try { return f(); } catch { return Array.Empty<string>(); }
    }
}
