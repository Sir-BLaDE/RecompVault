using System.IO;
using System.Text.Json;
using System.Linq;
using RecompVault.Models;

namespace RecompVault.Services;

public class QuiverAppInfo
{
    public string Name { get; set; } = "";
    public string FolderName { get; set; } = "";
    public string InstallPath { get; set; } = "";
    public string AppIconUrl { get; set; } = "";
    public string Repository { get; set; } = "";
    public string Project { get; set; } = "";
    public List<string> Tags { get; set; } = new();
}

public class QuiverVersionInfo
{
    public string Version { get; set; } = ""; // latest/upstream
    public string InstalledOrVerified { get; set; } = "";
    public string AssetUrl { get; set; } = "";
    public string AssetName { get; set; } = "";
    public string Digest { get; set; } = "";
    public bool UpdateAvailable { get; set; }
}

public class QuiverCatalogEntry
{
    public string Slug { get; set; } = "";
    public string FolderName { get; set; } = "";
    public string Repository { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Verified { get; set; } = "";
    public string Latest { get; set; } = "";
    public bool UpdateAvailable { get; set; }
}

public static class QuiverIntegrationService
{
    public static string? DetectQuiverPath(string root)
    {
        try
        {
            var candidate = Path.Combine(root, "QuiverLauncher-win-Portable");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "apps.json"))) return candidate;
            // search shallow
            foreach (var d in Directory.GetDirectories(root, "QuiverLauncher*", SearchOption.TopDirectoryOnly))
                if (File.Exists(Path.Combine(d, "apps.json"))) return d;
        }
        catch { }
        return null;
    }

    public static string? ResolveQuiverPath(AppConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.QuiverPath) && Directory.Exists(cfg.QuiverPath)) return cfg.QuiverPath;
        var det = DetectQuiverPath(cfg.RootPath);
        if (!string.IsNullOrEmpty(det)) { cfg.QuiverPath = det; return det; }
        return null;
    }

    public static Dictionary<string, QuiverAppInfo> LoadApps(string quiverPath)
    {
        var map = new Dictionary<string, QuiverAppInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(quiverPath, "apps.json");
            if (!File.Exists(path)) return map;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("apps", out var apps)) return map;
            foreach (var a in apps.EnumerateArray())
            {
                var info = new QuiverAppInfo
                {
                    Name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    FolderName = a.TryGetProperty("folderName", out var f) ? f.GetString() ?? "" : "",
                    InstallPath = a.TryGetProperty("installPath", out var ip) ? (ip.GetString() ?? "") : "",
                    AppIconUrl = a.TryGetProperty("appIconUrl", out var u) ? (u.GetString() ?? "") : "",
                    Repository = a.TryGetProperty("repository", out var r) ? (r.GetString() ?? "") : "",
                    Project = a.TryGetProperty("project", out var p) ? (p.GetString() ?? "") : "",
                };
                if (string.IsNullOrWhiteSpace(info.AppIconUrl) && a.TryGetProperty("catalog", out var c) && c.TryGetProperty("appIconUrl", out var cu))
                    info.AppIconUrl = cu.GetString() ?? "";
                if (a.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
                    foreach (var t in tags.EnumerateArray()) { var ts = t.GetString(); if (!string.IsNullOrWhiteSpace(ts)) info.Tags.Add(ts); }
                if (!string.IsNullOrWhiteSpace(info.InstallPath))
                {
                    var norm = Norm(info.InstallPath);
                    if (!map.ContainsKey(norm)) map[norm] = info;
                }
                if (!string.IsNullOrWhiteSpace(info.FolderName) && !map.ContainsKey("folder:" + info.FolderName))
                    map["folder:" + info.FolderName] = info;
            }
        }
        catch { }
        return map;
    }

    public static QuiverAppInfo? Match(GameEntry g, Dictionary<string, QuiverAppInfo> apps)
    {
        // 1) exact/path matches (rápidos)
        if (apps.TryGetValue(Norm(g.FolderPath), out var a)) return a;
        var folderName = Path.GetFileName(g.FolderPath);
        if (apps.TryGetValue("folder:" + folderName, out a)) return a;
        foreach (var kv in apps)
        {
            if (kv.Key.StartsWith("folder:", StringComparison.OrdinalIgnoreCase))
            {
                var fn = kv.Key.Substring(7);
                if (string.Equals(fn, folderName, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            }
        }
        var gn = Norm(g.FolderPath);
        foreach (var kv in apps)
        {
            if (!kv.Key.StartsWith("folder:", StringComparison.OrdinalIgnoreCase))
            {
                if (gn.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase) || kv.Key.StartsWith(gn, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            }
        }
        var nfn = NormName(folderName);
        if (nfn.Length >= 5)
        {
            foreach (var kv in apps)
            {
                if (kv.Key.StartsWith("folder:", StringComparison.OrdinalIgnoreCase))
                {
                    var fn = kv.Key.Substring(7);
                    if (string.Equals(NormName(fn), nfn, StringComparison.OrdinalIgnoreCase)) return kv.Value;
                }
            }
        }

        // 2) coincidencia inteligente por nombre + contenido de carpeta (para carpetas no enlazadas por ruta)
        try
        {
            var hints = CollectFolderHints(g.FolderPath);
            var candidates = apps.Values
                .Where(v => !string.IsNullOrWhiteSpace(v.Name) || !string.IsNullOrWhiteSpace(v.FolderName) || !string.IsNullOrWhiteSpace(v.Repository))
                .GroupBy(v => (v.Repository ?? "") + "|" + (v.FolderName ?? "") + "|" + (v.Name ?? ""))
                .Select(gr => gr.First())
                .ToList();

            QuiverAppInfo? best = null; int bestScore = 0;
            foreach (var c in candidates)
            {
                int score = ScoreAppMatch(folderName, g.Name, hints, c);
                if (score > bestScore) { bestScore = score; best = c; }
            }
            // umbral conservador para evitar falsos positivos
            if (best != null && bestScore >= 120) return best;
        }
        catch { }

        return null;
    }

    public static string NormName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var chars = s.Where(char.IsLetterOrDigit).ToArray();
        return new string(chars).ToLowerInvariant();
    }

    private sealed class FolderHints
    {
        public HashSet<string> Tokens = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Norms = new(StringComparer.OrdinalIgnoreCase);
        public string ExeBlobNorm = "";
        public string TextBlob = ""; // lowercased small text snippets (readmes/configs)
    }

    private static FolderHints CollectFolderHints(string folderPath)
    {
        var h = new FolderHints();
        try
        {
            var folderName = Path.GetFileName(folderPath) ?? "";
            foreach (var t in Tokenize(folderName)) h.Tokens.Add(t);
            var nf = NormName(folderName);
            if (nf.Length >= 4) h.Norms.Add(nf);

            // ejecutables y scripts (nombres son señales fuertes: RingOut.exe, Goemon64Recompiled.exe, etc.)
            var names = new List<string>();
            try { names.AddRange(Directory.GetFiles(folderPath, "*.exe", SearchOption.TopDirectoryOnly).Select(Path.GetFileName)); } catch { }
            try { names.AddRange(Directory.GetFiles(folderPath, "*.bat", SearchOption.TopDirectoryOnly).Select(Path.GetFileName)); } catch { }
            try { names.AddRange(Directory.GetFiles(folderPath, "*.cmd", SearchOption.TopDirectoryOnly).Select(Path.GetFileName)); } catch { }
            try { names.AddRange(Directory.GetFiles(folderPath, "*.ps1", SearchOption.TopDirectoryOnly).Select(Path.GetFileName)); } catch { }
            // también mirar una capa dentro (algunas builds)
            try
            {
                foreach (var sub in Directory.GetDirectories(folderPath).Take(6))
                {
                    try { names.AddRange(Directory.GetFiles(sub, "*.exe", SearchOption.TopDirectoryOnly).Take(10).Select(Path.GetFileName)); } catch { }
                }
            }
            catch { }
            var exeBlob = string.Join(" ", names.Where(n => !string.IsNullOrWhiteSpace(n)));
            h.ExeBlobNorm = NormName(exeBlob);
            foreach (var n in names.Where(n => !string.IsNullOrWhiteSpace(n)))
            {
                foreach (var t in Tokenize(Path.GetFileNameWithoutExtension(n)!)) h.Tokens.Add(t);
                var nn = NormName(Path.GetFileNameWithoutExtension(n)!);
                if (nn.Length >= 4) h.Norms.Add(nn);
            }

            // pistas en textos pequeños (README, json, txt) buscando repo/nombres
            var textCandidates = new List<string>();
            foreach (var pat in new[] { "README*", "readme*", "*.txt", "*.json", "*.cfg", "*.ini" })
            {
                try { textCandidates.AddRange(Directory.GetFiles(folderPath, pat, SearchOption.TopDirectoryOnly)); } catch { }
            }
            var sb = new System.Text.StringBuilder();
            int read = 0;
            foreach (var f in textCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (read >= 6) break;
                try
                {
                    var fi = new FileInfo(f);
                    if (fi.Length > 200_000) continue;
                    var txt = File.ReadAllText(f);
                    if (string.IsNullOrWhiteSpace(txt)) continue;
                    if (sb.Length < 12000) sb.Append(' ').Append(txt.Length > 4000 ? txt.Substring(0, 4000) : txt);
                    read++;
                }
                catch { }
            }
            h.TextBlob = sb.ToString().ToLowerInvariant();
        }
        catch { }
        return h;
    }

    private static IEnumerable<string> Tokenize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) yield break;
        var cleaned = new string(s.Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ').ToArray());
        foreach (var raw in cleaned.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Trim();
            if (t.Length < 3) continue;
            // ignorar tokens muy genéricos/versiones puras
            if (t.All(char.IsDigit)) continue;
            if (GenericTokens.Contains(t)) continue;
            yield return t;
        }
    }

    private static readonly HashSet<string> GenericTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "recomp","recompiled","port","pc","windows","win","win64","x64","x86","release","debug","build","final",
        "version","v","the","and","for","game","launcher","setup","update","patch","remaster","hd","edition",
        "portable","bin","data","files","new","old","test","beta","alpha"
    };

    private static int ScoreAppMatch(string folderName, string gameName, FolderHints hints, QuiverAppInfo c)
    {
        int score = 0;
        var folderNorm = NormName(folderName);
        var folderTokens = Tokenize(folderName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        void ScoreText(string? text, int exactNorm, int containsNorm, int tokenWeight)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var n = NormName(text);
            if (n.Length >= 5)
            {
                if (folderNorm.Length >= 5 && string.Equals(folderNorm, n, StringComparison.OrdinalIgnoreCase)) score += exactNorm;
                else if (folderNorm.Length >= 6 && n.Length >= 6 && (folderNorm.Contains(n) || n.Contains(folderNorm))) score += containsNorm;
                else
                {
                    // solapamiento por prefijo largo (versiones sufijos)
                    int commonPrefix = 0; int m = Math.Min(folderNorm.Length, n.Length);
                    while (commonPrefix < m && folderNorm[commonPrefix] == n[commonPrefix]) commonPrefix++;
                    if (commonPrefix >= 8) score += containsNorm - 10;
                }
            }
            var toks = Tokenize(text).ToList();
            if (toks.Count == 0) return;
            int overlap = toks.Count(t => folderTokens.Contains(t) || hints.Tokens.Contains(t));
            score += overlap * tokenWeight;
            // todos los tokens significativos presentes
            if (toks.Count >= 2 && overlap == toks.Count) score += 35;
        }

        ScoreText(c.FolderName, 140, 95, 18);
        ScoreText(c.Name, 120, 80, 16);
        ScoreText(c.Project, 110, 75, 14);

        // repositorio/proyecto: pistas muy fuertes si aparecen en exe, nombres o textos
        if (!string.IsNullOrWhiteSpace(c.Repository))
        {
            var repo = c.Repository.Trim();
            var parts = repo.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var repoName = parts.Length >= 2 ? parts[1] : repo;
            var owner = parts.Length >= 2 ? parts[0] : "";
            var repoNorm = NormName(repoName);
            if (repoNorm.Length >= 4)
            {
                if (hints.ExeBlobNorm.Contains(repoNorm) || hints.Norms.Contains(repoNorm)) score += 120;
                else if (hints.Norms.Any(hn => hn.Length >= 4 && (repoNorm.Contains(hn) || hn.Contains(repoNorm)))) score += 90;
                else if (hints.ExeBlobNorm.Length >= 4 && (repoNorm.Contains(hints.ExeBlobNorm) || hints.ExeBlobNorm.Contains(repoNorm))) score += 80;
                // tokens del repo presentes en carpeta/exe
                var rtoks = Tokenize(repoName).ToList();
                if (rtoks.Count > 0)
                {
                    int ro = rtoks.Count(t => folderTokens.Contains(t) || hints.Tokens.Contains(t));
                    if (ro == rtoks.Count) score += 80;
                    else score += ro * 14;
                }
            }
            if (!string.IsNullOrWhiteSpace(owner))
            {
                var ownerNorm = NormName(owner);
                if (ownerNorm.Length >= 4 && hints.ExeBlobNorm.Contains(ownerNorm)) score += 30;
            }
            if (!string.IsNullOrWhiteSpace(hints.TextBlob))
            {
                var rl = repo.ToLowerInvariant();
                if (hints.TextBlob.Contains(rl)) score += 160;
                else if (repoNorm.Length >= 5 && hints.TextBlob.Contains(repoNorm.ToLowerInvariant())) score += 70;
            }
        }

        // coincidencia directa de nombre de carpeta contra nombre de proyecto normalizado
        if (!string.IsNullOrWhiteSpace(c.Project))
        {
            var pn = NormName(c.Project);
            if (pn.Length >= 5 && (hints.ExeBlobNorm.Contains(pn) || hints.Norms.Any(hn => hn.Length >= 5 && (pn.Contains(hn) || hn.Contains(pn))))) score += 90;
        }

        // bonus si el nombre del juego (ya resuelto) coincide fuerte
        if (!string.IsNullOrWhiteSpace(gameName) && !string.IsNullOrWhiteSpace(c.Name))
        {
            var gn = NormName(gameName); var cn = NormName(c.Name);
            if (gn.Length >= 5 && cn.Length >= 5 && (gn == cn || gn.Contains(cn) || cn.Contains(gn))) score += 40;
        }

        return score;
    }

    public static void Enrich(GameEntry g, QuiverAppInfo info, string quiverPath)
    {
        if (string.IsNullOrWhiteSpace(ConfigHasCustomName(g.FolderPath)))
        {
            // keep existing name if user didn't override - but we want to prefer Quiver name? handled by NameOverrides in scan
        }
        g.Repository = string.IsNullOrWhiteSpace(g.Repository) ? info.Repository : g.Repository;
        g.Project = string.IsNullOrWhiteSpace(g.Project) ? info.Project : g.Project;
        g.QuiverFolderName = info.FolderName;
        g.QuiverAppIconUrl = info.AppIconUrl;
        g.QuiverLocalIconPath = FindLocalIcon(quiverPath, info.FolderName, g.FolderPath);
    }

    private static string? ConfigHasCustomName(string folderPath) => null; // placeholder, not used

    public static string? FindLocalIcon(string quiverPath, string folderName, string gameFolder)
    {
        try
        {
            var iconsDir = Path.Combine(quiverPath, "Cache", "Icons");
            var customDir = Path.Combine(quiverPath, "Cache", "CustomIcons");
            foreach (var dir in new[] { customDir, iconsDir })
            {
                if (!Directory.Exists(dir)) continue;
                var fn = folderName;
                if (string.IsNullOrWhiteSpace(fn)) fn = Path.GetFileName(gameFolder);
                foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".webp", ".ico" })
                {
                    // exact or prefix match
                    foreach (var f in Directory.GetFiles(dir, fn + "*" + ext)) if (File.Exists(f)) return f;
                    foreach (var f in Directory.GetFiles(dir, "*" + fn + "*" + ext)) if (File.Exists(f)) return f;
                }
                // try any file starting with folderName
                foreach (var f in Directory.GetFiles(dir, fn + "*")) if (IsImage(f)) return f;
            }
        }
        catch { }
        return null;
    }

    private static bool IsImage(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".ico";
    }

    public static Dictionary<string, QuiverVersionInfo> LoadVersionCache(string quiverPath)
    {
        var map = new Dictionary<string, QuiverVersionInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(quiverPath, "Cache", "version_cache.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var key = prop.Name; // e.g. github:Owner/Repo
                    var v = prop.Value;
                    string version = "";
                    if (v.TryGetProperty("Version", out var ver)) version = ver.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(version) && v.TryGetProperty("CachedRelease", out var cr) && cr.TryGetProperty("tag_name", out var tn)) version = tn.GetString() ?? "";
                    var info = new QuiverVersionInfo { Version = version };
                    if (v.TryGetProperty("CachedRelease", out var rel) && rel.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                    {
                        PickWindowsAsset(info, assets);
                    }
                    PutVersion(map, key, info);
                }
            }

            // merge catalog-release-status (has verified vs latestUpstream update signals)
            var statusPath = Path.Combine(quiverPath, "Cache", "catalog-release-status.json");
            if (File.Exists(statusPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(statusPath));
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in doc.RootElement.EnumerateArray())
                    {
                        var repo = e.TryGetProperty("repository", out var r) ? (r.GetString() ?? "") : "";
                        if (string.IsNullOrWhiteSpace(repo)) continue;
                        string verified = "";
                        string latest = "";
                        if (e.TryGetProperty("verified", out var v) && v.ValueKind == JsonValueKind.Object && v.TryGetProperty("version", out var vv)) verified = vv.GetString() ?? "";
                        if (e.TryGetProperty("latestUpstream", out var lu) && lu.ValueKind == JsonValueKind.Object && lu.TryGetProperty("version", out var lv)) latest = lv.GetString() ?? "";
                        if (!map.TryGetValue(repo, out var info)) { info = new QuiverVersionInfo(); map[repo] = info; }
                        // preferir versión upstream del catálogo como "última" disponible (la caché puede estar obsoleta)
                        if (!string.IsNullOrWhiteSpace(latest)) info.Version = latest;
                        if (string.IsNullOrWhiteSpace(info.InstalledOrVerified) && !string.IsNullOrWhiteSpace(verified)) info.InstalledOrVerified = verified;
                        if (!string.IsNullOrWhiteSpace(verified) && !string.IsNullOrWhiteSpace(latest) && !SameVersion(verified, latest))
                            info.UpdateAvailable = true;
                        // también si la última conocida difiere de la verificada/instalada
                        if (!string.IsNullOrWhiteSpace(verified) && !string.IsNullOrWhiteSpace(info.Version) && !SameVersion(verified, info.Version))
                            info.UpdateAvailable = true;
                    }
                }
            }
        }
        catch { }
        return map;
    }

    private static void PickWindowsAsset(QuiverVersionInfo info, JsonElement assets)
    {
        JsonElement? best = null; int bestScore = -999;
        foreach (var a in assets.EnumerateArray())
        {
            var name = a.TryGetProperty("name", out var an) ? (an.GetString() ?? "") : "";
            var lower = name.ToLowerInvariant();
            int score = 0;
            bool isWin = lower.Contains("windows") || lower.Contains("win64") || lower.Contains("win-x64") || lower.Contains("win32") || lower.Contains("-win-") || lower.Contains("_win") || lower.Contains(".exe");
            bool bad = lower.Contains("linux") || lower.Contains("android") || lower.Contains("macos") || lower.Contains("osx") || lower.Contains("darwin") || lower.Contains("switch") || lower.Contains("psp") || lower.Contains("vita");
            if (isWin) score += 50;
            if (bad) score -= 100;
            if (lower.EndsWith(".zip")) score += 20;
            if (lower.EndsWith(".exe")) score += 10;
            if (lower.EndsWith(".tar.gz") || lower.EndsWith(".7z") || lower.EndsWith(".rar")) score -= 5;
            if (score > bestScore) { bestScore = score; best = a; }
        }
        best ??= assets.GetArrayLength() > 0 ? assets[0] : (JsonElement?)null;
        if (best.HasValue && bestScore > -50)
        {
            var b = best.Value;
            info.AssetName = b.TryGetProperty("name", out var bn) ? (bn.GetString() ?? "") : "";
            info.AssetUrl = b.TryGetProperty("browser_download_url", out var bu) ? (bu.GetString() ?? "") : "";
            info.Digest = b.TryGetProperty("digest", out var dg) ? (dg.GetString() ?? "") : "";
        }
    }

    private static void PutVersion(Dictionary<string, QuiverVersionInfo> map, string key, QuiverVersionInfo info)
    {
        if (!map.TryGetValue(key, out var ex)) map[key] = info;
        else
        {
            if (string.IsNullOrWhiteSpace(ex.Version)) ex.Version = info.Version;
            if (string.IsNullOrWhiteSpace(ex.AssetUrl)) { ex.AssetUrl = info.AssetUrl; ex.AssetName = info.AssetName; ex.Digest = info.Digest; }
            if (string.IsNullOrWhiteSpace(ex.InstalledOrVerified)) ex.InstalledOrVerified = info.InstalledOrVerified;
            ex.UpdateAvailable |= info.UpdateAvailable;
        }
        var idx = key.IndexOf(':');
        if (idx >= 0 && idx + 1 < key.Length)
        {
            var repo = key.Substring(idx + 1);
            if (!map.TryGetValue(repo, out var ex2)) map[repo] = info;
            else
            {
                if (string.IsNullOrWhiteSpace(ex2.Version)) ex2.Version = info.Version;
                if (string.IsNullOrWhiteSpace(ex2.AssetUrl)) { ex2.AssetUrl = info.AssetUrl; ex2.AssetName = info.AssetName; ex2.Digest = info.Digest; }
                if (string.IsNullOrWhiteSpace(ex2.InstalledOrVerified)) ex2.InstalledOrVerified = info.InstalledOrVerified;
                ex2.UpdateAvailable |= info.UpdateAvailable;
            }
        }
    }

    public static bool SameVersion(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        string N(string s) { s = s.Trim(); if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s[1..]; return s; }
        return string.Equals(N(a), N(b), StringComparison.OrdinalIgnoreCase);
    }

    public static string? ReadInstalledVersion(string gameFolder)
    {
        try
        {
            foreach (var name in new[] { "version.txt", "installed-build.txt", "VERSION", "Version.txt" })
            {
                var p = Path.Combine(gameFolder, name);
                if (File.Exists(p))
                {
                    var t = File.ReadAllText(p).Trim();
                    if (!string.IsNullOrWhiteSpace(t)) return t.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                }
            }
            // try subdirs one level (some installs have version file inside)
            foreach (var sub in Directory.GetDirectories(gameFolder))
            {
                foreach (var name in new[] { "version.txt", "installed-build.txt" })
                {
                    var p = Path.Combine(sub, name);
                    if (File.Exists(p))
                    {
                        var t = File.ReadAllText(p).Trim();
                        if (!string.IsNullOrWhiteSpace(t)) return t.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                    }
                }
            }
        }
        catch { }
        return null;
    }

    public static void ApplyUpdateInfo(GameEntry g, Dictionary<string, QuiverVersionInfo> versions)
    {
        if (string.IsNullOrWhiteSpace(g.Repository)) return;
        QuiverVersionInfo? vi = null;
        if (!versions.TryGetValue("github:" + g.Repository, out vi))
            versions.TryGetValue(g.Repository, out vi);
        if (vi == null) return;

        // La versión realmente instalada en la carpeta es la fuente de verdad (evita reofrecer updates ya aplicados).
        var inst = ReadInstalledVersion(g.FolderPath);
        if (!string.IsNullOrWhiteSpace(inst))
            g.InstalledVersion = inst;
        else if (string.IsNullOrWhiteSpace(g.InstalledVersion) && !string.IsNullOrWhiteSpace(vi.InstalledOrVerified))
            g.InstalledVersion = vi.InstalledOrVerified;

        if (!string.IsNullOrWhiteSpace(vi.Version)) g.LatestVersion = vi.Version;
        if (!string.IsNullOrWhiteSpace(vi.AssetUrl)) { g.UpdateAssetUrl = vi.AssetUrl; g.UpdateAssetName = vi.AssetName; g.UpdateAssetDigest = vi.Digest; }

        // Si sigue sin conocerse la instalada, no inventar "instalada": sin base real no marcamos actualización falsa.
        // (HasUpdate requiere instalada y última distintas; dejar instalada vacía evita el bucle de "sigue habiendo update").
        if (string.IsNullOrWhiteSpace(g.InstalledVersion) && !string.IsNullOrWhiteSpace(g.LatestVersion))
        {
            // último recurso: si la caché trae una verificada/instalada explícita distinta, úsala como base
            if (!string.IsNullOrWhiteSpace(vi.InstalledOrVerified) && !SameVersion(vi.InstalledOrVerified, g.LatestVersion))
                g.InstalledVersion = vi.InstalledOrVerified;
        }
    }

    public static Dictionary<string, QuiverCatalogEntry> LoadCatalogIndex(string quiverPath)
    {
        var byKey = new Dictionary<string, QuiverCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var linksPath = Path.Combine(quiverPath, "Cache", "catalog-release-links.json");
            var statusPath = Path.Combine(quiverPath, "Cache", "catalog-release-status.json");
            var statusBySlug = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(statusPath))
            {
                using var sdoc = JsonDocument.Parse(File.ReadAllText(statusPath));
                if (sdoc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in sdoc.RootElement.EnumerateArray())
                    {
                        var slug = e.TryGetProperty("slug", out var sl) ? (sl.GetString() ?? "") : "";
                        if (!string.IsNullOrWhiteSpace(slug)) statusBySlug[slug] = e.Clone();
                    }
                }
            }
            if (File.Exists(linksPath))
            {
                using var ldoc = JsonDocument.Parse(File.ReadAllText(linksPath));
                foreach (var prop in ldoc.RootElement.EnumerateObject())
                {
                    var slug = prop.Name;
                    var v = prop.Value;
                    var folderName = v.TryGetProperty("folderName", out var fn) ? (fn.GetString() ?? "") : "";
                    if (string.IsNullOrWhiteSpace(folderName)) continue;
                    var entry = new QuiverCatalogEntry { Slug = slug, FolderName = folderName };
                    if (statusBySlug.TryGetValue(slug, out var st))
                    {
                        entry.Repository = st.TryGetProperty("repository", out var r) ? (r.GetString() ?? "") : "";
                        entry.Provider = st.TryGetProperty("provider", out var pr) ? (pr.GetString() ?? "") : "";
                        if (st.TryGetProperty("verified", out var ver) && ver.ValueKind == JsonValueKind.Object && ver.TryGetProperty("version", out var vv)) entry.Verified = vv.GetString() ?? "";
                        if (st.TryGetProperty("latestUpstream", out var lu) && lu.ValueKind == JsonValueKind.Object && lu.TryGetProperty("version", out var lv)) entry.Latest = lv.GetString() ?? "";
                        entry.UpdateAvailable = !string.IsNullOrWhiteSpace(entry.Verified) && !string.IsNullOrWhiteSpace(entry.Latest) && !SameVersion(entry.Verified, entry.Latest);
                    }
                    byKey["fn:" + folderName] = entry;
                    var norm = NormName(folderName);
                    if (norm.Length >= 5 && !byKey.ContainsKey("n:" + norm)) byKey["n:" + norm] = entry;
                }
            }
            // also include status entries without links? they lack folderName, skip for folder matching
        }
        catch { }
        return byKey;
    }

    public static QuiverCatalogEntry? MatchCatalog(GameEntry g, Dictionary<string, QuiverCatalogEntry> catalog)
    {
        var folderName = Path.GetFileName(g.FolderPath);
        if (catalog.TryGetValue("fn:" + folderName, out var e)) return e;
        // case-insensitive exact on stored names
        foreach (var kv in catalog)
        {
            if (kv.Key.StartsWith("fn:", StringComparison.OrdinalIgnoreCase))
            {
                var fn = kv.Key.Substring(3);
                if (string.Equals(fn, folderName, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            }
        }
        var n = NormName(folderName);
        if (n.Length >= 6 && catalog.TryGetValue("n:" + n, out e)) return e;

        // fuzzy: por nombre/tokens y contenido de carpeta contra folderName/slug/repo del catálogo
        try
        {
            var hints = CollectFolderHints(g.FolderPath);
            var candidates = catalog.Where(kv => kv.Key.StartsWith("fn:", StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Value)
                .GroupBy(v => (v.Slug ?? "") + "|" + (v.Repository ?? ""))
                .Select(gr => gr.First())
                .ToList();
            QuiverCatalogEntry? best = null; int bestScore = 0;
            foreach (var c in candidates)
            {
                int score = ScoreCatalogMatch(folderName, g.Name, hints, c);
                if (score > bestScore) { bestScore = score; best = c; }
            }
            if (best != null && bestScore >= 110) return best;
        }
        catch { }

        return null;
    }

    private static int ScoreCatalogMatch(string folderName, string gameName, FolderHints hints, QuiverCatalogEntry c)
    {
        int score = 0;
        var folderNorm = NormName(folderName);
        var folderTokens = Tokenize(folderName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        void ScoreText(string? text, int exactNorm, int containsNorm, int tokenWeight)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var n = NormName(text);
            if (n.Length >= 5)
            {
                if (folderNorm.Length >= 5 && string.Equals(folderNorm, n, StringComparison.OrdinalIgnoreCase)) score += exactNorm;
                else if (folderNorm.Length >= 6 && n.Length >= 6 && (folderNorm.Contains(n) || n.Contains(folderNorm))) score += containsNorm;
                else
                {
                    int commonPrefix = 0; int m = Math.Min(folderNorm.Length, n.Length);
                    while (commonPrefix < m && folderNorm[commonPrefix] == n[commonPrefix]) commonPrefix++;
                    if (commonPrefix >= 8) score += containsNorm - 10;
                }
            }
            var toks = Tokenize(text).ToList();
            if (toks.Count == 0) return;
            int overlap = toks.Count(t => folderTokens.Contains(t) || hints.Tokens.Contains(t));
            score += overlap * tokenWeight;
            if (toks.Count >= 2 && overlap == toks.Count) score += 30;
        }

        ScoreText(c.FolderName, 140, 95, 18);
        ScoreText((c.Slug ?? "").Replace("-", " ").Replace("_", " "), 90, 60, 12);

        if (!string.IsNullOrWhiteSpace(c.Repository))
        {
            var repo = c.Repository.Trim();
            var parts = repo.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var repoName = parts.Length >= 2 ? parts[1] : repo;
            var owner = parts.Length >= 2 ? parts[0] : "";
            var repoNorm = NormName(repoName);
            if (repoNorm.Length >= 4)
            {
                if (hints.ExeBlobNorm.Contains(repoNorm) || hints.Norms.Contains(repoNorm)) score += 120;
                else if (hints.Norms.Any(hn => hn.Length >= 4 && (repoNorm.Contains(hn) || hn.Contains(repoNorm)))) score += 90;
                else if (hints.ExeBlobNorm.Length >= 4 && (repoNorm.Contains(hints.ExeBlobNorm) || hints.ExeBlobNorm.Contains(repoNorm))) score += 80;
                var rtoks = Tokenize(repoName).ToList();
                if (rtoks.Count > 0)
                {
                    int ro = rtoks.Count(t => folderTokens.Contains(t) || hints.Tokens.Contains(t));
                    if (ro == rtoks.Count) score += 80;
                    else score += ro * 14;
                }
            }
            if (!string.IsNullOrWhiteSpace(owner))
            {
                var ownerNorm = NormName(owner);
                if (ownerNorm.Length >= 4 && hints.ExeBlobNorm.Contains(ownerNorm)) score += 25;
            }
            if (!string.IsNullOrWhiteSpace(hints.TextBlob))
            {
                var rl = repo.ToLowerInvariant();
                if (hints.TextBlob.Contains(rl)) score += 160;
                else if (repoNorm.Length >= 5 && hints.TextBlob.Contains(repoNorm.ToLowerInvariant())) score += 70;
            }
        }

        return score;
    }

    public static void EnrichFromCatalog(GameEntry g, QuiverCatalogEntry c)
    {
        if (string.IsNullOrWhiteSpace(g.Repository)) g.Repository = c.Repository;
        if (string.IsNullOrWhiteSpace(g.QuiverFolderName)) g.QuiverFolderName = c.FolderName;
        // apply version/update hints from catalog if missing
        if (string.IsNullOrWhiteSpace(g.LatestVersion) && !string.IsNullOrWhiteSpace(c.Latest)) g.LatestVersion = c.Latest;

        // Preferir siempre la versión instalada real de la carpeta sobre la "verificada" del catálogo.
        var inst = ReadInstalledVersion(g.FolderPath);
        if (!string.IsNullOrWhiteSpace(inst))
            g.InstalledVersion = inst;
        else if (string.IsNullOrWhiteSpace(g.InstalledVersion) && !string.IsNullOrWhiteSpace(c.Verified))
            g.InstalledVersion = c.Verified;

        // No forzar marcador ficticio; si no hay base real de instalada, no se declara actualización falsa.
    }

    public static string Norm(string p) => (p ?? "").Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
}
