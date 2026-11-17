using System.Collections.Concurrent;
using System.Text;

namespace RecompVault.Core;

public sealed record QuiverMatch(QuiverApp? App, QuiverCatalogEntry? Catalog)
{
    public string Repository => App?.Repository is { Length: > 0 } r ? r : Catalog?.Repository ?? "";
    public string Provider => App?.Repository is { Length: > 0 } ? App.Provider : Catalog?.Provider ?? "github";
    public string AssetFilter => App?.AssetFilter is { Length: > 0 } f ? f : Catalog?.AssetFilter ?? "";
}

/// <summary>
/// Relaciona una carpeta de juego con una app de Quiver o una entrada de su catálogo:
/// primero por ruta y nombre de carpeta exactos, después por puntuación (nombre, ejecutables, README...).
/// </summary>
public static class QuiverMatcher
{
    private const int AppThreshold = 120;
    private const int CatalogThreshold = 110;

    /// <summary>Margen mínimo sobre el segundo candidato para aceptar una coincidencia difusa.</summary>
    private const int AmbiguityMargin = 25;

    /// <summary>Solo coincidencias seguras: ruta, nombre de carpeta exacto o normalizado.</summary>
    public static QuiverMatch? MatchExact(QuiverLibrary lib, string folderPath)
    {
        var folderName = Path.GetFileName(folderPath.TrimEnd('\\', '/'));
        var app = lib.AppByPath(folderPath) ?? lib.AppByFolderName(folderName) ?? lib.AppByNormName(PathUtil.NormName(folderName));
        if (app != null) return new QuiverMatch(app, lib.CatalogFor(app));
        var cat = lib.CatalogByFolderName(folderName) ?? lib.CatalogByNormName(PathUtil.NormName(folderName));
        return cat != null ? new QuiverMatch(null, cat) : null;
    }

    /// <param name="taken">Apps ya asignadas a otra carpeta: no se reutilizan en coincidencias difusas.</param>
    public static QuiverMatch? Match(QuiverLibrary lib, string folderPath, string gameName, ISet<QuiverApp>? taken = null)
    {
        var folderName = Path.GetFileName(folderPath.TrimEnd('\\', '/'));

        // 1) exactos
        var app = lib.AppByPath(folderPath)
                  ?? lib.AppByFolderName(folderName)
                  ?? lib.AppByNormName(PathUtil.NormName(folderName));
        if (app == null)
        {
            // carpeta dentro de un installPath de Quiver o al revés (con límite de separador)
            foreach (var a in lib.Apps)
            {
                if (a.InstallPath.Length == 0) continue;
                if (PathUtil.IsSameOrInside(folderPath, a.InstallPath) || PathUtil.IsSameOrInside(a.InstallPath, folderPath)) { app = a; break; }
            }
        }
        if (app != null) return new QuiverMatch(app, lib.CatalogFor(app));

        var cat = lib.CatalogByFolderName(folderName) ?? lib.CatalogByNormName(PathUtil.NormName(folderName));
        if (cat != null) return new QuiverMatch(null, cat);

        // 2) por puntuación
        var hints = Hints(folderPath);
        QuiverApp? bestApp = null; int bestAppScore = 0, secondApp = 0;
        foreach (var c in lib.Apps)
        {
            if (taken != null && taken.Contains(c)) continue;
            // la carpeta instalada de esta app existe y es otra: no puede ser esta
            if (c.InstallPath.Length > 0 && Directory.Exists(c.InstallPath) && !PathUtil.IsSameOrInside(folderPath, c.InstallPath)) continue;
            int s = Score(folderName, gameName, hints, c.FolderName, c.Name, c.Project, c.Repository, isApp: true);
            if (s > bestAppScore) { secondApp = bestAppScore; bestAppScore = s; bestApp = c; }
            else if (s > secondApp && (bestApp == null || c.Repository != bestApp.Repository)) secondApp = s;
        }
        if (bestApp != null && bestAppScore >= AppThreshold && bestAppScore - secondApp >= AmbiguityMargin)
            return new QuiverMatch(bestApp, lib.CatalogFor(bestApp));

        QuiverCatalogEntry? bestCat = null; int bestCatScore = 0, secondCat = 0;
        foreach (var c in lib.Catalog)
        {
            int s = Score(folderName, gameName, hints, c.FolderName, c.Slug.Replace('-', ' ').Replace('_', ' '), "", c.Repository, isApp: false);
            if (s > bestCatScore) { secondCat = bestCatScore; bestCatScore = s; bestCat = c; }
            else if (s > secondCat && (bestCat == null || c.Repository != bestCat.Repository)) secondCat = s;
        }
        if (bestCat != null && bestCatScore >= CatalogThreshold && bestCatScore - secondCat >= AmbiguityMargin)
            return new QuiverMatch(null, bestCat);
        return null;
    }

