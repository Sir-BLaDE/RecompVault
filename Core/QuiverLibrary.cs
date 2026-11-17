using System.Text.Json;

namespace RecompVault.Core;

public sealed class QuiverApp
{
    public string Name { get; init; } = "";
    public string FolderName { get; init; } = "";
    /// <summary>Carpeta donde está instalada (resuelta a ruta absoluta), o "" si no se encontró.</summary>
    public string InstallPath { get; set; } = "";
    public string Repository { get; init; } = "";
    /// <summary>"github" o "gitlab".</summary>
    public string Provider { get; init; } = "github";
    public string Project { get; init; } = "";
    public string AppIconUrl { get; init; } = "";
    public string CustomIconPath { get; set; } = "";
    public List<string> Tags { get; init; } = new();
    public bool Hidden { get; init; }
    /// <summary>releaseAssetFilter: distingue apps que comparten repositorio (p.ej. varios juegos CPS2).</summary>
    public string AssetFilter { get; set; } = "";
}

public sealed class QuiverRelease
{
    public string Version { get; set; } = "";
    public List<ReleaseAsset> Assets { get; set; } = new();
}

public sealed class QuiverCatalogEntry
{
    public string Slug { get; init; } = "";
    public string FolderName { get; init; } = "";
    public string Repository { get; set; } = "";
    public string Provider { get; set; } = "github";
    public string Verified { get; set; } = "";
    public string Latest { get; set; } = "";
    public string AssetFilter { get; set; } = "";
}

/// <summary>
/// Instantánea de una biblioteca de Quiver Launcher (2.x/3.x portable, Velopack o instalada).
/// Formatos sacados del propio código de Quiver: apps.json (o games.json antiguo), settings.json
/// (appsPath, manuallyHiddenApps), Cache/version_cache.json, Cache/CustomIcons/&lt;folderName&gt;_custom.*
/// y los ficheros de catálogo catalog-release-status/links.json cuando existen.
/// </summary>
public sealed class QuiverLibrary
{
    public string Root { get; }
    public string? CacheDir { get; }
    public IReadOnlyList<string> AppsFolders { get; }
    public IReadOnlyList<QuiverApp> Apps { get; }

    private readonly Dictionary<string, QuiverApp> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, QuiverApp> _byFolder = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, QuiverApp> _byNorm = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, QuiverRelease> _releases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, QuiverCatalogEntry> _catalogByFolder = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, QuiverCatalogEntry> _catalogByNorm = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, QuiverCatalogEntry> _catalogByRepo = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<QuiverCatalogEntry> Catalog => _catalogByFolder.Values;

    private QuiverLibrary(string root, string? cacheDir, List<string> appsFolders, List<QuiverApp> apps)
    {
        Root = root; CacheDir = cacheDir; AppsFolders = appsFolders; Apps = apps;
    }

    // ------------------------------------------------------------------ detección

