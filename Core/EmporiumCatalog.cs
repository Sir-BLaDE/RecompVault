using System.IO;
using System.Net;
using System.Text.RegularExpressions;

namespace RecompVault.Core;

/// <summary>Un proyecto del catálogo de The Gaming Emporium (thegamingemporium.com) que enlaza a un repositorio.</summary>
public sealed record EmporiumEntry(
    string Slug, string Title, string Platform, string Category, string Genres, string ReleasePlatforms,
    string Url, string Provider, string Repository)
{
    public bool HasWindows => ReleasePlatforms.Length == 0 || ReleasePlatforms.Contains("windows", StringComparison.OrdinalIgnoreCase);
    public bool IsUtility => Genres.Split('|').Any(g => g.Trim().Equals("Utility", StringComparison.OrdinalIgnoreCase));
    public string RepoName => Repository.Split('/').LastOrDefault() ?? "";
}

public sealed record EmporiumMatch(EmporiumEntry Entry, string Kind, bool Approximate);

/// <summary>
/// Lee las páginas de categoría de The Gaming Emporium y empareja carpetas con sus proyectos.
/// La web no tiene API: cada tarjeta lleva el título, la plataforma original y el enlace en atributos data-*.
/// </summary>
public static class EmporiumCatalog
{
    public const string BaseUrl = "https://thegamingemporium.com";

    /// <summary>Categorías con proyectos ejecutables en PC (las demás son mods, ROM hacks, guías, etc.).</summary>
    public static readonly string[] Categories = { "decompilations-recompilations", "console-to-pc-port", "open-source" };

    private static readonly Regex CardStart = new(@"<div class=""game-card\b", RegexOptions.Compiled);
    private static readonly Regex Attr = new(@"\bdata-([a-z-]+)=""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex Link = new(@"class=""game-card__link""\s+href=""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex RepoUrl = new(@"^https?://(?:www\.)?(github\.com|gitlab\.com)/([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> NotOwners = new(StringComparer.OrdinalIgnoreCase)
    { "orgs", "topics", "search", "collections", "sponsors", "marketplace", "features", "explore", "users", "groups" };

    /// <summary>"https://github.com/Owner/Repo/releases/tag/x" → ("github", "Owner/Repo").</summary>
    public static (string provider, string repo)? ParseRepo(string url)
    {
        var m = RepoUrl.Match(url ?? "");
        if (!m.Success) return null;
        var owner = m.Groups[2].Value;
        var repo = m.Groups[3].Value;
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo[..^4];
        if (NotOwners.Contains(owner) || repo.Length == 0) return null;
        return (m.Groups[1].Value.StartsWith("gitlab", StringComparison.OrdinalIgnoreCase) ? "gitlab" : "github", owner + "/" + repo);
    }

    /// <summary>Tarjetas de una página de categoría que enlazan a un repositorio de GitHub o GitLab.</summary>
    public static List<EmporiumEntry> ParseCategoryPage(string html)
    {
        var list = new List<EmporiumEntry>();
        if (string.IsNullOrEmpty(html)) return list;
        var starts = CardStart.Matches(html).Select(m => m.Index).ToList();
        for (int i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1] : Math.Min(html.Length, starts[i] + 20000);
            var chunk = html.AsSpan(starts[i], end - starts[i]).ToString();
            var tagEnd = chunk.IndexOf('>');
            if (tagEnd < 0) continue;
            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match a in Attr.Matches(chunk[..tagEnd])) attrs[a.Groups[1].Value] = WebUtility.HtmlDecode(a.Groups[2].Value).Trim();
            var lm = Link.Match(chunk);
            if (!lm.Success) continue;
            var url = WebUtility.HtmlDecode(lm.Groups[1].Value).Trim();
            var repo = ParseRepo(url);
            if (repo == null) continue;
            string A(string k) => attrs.TryGetValue(k, out var v) ? v : "";
            var title = A("title");
            if (title.Length == 0) continue;
            list.Add(new EmporiumEntry(A("favourite-id"), title, A("platform"), A("category"), A("genres"),
                A("release-platforms"), url, repo.Value.provider, repo.Value.repo));
        }
        return list;
    }

    // ------------------------------------------------------------------ emparejado