    // ------------------------------------------------------------------ pistas de carpeta (cacheadas)

    internal sealed class FolderHints
    {
        public readonly HashSet<string> Tokens = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Norms = new(StringComparer.OrdinalIgnoreCase);
        public string ExeBlobNorm = "";
        public string TextBlob = "";
    }

    private static readonly ConcurrentDictionary<string, (long stamp, FolderHints hints)> HintCache = new(StringComparer.OrdinalIgnoreCase);

    public static void ClearCache() => HintCache.Clear();

    internal static FolderHints Hints(string folderPath)
    {
        long stamp = 0;
        try { stamp = Directory.GetLastWriteTimeUtc(folderPath).Ticks; } catch { }
        if (HintCache.TryGetValue(folderPath, out var c) && c.stamp == stamp) return c.hints;
        var h = CollectHints(folderPath);
        HintCache[folderPath] = (stamp, h);
        return h;
    }

    private static FolderHints CollectHints(string folderPath)
    {
        var h = new FolderHints();
        try
        {
            var folderName = Path.GetFileName(folderPath.TrimEnd('\\', '/'));
            foreach (var t in Tokenize(folderName)) h.Tokens.Add(t);
            var nf = PathUtil.NormName(folderName);
            if (nf.Length >= 4) h.Norms.Add(nf);

            var names = new List<string>();
            foreach (var pat in new[] { "*.exe", "*.bat", "*.cmd", "*.ps1" })
                names.AddRange(QuiverLibrary.SafeFiles(folderPath, pat).Select(f => Path.GetFileName(f)));
            foreach (var sub in QuiverLibrary.SafeDirs(folderPath).Take(6))
                names.AddRange(QuiverLibrary.SafeFiles(sub, "*.exe").Take(10).Select(f => Path.GetFileName(f)));

            h.ExeBlobNorm = PathUtil.NormName(string.Join(" ", names));
            foreach (var n in names)
            {
                var stem = Path.GetFileNameWithoutExtension(n);
                foreach (var t in Tokenize(stem)) h.Tokens.Add(t);
                var nn = PathUtil.NormName(stem);
                if (nn.Length >= 4) h.Norms.Add(nn);
            }

            var texts = new List<string>();
            foreach (var pat in new[] { "README*", "*.txt", "*.json", "*.cfg", "*.ini", "*.toml" })
                texts.AddRange(QuiverLibrary.SafeFiles(folderPath, pat));
            var sb = new StringBuilder();
            int read = 0;
            foreach (var f in texts.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (read >= 6 || sb.Length >= 12000) break;
                try
                {
                    var fi = new FileInfo(f);
                    if (fi.Length == 0 || fi.Length > 200_000) continue;
                    using var r = new StreamReader(f);
                    var buf = new char[4000];
                    var n = r.Read(buf, 0, buf.Length);
                    if (n <= 0) continue;
                    sb.Append(' ').Append(buf, 0, n);
                    read++;
                }
                catch { }
            }
            h.TextBlob = sb.ToString().ToLowerInvariant();
        }
        catch { }
        return h;
    }

