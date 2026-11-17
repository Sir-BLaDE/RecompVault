using System.IO;
using System.Text.Json;
using RecompVault.Models;

namespace RecompVault.Services;

public static class ScannerService
{
    private static readonly HashSet<string> ExcludedSystems = new(StringComparer.OrdinalIgnoreCase)
    { "native-console-windows-x64" };

    private static Dictionary<string, string>? _titleMap;
    private static Dictionary<string, string> TitleMap
    {
        get
        {
            if (_titleMap == null) _titleMap = LoadTitleMap();
            return _titleMap;
        }
    }

    private static Dictionary<string, string> LoadTitleMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var asm = typeof(ScannerService).Assembly;
            var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("gametitles.json", StringComparison.OrdinalIgnoreCase));
            string? json = null;
            if (resName != null)
            {
                using var s = asm.GetManifestResourceStream(resName);
                if (s != null) using (var r = new StreamReader(s)) json = r.ReadToEnd();
            }
            if (json == null)
            {
                // fallback file next to exe
                var p = Path.Combine(AppContext.BaseDirectory, "gametitles.json");
                if (File.Exists(p)) json = File.ReadAllText(p);
            }
            if (json != null)
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var prop in doc.RootElement.EnumerateObject())
                    map[prop.Name] = prop.Value.GetString() ?? prop.Name;
            }
        }
        catch { }
        return map;
    }

    private static readonly HashSet<string> ExcludedExeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "hactool","hactool.exe","zig","zig.exe","python","python.exe","pythonw.exe","pythonw",
        "chdman","chdman.exe","ffmpeg","ffmpeg.exe","clang","clang.exe","lld-link","lld-link.exe",
        "ninja","ninja.exe","tcc","tcc.exe","i386-win32-tcc.exe","armips","armips.exe",
        "torch","torch.exe","createdump","createdump.exe","elevate","elevate.exe",
        "unins000","unins000.exe","setup","setup.exe","uninstall","uninstall.exe",
        "vc_redist.x64.exe","exiso","exiso.exe","xdvdfs","xdvdfs.exe","rexglue","rexglue.exe",
        "nodtool","nodtool.exe","waifu2x-ncnn-vulkan","waifu2x-ncnn-vulkan.exe"
    };

    public static List<GameEntry> Scan(string root, AppConfig cfg)
    {
        var result = new List<GameEntry>();
        if (!Directory.Exists(root)) return result;

        // load apps.json mapping
        var appMap = LoadAppsJson(root);

        var systemDirs = Directory.GetDirectories(root)
            .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var sysDir in systemDirs)
        {
            var sysName = Path.GetFileName(sysDir);
            if (ExcludedSystems.Contains(sysName)) continue;
            if (sysName.StartsWith("QuiverLauncher", StringComparison.OrdinalIgnoreCase)) continue;
            if (sysName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var gameDir in Directory.GetDirectories(sysDir))
            {
                var folderName = Path.GetFileName(gameDir);
                // skip hidden-ish
                if (folderName.StartsWith(".")) continue;

                // match apps.json
                string gameTitle = CleanName(folderName);
                string recompName = CleanName(folderName);
                if (appMap.TryGetValue(Norm(gameDir), out var app) || appMap.TryGetValue(folderName, out app))
                {
                    if (!string.IsNullOrWhiteSpace(app.Name)) gameTitle = app.Name;
                    // recomp name = folder name (project identifier)
                    recompName = folderName;
                }
                else if (TitleMap.TryGetValue(folderName, out var mapped))
                {
                    gameTitle = mapped;
                    recompName = folderName;
                }

                // find launch candidate
                string launch = "";
                if (cfg.LaunchOverrides.TryGetValue(gameDir, out var ov) && File.Exists(ov)) launch = ov;
                else launch = FindLaunchExe(gameDir, folderName);

                bool isHidden = cfg.Hidden.Contains(gameDir);

                // skip completely empty dirs with no exe? keep but mark; but skip if no exe AND empty
                bool hasAnyFile = false;
                try { hasAnyFile = Directory.EnumerateFiles(gameDir, "*", SearchOption.TopDirectoryOnly).Any() || Directory.GetDirectories(gameDir).Length > 0; } catch { }
                if (string.IsNullOrEmpty(launch) && !hasAnyFile) continue;

                var entry = new GameEntry
                {
                    Name = gameTitle,
                    RecompName = recompName,
                    System = sysName,
                    FolderPath = gameDir,
                    LaunchPath = launch,
                    Notes = app?.Notes,
                    IsHidden = isHidden
                };
                if (cfg.NameOverrides.TryGetValue(gameDir, out var customName) && !string.IsNullOrWhiteSpace(customName))
                    entry.Name = customName.Trim();
                ArtworkService.Apply(entry, cfg);
                result.Add(entry);
            }
        }

        // apply favorites and Quiver enrichment
        var quiverPath = cfg.QuiverSyncEnabled ? QuiverIntegrationService.ResolveQuiverPath(cfg) : null;
        Dictionary<string, QuiverAppInfo>? qApps = null;
        Dictionary<string, QuiverVersionInfo>? qVers = null;
        Dictionary<string, QuiverCatalogEntry>? qCatalog = null;
        if (!string.IsNullOrEmpty(quiverPath))
        {
            qApps = QuiverIntegrationService.LoadApps(quiverPath);
            qVers = QuiverIntegrationService.LoadVersionCache(quiverPath);
            qCatalog = QuiverIntegrationService.LoadCatalogIndex(quiverPath);
        }
        foreach (var g in result)
        {
            g.Favorite = cfg.Favorites.Contains(g.FolderPath);
            QuiverAppInfo? info = null;
            if (qApps != null) info = QuiverIntegrationService.Match(g, qApps);
            if (info != null)
            {
                if (!cfg.NameOverrides.ContainsKey(g.FolderPath) && !string.IsNullOrWhiteSpace(info.Name))
                    g.Name = info.Name;
                QuiverIntegrationService.Enrich(g, info, quiverPath!);
                if (!g.HasCover && !string.IsNullOrWhiteSpace(g.QuiverLocalIconPath) && File.Exists(g.QuiverLocalIconPath))
                    TryApplyCoverFile(g, g.QuiverLocalIconPath);
            }
            else if (qCatalog != null)
            {
                var ce = QuiverIntegrationService.MatchCatalog(g, qCatalog);
                if (ce != null)
                {
                    // recognized via Quiver catalog DB (repo/update info)
                    g.Repository = string.IsNullOrWhiteSpace(g.Repository) ? ce.Repository : g.Repository;
                    QuiverIntegrationService.EnrichFromCatalog(g, ce);
                    // try to find local icon by catalog folderName
                    if (!string.IsNullOrEmpty(quiverPath))
                    {
                        var icon = QuiverIntegrationService.FindLocalIcon(quiverPath, ce.FolderName, g.FolderPath);
                        if (!string.IsNullOrWhiteSpace(icon))
                        {
                            g.QuiverLocalIconPath = icon;
                            if (!g.HasCover) TryApplyCoverFile(g, icon);
                        }
                    }
                }
            }
            if (qVers != null) QuiverIntegrationService.ApplyUpdateInfo(g, qVers);
        }

        return result.OrderBy(g => g.System).ThenBy(g => g.Name).ToList();
    }

    private static string CleanName(string folder)
    {
        var s = folder;
        // remove version-ish suffixes in parentheses/brackets? keep simple: replace underscores/hyphens
        s = s.Replace("_", " ");
        // collapse multiple spaces
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Trim();
    }

    private static string FindLaunchExe(string gameDir, string folderName)
    {
        var candidates = new List<(string path, int score)>();
        try
        {
            var heavy = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "romfs", "exefs", "assets", "cache", "extracted", "nup_dump", "mods", "save", "logs", "debug" };
            var files = new List<string>();
            var stack = new Stack<(string dir, int depth)>();
            stack.Push((gameDir, 0));
            int count = 0;
            while (stack.Count > 0 && count < 2500)
            {
                var (d, depth) = stack.Pop();
                if (depth > 3) continue;
                string[] fs = Array.Empty<string>();
                try { fs = Directory.GetFiles(d); } catch { }
                foreach (var f in fs)
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".exe" || ext == ".bat" || ext == ".cmd")
                    {
                        files.Add(f);
                        count++;
                        if (count >= 2500) break;
                    }
                }
                if (depth < 3)
                {
                    string[] subs = Array.Empty<string>();
                    try { subs = Directory.GetDirectories(d); } catch { }
                    foreach (var sub in subs)
                    {
                        var bn = Path.GetFileName(sub);
                        if (heavy.Contains(bn)) continue;
                        stack.Push((sub, depth + 1));
                    }
                }
            }

            foreach (var f in files)
            {
                var name = Path.GetFileName(f).ToLowerInvariant();
                if (ExcludedExeNames.Contains(name)) continue;
                if (name.Contains("setup") || name.Contains("uninstall") || name.Contains("install")) continue;
                if (name.Contains("generator") || name.Contains("extract") || name.Contains("tool")) continue;

                int score = 0;
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".exe") score += 5;
                if (name.Contains("launcher")) score += 100;
                if (name.Contains("recomp")) score += 50;
                if (name.Contains("port")) score += 30;
                // root-level bonus
                if (Path.GetDirectoryName(f) == gameDir) score += 25;
                // game token bonus
                var token = new string(folderName.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
                if (token.Length > 3 && name.Contains(token.Substring(0, Math.Min(6, token.Length)))) score += 40;
                // size
                try
                {
                    var len = new FileInfo(f).Length;
                    score += (int)Math.Min(60, len / (1024 * 1024));
                }
                catch { }
                // bat/cmd slight penalty vs exe
                if (ext != ".exe") score -= 10;

                candidates.Add((f, score));
            }
        }
        catch { }

        if (candidates.Count == 0) return "";
        return candidates.OrderByDescending(c => c.score).First().path;
    }

    private class AppInfo { public string Name { get; set; } = ""; public string? Notes { get; set; } }

    private static Dictionary<string, AppInfo> LoadAppsJson(string root)
    {
        var map = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(root, "QuiverLauncher-win-Portable", "apps.json");
            if (!File.Exists(path)) return map;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("apps", out var apps)) return map;
            foreach (var a in apps.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var folder = a.TryGetProperty("folderName", out var f) ? f.GetString() ?? "" : "";
                var install = a.TryGetProperty("installPath", out var ip) ? ip.GetString() ?? "" : "";
                var info = new AppInfo { Name = name };
                if (!string.IsNullOrWhiteSpace(install))
                {
                    var norm = Norm(install.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    map[norm] = info;
                }
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    if (!map.ContainsKey(folder)) map[folder] = info;
                }
            }
        }
        catch { }
        return map;
    }

    private static void TryApplyCoverFile(GameEntry g, string path)
    {
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess()) dispatcher.Invoke(() => g.CoverImage = bmp);
            else g.CoverImage = bmp;
        }
        catch { }
    }

    private static string Norm(string p) => p.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
}