    private static readonly Regex TitleNoise = new(
        @"\b(recompilation|recompiled|recomp|decompilation|decompiled|decomp|native|pc|ports?|remake|enhanced|unofficial|fan|project|emulation|wrapper|launcher|windows)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Formas del título con las que comparar: completo, sin "Recompilation/PC Port", y cada lado de " - ".</summary>
    internal static IEnumerable<string> TitleVariants(EmporiumEntry e)
    {
        var t = e.Title.Replace('–', '-').Replace('—', '-').Replace("​", "");
        yield return t;
        yield return TitleNoise.Replace(t, " ");
        foreach (var part in t.Split(" - ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return part;
            yield return TitleNoise.Replace(part, " ");
        }
        yield return e.RepoName;
    }

    private static bool PlatformCompatible(EmporiumEntry e, string? gameSystem)
    {
        if (string.IsNullOrWhiteSpace(e.Platform) || string.IsNullOrWhiteSpace(gameSystem)) return true;
        var ep = SystemMapper.Suggest(new[] { e.Platform });
        var gp = SystemMapper.Suggest(new[] { gameSystem }) ?? gameSystem;
        // "Windows"/"Multiple Platforms" o un sistema desconocido no descartan nada
        if (ep == null || ep == "PC" || gp == "PC") return true;
        return string.Equals(ep, gp, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Proyecto del catálogo que corresponde a la carpeta, o null si no hay pruebas suficientes o hay varios candidatos.
    /// Pruebas, de más a menos fuertes: el enlace al repositorio aparece en los archivos de la carpeta; el nombre del
    /// repositorio aparece en la carpeta o en sus ejecutables; el título coincide (aproximada, no se auto-actualiza).
    /// </summary>
    private sealed record Prepared(EmporiumEntry E, string RepoLower, string RepoNorm, string RepoCore, HashSet<string> TitleCores);

    private static (IReadOnlyList<EmporiumEntry> src, Prepared[] items)? _prep;
    private static readonly object PrepLock = new();

    /// <summary>Normalizaciones de cada proyecto calculadas una sola vez por catálogo (el emparejado las usa miles de veces).</summary>
    private static Prepared[] Prepare(IReadOnlyList<EmporiumEntry> entries)
    {
        lock (PrepLock)
        {
            if (_prep is { } p && ReferenceEquals(p.src, entries)) return p.items;
            var items = entries.Where(e => !e.IsUtility && e.HasWindows).Select(e => new Prepared(e,
                e.Repository.ToLowerInvariant(), PathUtil.NormName(e.RepoName), QuiverMatcher.CoreTitle(e.RepoName),
                TitleVariants(e).Select(QuiverMatcher.CoreTitle).Where(v => v.Length >= 4).ToHashSet())).ToArray();
            _prep = (entries, items);
            return items;
        }
    }

    public static EmporiumMatch? Match(IReadOnlyList<EmporiumEntry> entries, string folderPath, string? gameSystem)
    {
        if (entries.Count == 0 || !Directory.Exists(folderPath)) return null;
        var hints = QuiverMatcher.Hints(folderPath);
        var folderName = Path.GetFileName(folderPath.TrimEnd('\\', '/'));
        var folderCore = QuiverMatcher.CoreTitle(folderName);

        var byLevel = new List<(int level, EmporiumEntry e, string kind)>();
        foreach (var pr in Prepare(entries))
        {
            var e = pr.E;
            if (!PlatformCompatible(e, gameSystem)) continue;

            if (hints.TextBlob.Length > 0 && (hints.TextBlob.Contains("github.com/" + pr.RepoLower) || hints.TextBlob.Contains("gitlab.com/" + pr.RepoLower)))
            { byLevel.Add((1, e, "enlace al repositorio en los archivos")); continue; }

            // mismo nombre exacto que la carpeta o que un ejecutable ("CPS2Recomp", "reblue.exe", "Takedawn")
            if (pr.RepoNorm.Length >= 5 && pr.RepoCore.Length >= 4 && hints.Norms.Contains(pr.RepoNorm))
            {
                var byExe = hints.ExeBlobNorm.Contains(pr.RepoNorm);
                byLevel.Add((2, e, byExe ? "nombre del repositorio (ejecutable)" : "nombre del repositorio (carpeta)"));
                continue;
            }

            // mismo título sin versión ni relleno: solo es una aproximación (hay muchos ports del mismo juego)
            if (folderCore.Length >= 4 && (pr.RepoCore == folderCore || pr.TitleCores.Contains(folderCore)))
            {
                // el repositorio es una variante concreta del juego ("...Plus", "...Remastered"): no basta con el título
                bool variant = pr.RepoCore.Length >= 5 && pr.RepoCore != folderCore && !folderCore.Contains(pr.RepoCore) && pr.RepoCore.Contains(folderCore);
                if (!variant) byLevel.Add((3, e, "título"));
            }
        }
        if (byLevel.Count == 0) return null;
        var best = byLevel.Min(x => x.level);
        var top = byLevel.Where(x => x.level == best).ToList();
        var repos = top.Select(x => x.e.Repository.ToLowerInvariant()).Distinct().ToList();
        if (repos.Count != 1) return null; // ambiguo: varios proyectos distintos
        // carpeta llamada como el repositorio ("Perfect Dark") pero hay otros ports del mismo juego: no es concluyente
        if (best == 2 && top.All(x => x.kind.EndsWith("(carpeta)")) &&
            byLevel.Any(x => x.level == 3 && !x.e.Repository.Equals(top[0].e.Repository, StringComparison.OrdinalIgnoreCase)))
            return null;
        var pick = top.First();
        return new EmporiumMatch(pick.e, pick.kind, Approximate: best == 3);
    }

    /// <summary>Hasta <paramref name="max"/> candidatos por título para sugerir en la ventana de detección (sin aplicar).</summary>
    public static List<EmporiumEntry> Candidates(IReadOnlyList<EmporiumEntry> entries, string folderPath, string? gameSystem, string? name, int max = 3)
    {
        var keys = new[] { QuiverMatcher.CoreTitle(Path.GetFileName(folderPath.TrimEnd('\\', '/'))), QuiverMatcher.CoreTitle(name) }
            .Where(k => k.Length >= 4).Distinct().ToList();
        if (keys.Count == 0) return new();
        return Prepare(entries).Where(p => PlatformCompatible(p.E, gameSystem))
            .Select(p => (e: p.E, score: p.TitleCores.Append(p.RepoCore).Where(v => v.Length >= 4)
                .Select(v => keys.Max(k => v == k ? 3 : (v.StartsWith(k) || k.StartsWith(v)) ? 2 : (v.Contains(k) || k.Contains(v)) ? 1 : 0))
                .DefaultIfEmpty(0).Max()))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score).ThenBy(x => x.e.Title.Length)
            .Select(x => x.e)
            .GroupBy(e => e.Repository, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .Take(max).ToList();
    }
}