    private static readonly HashSet<string> GenericTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "recomp","recompiled","decomp","port","pc","windows","win","win64","x64","x86","release","debug","build","final",
        "version","the","and","for","game","launcher","setup","update","patch","remaster","hd","edition",
        "portable","bin","data","files","new","old","test","beta","alpha","msvc","mingw","amd64"
    };

    internal static IEnumerable<string> Tokenize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) yield break;
        // separa también CamelCase: "BanjoKazooie" → "banjo kazooie"
        var sb = new StringBuilder(s.Length * 2);
        for (int i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (!char.IsLetterOrDigit(ch)) { sb.Append(' '); continue; }
            if (i > 0 && char.IsUpper(ch) && char.IsLower(s[i - 1])) sb.Append(' ');
            sb.Append(char.ToLowerInvariant(ch));
        }
        foreach (var raw in sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Length < 3) continue;
            if (raw.All(char.IsDigit)) continue;
            if (raw[0] == 'v' && raw.Skip(1).All(c => char.IsDigit(c))) continue;
            if (GenericTokens.Contains(raw)) continue;
            yield return raw;
        }
    }

    private static int Score(string folderName, string gameName, FolderHints hints,
        string candFolder, string candName, string candProject, string repository, bool isApp)
    {
        int score = 0;
        var folderNorm = PathUtil.NormName(folderName);
        var folderTokens = Tokenize(folderName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        void ScoreText(string? text, int exactNorm, int containsNorm, int tokenWeight, int allBonus)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var n = PathUtil.NormName(text);
            if (n.Length >= 5 && folderNorm.Length >= 5)
            {
                if (folderNorm == n) score += exactNorm;
                else if (folderNorm.Length >= 6 && n.Length >= 6 && (folderNorm.Contains(n) || n.Contains(folderNorm))) score += containsNorm;
                else
                {
                    int cp = 0, m = Math.Min(folderNorm.Length, n.Length);
                    while (cp < m && folderNorm[cp] == n[cp]) cp++;
                    if (cp >= 8) score += containsNorm - 10;
                }
            }
            var toks = Tokenize(text).ToList();
            if (toks.Count == 0) return;
            int overlap = toks.Count(t => folderTokens.Contains(t) || hints.Tokens.Contains(t));
            score += overlap * tokenWeight;
            if (toks.Count >= 2 && overlap == toks.Count) score += allBonus;
        }

        ScoreText(candFolder, 140, 95, 18, isApp ? 35 : 30);
        ScoreText(candName, isApp ? 120 : 90, isApp ? 80 : 60, isApp ? 16 : 12, isApp ? 35 : 30);
        if (isApp) ScoreText(candProject, 110, 75, 14, 35);

        if (!string.IsNullOrWhiteSpace(repository))
        {
            var repo = repository.Trim();
            var parts = repo.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var repoName = parts.Length >= 2 ? parts[^1] : repo;
            var owner = parts.Length >= 2 ? parts[0] : "";
            var repoNorm = PathUtil.NormName(repoName);
            if (repoNorm.Length >= 4)
            {
                if (hints.ExeBlobNorm.Contains(repoNorm) || hints.Norms.Contains(repoNorm)) score += 120;
                else if (hints.Norms.Any(hn => hn.Length >= 4 && (repoNorm.Contains(hn) || hn.Contains(repoNorm)))) score += 90;
                var rtoks = Tokenize(repoName).ToList();
                if (rtoks.Count > 0)
                {
                    int ro = rtoks.Count(t => folderTokens.Contains(t) || hints.Tokens.Contains(t));
                    score += ro == rtoks.Count ? 80 : ro * 14;
                }
            }
            var ownerNorm = PathUtil.NormName(owner);
            if (ownerNorm.Length >= 4 && hints.ExeBlobNorm.Contains(ownerNorm)) score += isApp ? 30 : 25;
            if (hints.TextBlob.Length > 0)
            {
                if (hints.TextBlob.Contains(repo.ToLowerInvariant())) score += 160;
                else if (repoNorm.Length >= 5 && hints.TextBlob.Contains(repoNorm)) score += 70;
            }
        }

        if (isApp && !string.IsNullOrWhiteSpace(candProject))
        {
            var pn = PathUtil.NormName(candProject);
            if (pn.Length >= 5 && (hints.ExeBlobNorm.Contains(pn) || hints.Norms.Any(hn => hn.Length >= 5 && (pn.Contains(hn) || hn.Contains(pn))))) score += 90;
        }

        if (isApp && !string.IsNullOrWhiteSpace(gameName) && !string.IsNullOrWhiteSpace(candName))
        {
            var gn = PathUtil.NormName(gameName); var cn = PathUtil.NormName(candName);
            if (gn.Length >= 5 && cn.Length >= 5 && (gn == cn || gn.Contains(cn) || cn.Contains(gn))) score += 40;
        }
        return score;
    }
}