    public static string? LibraryFile(string dir)
    {
        foreach (var f in new[] { "apps.json", "games.json" })
        {
            var p = Path.Combine(dir, f);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>
    /// Normaliza una carpeta elegida por el usuario a la raíz de datos de Quiver: acepta la carpeta
    /// portable, su subcarpeta "current" (Velopack) o una carpeta que la contenga.
    /// </summary>
    public static string? ResolveRoot(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        try
        {
            dir = Path.GetFullPath(dir.Trim().Trim('"'));
            if (!Directory.Exists(dir)) return null;
            if (LibraryFile(dir) != null) return dir;
            var parent = Path.GetDirectoryName(dir);
            if (parent != null && string.Equals(Path.GetFileName(dir), "current", StringComparison.OrdinalIgnoreCase) && LibraryFile(parent) != null)
                return parent;
            var cur = Path.Combine(dir, "current");
            if (LibraryFile(cur) != null) return cur;
            foreach (var sub in SafeDirs(dir).Where(d => Path.GetFileName(d).StartsWith("Quiver", StringComparison.OrdinalIgnoreCase)))
            {
                if (LibraryFile(sub) != null) return sub;
                if (LibraryFile(Path.Combine(sub, "current")) != null) return Path.Combine(sub, "current");
            }
        }
        catch { }
        return null;
    }

    /// <summary>Busca Quiver: ruta configurada, dentro/al lado de la raíz de juegos y en %LOCALAPPDATA%.</summary>
    public static string? Detect(string? configured, string? libraryRoot)
    {
        var fromCfg = ResolveRoot(configured);
        if (fromCfg != null) return fromCfg;

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(libraryRoot) && Directory.Exists(libraryRoot))
        {
            candidates.Add(libraryRoot);
            candidates.AddRange(SafeDirs(libraryRoot).Where(d => Path.GetFileName(d).Contains("quiver", StringComparison.OrdinalIgnoreCase)));
            var parent = Path.GetDirectoryName(Path.GetFullPath(libraryRoot));
            if (parent != null)
                candidates.AddRange(SafeDirs(parent).Where(d => Path.GetFileName(d).Contains("quiver", StringComparison.OrdinalIgnoreCase)));
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
        {
            candidates.Add(Path.Combine(local, "QuiverLauncher"));
            candidates.Add(Path.Combine(local, "Quiver"));
            candidates.Add(Path.Combine(local, "GithubLauncher"));
        }

        // Si hay varias, la que tenga apps.json guardado más recientemente (como hace Quiver).
        string? best = null; DateTime bestTime = DateTime.MinValue;
        foreach (var c in candidates)
        {
            var r = ResolveRoot(c);
            if (r == null) continue;
            var t = File.GetLastWriteTimeUtc(LibraryFile(r)!);
            if (t > bestTime) { bestTime = t; best = r; }
        }
        return best;
    }

    // ------------------------------------------------------------------ carga con caché

    private static readonly object CacheLock = new();
    private static QuiverLibrary? _cached;
    private static string _cachedStamp = "";

    /// <summary>Carga la biblioteca; solo vuelve a parsear si alguno de los ficheros ha cambiado.</summary>
    public static QuiverLibrary? Load(string? root)
    {
        root = ResolveRoot(root);
        if (root == null) return null;
        var stamp = Stamp(root);
        lock (CacheLock)
        {
            if (_cached != null && _cachedStamp == stamp) return _cached;
        }
        var lib = LoadUncached(root);
        lock (CacheLock) { _cached = lib; _cachedStamp = stamp; }
        return lib;
    }

    public static void InvalidateCache() { lock (CacheLock) { _cached = null; _cachedStamp = ""; } }

    private static string Stamp(string root)
    {
        var parts = new List<string> { root };
        var cache = FindCacheDir(root);
        foreach (var f in new[] { LibraryFile(root), Path.Combine(root, "settings.json"),
                     cache == null ? null : Path.Combine(cache, "version_cache.json"),
                     cache == null ? null : Path.Combine(cache, "catalog-release-status.json"),
                     cache == null ? null : Path.Combine(cache, "catalog-release-links.json") })
        {
            if (f != null && File.Exists(f)) parts.Add(f + "@" + File.GetLastWriteTimeUtc(f).Ticks);
        }
        var custom = cache == null ? null : Path.Combine(cache, "CustomIcons");
        if (custom != null && Directory.Exists(custom)) parts.Add("ci@" + Directory.GetLastWriteTimeUtc(custom).Ticks);
        return string.Join("|", parts);
    }

    private static string? FindCacheDir(string root)
    {
        foreach (var c in new[] { Path.Combine(root, "Cache"), Path.Combine(root, "current", "Cache") })
            if (Directory.Exists(c)) return c;
        return null;
    }

    private static string Resolve(string root, string path)
    {
        path = path.Trim().Trim('"');
        try
        {
            if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
            var a = Path.GetFullPath(Path.Combine(root, path));
            if (Directory.Exists(a)) return a;
            var b = Path.GetFullPath(Path.Combine(root, "current", path));
            if (Directory.Exists(b)) return b;
            return a;
        }
        catch { return path; }
    }

    private static QuiverLibrary LoadUncached(string root)
    {
        var cacheDir = FindCacheDir(root);

        // settings.json: carpeta de apps y ocultas
        var appsFolders = new List<string>();
        var hiddenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var settings = JsonUtil.ReadFile(Path.Combine(root, "settings.json")))
        {
            if (settings != null)
            {
                var s = settings.RootElement;
                var appsPath = s.Str("appsPath", "gamesFolder", "gamesPath", "installPath");
                if (appsPath != null) appsFolders.Add(Resolve(root, appsPath));
                foreach (var h in s.Strings("manuallyHiddenApps")) hiddenKeys.Add(h);
            }
        }
        appsFolders.Add(Path.Combine(root, "Apps"));
        appsFolders.Add(Path.Combine(root, "current", "Apps"));
        appsFolders = appsFolders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var apps = new List<QuiverApp>();
        var libFile = LibraryFile(root);
        using (var doc = libFile == null ? null : JsonUtil.ReadFile(libFile))
        {
            if (doc != null)
            {
                var rootEl = doc.RootElement;
                IEnumerable<JsonElement> list = rootEl.ValueKind == JsonValueKind.Array ? rootEl.EnumerateArray()
                    : rootEl.Arr("apps").Any() ? rootEl.Arr("apps") : rootEl.Arr("games");
                foreach (var a in list)
                {
                    if (a.ValueKind != JsonValueKind.Object) continue;
                    var name = a.Str("name", "displayName", "customDisplayName") ?? "";
                    var folder = a.Str("folderName") ?? "";
                    var repo = a.Str("repository") ?? "";
                    if (name.Length == 0 && folder.Length == 0 && repo.Length == 0) continue;

                    var catalog = a.Obj("catalog");
                    var icon = a.Str("appIconUrl", "gameIconUrl") ?? catalog?.Str("appIconUrl", "gameIconUrl") ?? "";
                    var tags = a.Strings("tags");
                    if (tags.Count == 0 && catalog != null) tags = catalog.Value.Strings("tags");
                    var key = folder.Length > 0 ? "folder:" + folder : repo.Length > 0 ? "repo:" + repo : "name:" + name;

                    var app = new QuiverApp
                    {
                        Name = a.Str("customDisplayName") ?? name,
                        FolderName = folder,
                        Repository = repo,
                        Provider = (a.Str("repositorySource", "provider") ?? "github").ToLowerInvariant(),
                        Project = a.Str("project") ?? "",
                        AppIconUrl = icon.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? icon : "",
                        Tags = tags,
                        Hidden = hiddenKeys.Contains(key) || hiddenKeys.Contains(name),
                        AssetFilter = a.Str("releaseAssetFilter") ?? "",
                    };

                    // ¿Dónde está instalada? installPath propio, o <appsFolder>/<folderName>
                    var own = a.Str("installPath");
                    var candidates = new List<string>();
                    if (own != null) candidates.Add(Resolve(root, own));
                    if (folder.Length > 0) candidates.AddRange(appsFolders.Select(f => Path.Combine(f, folder)));
                    app.InstallPath = candidates.FirstOrDefault(Directory.Exists) ?? (own != null ? Resolve(root, own) : "");

                    var customNamed = a.Str("customIconPath", "customIcon");
                    if (customNamed != null)
                    {
                        var p = Resolve(root, customNamed);
                        if (File.Exists(p)) app.CustomIconPath = p;
                    }
                    apps.Add(app);
                }
            }
        }

        var lib = new QuiverLibrary(root, cacheDir, appsFolders, apps);
        lib.Index();
        lib.LoadVersionCache();
        lib.LoadCatalog();
        return lib;
    }

    private void Index()
    {
        var customDirs = new List<string>();
        if (CacheDir != null) customDirs.Add(Path.Combine(CacheDir, "CustomIcons"));
        customDirs.Add(Path.Combine(Root, "CustomIcons"));
        var customFiles = customDirs.Where(Directory.Exists).SelectMany(d => SafeFiles(d)).Where(IsImage).ToList();

        foreach (var app in Apps)
        {
            if (string.IsNullOrEmpty(app.CustomIconPath) && app.FolderName.Length > 0)
            {
                var stem = app.FolderName + "_custom";
                app.CustomIconPath = customFiles.FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), stem, StringComparison.OrdinalIgnoreCase)) ?? "";
            }
            if (app.InstallPath.Length > 0) _byPath.TryAdd(PathUtil.Norm(app.InstallPath), app);
            if (app.FolderName.Length > 0)
            {
                _byFolder.TryAdd(app.FolderName, app);
                var n = PathUtil.NormName(app.FolderName);
                if (n.Length >= 5) _byNorm.TryAdd(n, app);
            }
        }
    }

    private void LoadVersionCache()
    {
        if (CacheDir == null) return;
        using var doc = JsonUtil.ReadFile(Path.Combine(CacheDir, "version_cache.json"));
        if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Object) return;
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var v = prop.Value;
            if (v.ValueKind != JsonValueKind.Object) continue;
            var rel = new QuiverRelease { Version = v.Str("Version") ?? "" };
            var cached = v.Obj("CachedRelease");
            if (cached != null)
            {
                if (rel.Version.Length == 0) rel.Version = cached.Value.Str("tag_name") ?? "";
                foreach (var asset in cached.Value.Arr("assets"))
                {
                    var name = asset.Str("name"); var url = asset.Str("browser_download_url", "url");
                    if (name != null && url != null)
                        rel.Assets.Add(new ReleaseAsset(name, url, asset.Str("digest"), asset.TryGet("size", out var sz) && sz.TryGetInt64(out var l) ? l : 0));
                }
            }
            if (rel.Version.Length == 0 && rel.Assets.Count == 0) continue;
            PutRelease(prop.Name, rel);
        }
    }

    private void PutRelease(string key, QuiverRelease rel)
    {
        // claves de Quiver: "github:Owner/Repo", "gitlab:group/project" o solo "Owner/Repo"
        var idx = key.IndexOf(':');
        var provider = idx > 0 ? key[..idx].ToLowerInvariant() : "github";
        var repo = idx > 0 ? key[(idx + 1)..] : key;
        _releases[provider + ":" + repo] = rel;
        _releases.TryAdd(repo, rel);
    }

    private void LoadCatalog()
    {
        if (CacheDir == null) return;
        var statusBySlug = new Dictionary<string, QuiverCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        using (var sdoc = JsonUtil.ReadFile(Path.Combine(CacheDir, "catalog-release-status.json")))
        {
            if (sdoc != null)
            {
                IEnumerable<JsonElement> items = sdoc.RootElement.ValueKind == JsonValueKind.Array ? sdoc.RootElement.EnumerateArray()
                    : sdoc.RootElement.ValueKind == JsonValueKind.Object ? sdoc.RootElement.EnumerateObject().Select(p => p.Value) : Enumerable.Empty<JsonElement>();
                foreach (var e in items)
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    var entry = new QuiverCatalogEntry
                    {
                        Slug = e.Str("slug") ?? "",
                        FolderName = e.Str("folderName") ?? "",
                        Repository = e.Str("repository") ?? "",
                        Provider = (e.Str("provider", "repositorySource") ?? "github").ToLowerInvariant(),
                        Verified = e.Obj("verified")?.Str("version") ?? e.Str("verified") ?? "",
                        Latest = e.Obj("latestUpstream")?.Str("version") ?? e.Str("latestUpstream") ?? "",
                    };
                    if (entry.Slug.Length > 0) statusBySlug[entry.Slug] = entry;
                    if (entry.Repository.Length > 0) _catalogByRepo.TryAdd(entry.Repository, entry);
                    if (entry.FolderName.Length > 0) AddCatalog(entry);
                }
            }
        }
        using var ldoc = JsonUtil.ReadFile(Path.Combine(CacheDir, "catalog-release-links.json"));
        if (ldoc == null || ldoc.RootElement.ValueKind != JsonValueKind.Object) return;
        foreach (var prop in ldoc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
            var folder = prop.Value.Str("folderName");
            if (folder == null) continue;
            statusBySlug.TryGetValue(prop.Name, out var st);
            var entry = new QuiverCatalogEntry
            {
                Slug = prop.Name,
                FolderName = folder,
                Repository = st?.Repository ?? prop.Value.Str("repository") ?? "",
                Provider = st?.Provider ?? (prop.Value.Str("provider", "repositorySource") ?? "github").ToLowerInvariant(),
                Verified = st?.Verified ?? "",
                Latest = st?.Latest ?? "",
                AssetFilter = prop.Value.Str("releaseAssetFilter") ?? "",
            };
            AddCatalog(entry);
        }
    }

    private void AddCatalog(QuiverCatalogEntry e)
    {
        _catalogByFolder[e.FolderName] = e;
        var n = PathUtil.NormName(e.FolderName);
        if (n.Length >= 6) _catalogByNorm.TryAdd(n, e);
        if (e.Repository.Length > 0) _catalogByRepo.TryAdd(e.Repository, e);
    }

    // ------------------------------------------------------------------ consultas

    public QuiverApp? AppByPath(string folderPath) => _byPath.TryGetValue(PathUtil.Norm(folderPath), out var a) ? a : null;
    public QuiverApp? AppByFolderName(string folderName) => _byFolder.TryGetValue(folderName, out var a) ? a : null;
    public QuiverApp? AppByNormName(string norm) => norm.Length >= 5 && _byNorm.TryGetValue(norm, out var a) ? a : null;

    public QuiverCatalogEntry? CatalogByFolderName(string folderName) => _catalogByFolder.TryGetValue(folderName, out var e) ? e : null;
    public QuiverCatalogEntry? CatalogByNormName(string norm) => norm.Length >= 6 && _catalogByNorm.TryGetValue(norm, out var e) ? e : null;
    public QuiverCatalogEntry? CatalogByRepo(string repo) => _catalogByRepo.TryGetValue(repo, out var e) ? e : null;

    /// <summary>Entrada de catálogo de una app: por carpeta si existe (repos compartidos), si no por repositorio.</summary>
    public QuiverCatalogEntry? CatalogFor(QuiverApp app) =>
        (app.FolderName.Length > 0 ? CatalogByFolderName(app.FolderName) : null) ?? CatalogByRepo(app.Repository);

    public QuiverRelease? Release(string provider, string repository)
    {
        if (string.IsNullOrWhiteSpace(repository)) return null;
        if (_releases.TryGetValue((string.IsNullOrWhiteSpace(provider) ? "github" : provider) + ":" + repository, out var r)) return r;
        return _releases.TryGetValue(repository, out r) ? r : null;
    }

    /// <summary>Última versión que conoce Quiver (caché de releases o catálogo upstream).</summary>
    public string? LatestKnown(string provider, string repository)
    {
        var fromCache = VersionUtil.Clean(Release(provider, repository)?.Version);
        var cat = CatalogByRepo(repository);
        var fromCatalog = VersionUtil.Clean(cat?.Latest);
        if (fromCache == null) return fromCatalog;
        if (fromCatalog == null) return fromCache;
        return VersionUtil.IsNewer(fromCatalog, fromCache) ? fromCatalog : fromCache;
    }

    // ------------------------------------------------------------------ helpers

    private static bool IsImage(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".ico" or ".gif";
    }

    internal static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); } catch { return Array.Empty<string>(); }
    }

    internal static IEnumerable<string> SafeFiles(string dir, string pattern = "*")
    {
        try { return Directory.GetFiles(dir, pattern); } catch { return Array.Empty<string>(); }
    }
}
