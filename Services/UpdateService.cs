using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using RecompVault.Core;
using RecompVault.Models;

namespace RecompVault.Services;

public enum CheckResult { NoRepository, UpToDate, UpdateAvailable, LatestOnly, NotFound, RateLimited, Error, Skipped }

/// <summary>
/// Comprobación e instalación de actualizaciones desde GitHub/GitLab Releases.
/// - Versión instalada: version.txt de la carpeta (el mismo fichero que usa Quiver).
/// - Respuestas cacheadas en disco con ETag (las 304 no gastan cuota de la API de GitHub).
/// - Sin consultas repetidas antes de <see cref="AppConfig.UpdateCheckIntervalHours"/> salvo comprobación manual.
/// </summary>
public static class UpdateService
{
    private static readonly HttpClient Http = CreateHttp();
    private static readonly string CachePath = Path.Combine(ConfigService.Dir, "release_cache.json");
    private static readonly object CacheGate = new();
    private static Dictionary<string, CachedRelease>? _cache;
    private static DateTime _rateLimitedUntilUtc = DateTime.MinValue;

    public static bool IsRateLimited => DateTime.UtcNow < _rateLimitedUntilUtc;
    public static DateTime RateLimitResetLocal => _rateLimitedUntilUtc.ToLocalTime();

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 6
        };
        var h = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }; // los timeouts van por petición
        h.DefaultRequestHeaders.UserAgent.ParseAdd("RecompVault/1.1 (+https://github.com/Sir-BLaDE/RecompVault)");
        return h;
    }

    // ------------------------------------------------------------------ versión instalada

    public static string? ReadInstalledVersion(string gameFolder)
    {
        try
        {
            var p = Path.Combine(gameFolder, "version.txt");
            if (!File.Exists(p) || new FileInfo(p).Length > 4096) return null;
            return VersionUtil.Clean(File.ReadAllText(p));
        }
        catch { return null; }
    }

    /// <summary>installed-build.txt de Quiver 3.5: "nombre-del-paquete\nsha256". Devuelve el nombre o null.</summary>
    public static string? ReadInstalledAssetName(string gameFolder)
    {
        try
        {
            var p = Path.Combine(gameFolder, "installed-build.txt");
            if (!File.Exists(p) || new FileInfo(p).Length > 4096) return null;
            var lines = File.ReadAllLines(p);
            var name = lines.Length > 0 ? lines[0].Trim() : "";
            // formato antiguo de RecompVault ("nombre\nversión") también vale para el nombre
            return name.Length > 0 && name.Contains('.') ? name : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// installed-build.txt con el formato de la versión antigua de RecompVault ("paquete" + "versión" en vez de
    /// "paquete" + "sha256"): esa carpeta la actualizó el RecompVault anterior, que no guardaba copia para deshacer.
    /// </summary>
    public static bool UpdatedByOldRecompVault(string gameFolder)
    {
        try
        {
            var p = Path.Combine(gameFolder, "installed-build.txt");
            if (!File.Exists(p) || new FileInfo(p).Length > 4096) return false;
            var lines = File.ReadAllLines(p).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
            return lines.Length >= 2 && !(lines[1].Length == 64 && lines[1].All(Uri.IsHexDigit));
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ caché de releases

    public sealed class CachedRelease
    {
        public string Tag { get; set; } = "";
        public bool Prerelease { get; set; }
        public List<ReleaseAsset> Assets { get; set; } = new();
        public string? ETag { get; set; }
        public string? Url { get; set; }
        public DateTime CheckedUtc { get; set; }
        public bool NotFound { get; set; }
        public string? Notes { get; set; }
    }

    private static Dictionary<string, CachedRelease> Cache
    {
        get
        {
            lock (CacheGate)
            {
                if (_cache != null) return _cache;
                try
                {
                    if (File.Exists(CachePath))
                        _cache = JsonSerializer.Deserialize<Dictionary<string, CachedRelease>>(File.ReadAllText(CachePath));
                }
                catch { }
                _cache = new Dictionary<string, CachedRelease>(_cache ?? new(), StringComparer.OrdinalIgnoreCase);
                return _cache;
            }
        }
    }

    private static void SaveCache()
    {
        lock (CacheGate)
        {
            try
            {
                Directory.CreateDirectory(ConfigService.Dir);
                var tmp = CachePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_cache));
                File.Move(tmp, CachePath, true);
            }
            catch (Exception ex) { Log.Warn("No se pudo guardar release_cache.json: " + ex.Message); }
        }
    }

    // ------------------------------------------------------------------ consulta remota

    private sealed record FetchResult(CheckResult Status, CachedRelease? Release);

    private static async Task<FetchResult> FetchAsync(string provider, string repo, AppConfig cfg, bool force, CancellationToken ct)
    {
        repo = repo.Trim().Trim('/');
        if (repo.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // por si alguien guardó la URL completa
            var u = new Uri(repo);
            repo = u.AbsolutePath.Trim('/');
            if (u.Host.Contains("gitlab", StringComparison.OrdinalIgnoreCase)) provider = "gitlab";
        }
        bool gitlab = provider.Equals("gitlab", StringComparison.OrdinalIgnoreCase);
        var key = (gitlab ? "gitlab:" : "github:") + repo;

        CachedRelease? cached;
        lock (CacheGate) Cache.TryGetValue(key, out cached);
        var interval = TimeSpan.FromHours(Math.Max(0, cfg.UpdateCheckIntervalHours));
        if (!force && cached != null && DateTime.UtcNow - cached.CheckedUtc < interval)
            return new FetchResult(cached.NotFound ? CheckResult.NotFound : CheckResult.Skipped, cached);

        if (!gitlab && IsRateLimited) return new FetchResult(CheckResult.RateLimited, cached);

        var url = gitlab
            ? $"https://gitlab.com/api/v4/projects/{Uri.EscapeDataString(repo)}/releases?per_page=10"
            : $"https://api.github.com/repos/{repo}/releases?per_page=10";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!gitlab)
        {
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            var token = cfg.GitHubToken?.Trim();
            if (!string.IsNullOrEmpty(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        if (cached?.ETag != null && cached.Url == url && !cached.NotFound)
            req.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(cached.ETag));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(25));
        HttpResponseMessage resp;
        try { resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Warn($"Consulta de releases fallida {key}: {ex.Message}");
            return new FetchResult(CheckResult.Error, cached);
        }

        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.NotModified && cached != null)
            {
                cached.CheckedUtc = DateTime.UtcNow;
                SaveCache();
                return new FetchResult(CheckResult.Skipped, cached);
            }
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests && !gitlab)
            {
                if (resp.Headers.TryGetValues("X-RateLimit-Remaining", out var rem) && rem.FirstOrDefault() == "0")
                {
                    var reset = resp.Headers.TryGetValues("X-RateLimit-Reset", out var rs) && long.TryParse(rs.FirstOrDefault(), out var epoch)
                        ? DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime : DateTime.UtcNow.AddMinutes(30);
                    _rateLimitedUntilUtc = reset;
                    Log.Warn($"Límite de la API de GitHub alcanzado hasta {reset.ToLocalTime():HH:mm}");
                    return new FetchResult(CheckResult.RateLimited, cached);
                }
            }
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                var nf = new CachedRelease { NotFound = true, CheckedUtc = DateTime.UtcNow, Url = url };
                lock (CacheGate) Cache[key] = nf;
                SaveCache();
                Log.Warn($"Repositorio no encontrado: {key}");
                return new FetchResult(CheckResult.NotFound, null);
            }
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn($"Releases {key}: HTTP {(int)resp.StatusCode}");
                return new FetchResult(CheckResult.Error, cached);
            }

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var rel = gitlab ? ParseGitLab(json, cfg.IncludePrereleases) : ParseGitHub(json, cfg.IncludePrereleases);
            if (rel == null)
            {
                var nf = new CachedRelease { NotFound = true, CheckedUtc = DateTime.UtcNow, Url = url };
                lock (CacheGate) Cache[key] = nf;
                SaveCache();
                return new FetchResult(CheckResult.NotFound, null);
            }
            rel.ETag = resp.Headers.ETag?.ToString();
            rel.Url = url;
            rel.CheckedUtc = DateTime.UtcNow;
            lock (CacheGate) Cache[key] = rel;
            SaveCache();
            return new FetchResult(CheckResult.Skipped, rel);
        }
    }

    public sealed record ReleaseInfo(string Tag, string Title, DateTime? Published, bool Prerelease, List<ReleaseAsset> Assets, string Notes);

    /// <summary>Lista de releases del repositorio (para elegir una versión concreta). No usa la caché.</summary>
    public static async Task<(List<ReleaseInfo> releases, string? error)> ListReleasesAsync(string provider, string repo, AppConfig cfg, CancellationToken ct = default)
    {
        var list = new List<ReleaseInfo>();
        repo = repo.Trim().Trim('/');
        bool gitlab = string.Equals(provider, "gitlab", StringComparison.OrdinalIgnoreCase);
        if (!gitlab && IsRateLimited) return (list, $"Límite de la API de GitHub alcanzado hasta las {RateLimitResetLocal:HH:mm}.");
        var url = gitlab
            ? $"https://gitlab.com/api/v4/projects/{Uri.EscapeDataString(repo)}/releases?per_page=50"
            : $"https://api.github.com/repos/{repo}/releases?per_page=50";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!gitlab)
            {
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                var token = cfg.GitHubToken?.Trim();
                if (!string.IsNullOrEmpty(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotFound) return (list, $"El repositorio {repo} no existe en {(gitlab ? "GitLab" : "GitHub")}.");
            if (!resp.IsSuccessStatusCode) return (list, $"HTTP {(int)resp.StatusCode} al consultar {repo}.");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return (list, "Respuesta inesperada del servidor.");
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                if (r.TryGet("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
                var tag = r.Str("tag_name") ?? r.Str("name");
                if (tag == null) continue;
                var assets = new List<ReleaseAsset>();
                if (gitlab)
                {
                    var a = r.Obj("assets");
                    if (a != null)
                        foreach (var l in a.Value.Arr("links"))
                        {
                            var n = l.Str("name"); var u = l.Str("direct_asset_url", "url");
                            if (n != null && u != null) assets.Add(new ReleaseAsset(n, u));
                        }
                }
                else
                {
                    foreach (var a in r.Arr("assets"))
                    {
                        var n = a.Str("name"); var u = a.Str("browser_download_url");
                        if (n != null && u != null)
                            assets.Add(new ReleaseAsset(n, u, a.Str("digest"), a.TryGet("size", out var sz) && sz.TryGetInt64(out var len) ? len : 0));
                    }
                }
                var pre = gitlab ? (r.TryGet("upcoming_release", out var up) && up.ValueKind == JsonValueKind.True)
                                 : (r.TryGet("prerelease", out var p) && p.ValueKind == JsonValueKind.True);
                DateTime? published = DateTime.TryParse(r.Str("published_at", "released_at", "created_at"), null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt) ? dt : null;
                var notes = r.Str("body", "description") ?? "";
                list.Add(new ReleaseInfo(tag, r.Str("name") ?? tag, published, pre, assets, notes));
            }
            return (list, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"Lista de releases {repo}: {ex.Message}");
            return (list, ex.Message);
        }
    }

    private static string? Truncate(string? s, int max) => s == null ? null : s.Length <= max ? s : s[..max] + "\n…";

    private static CachedRelease? ParseGitHub(string json, bool includePre)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        JsonElement? pick = null, firstAny = null;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGet("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
            var pre = r.TryGet("prerelease", out var p) && p.ValueKind == JsonValueKind.True;
            firstAny ??= r;
            if (!pre || includePre) { pick = r; break; }
        }
        var chosen = pick ?? firstAny;
        if (chosen == null) return null;
        var c = chosen.Value;
        var rel = new CachedRelease
        {
            Tag = c.Str("tag_name", "name") ?? "",
            Notes = Truncate(c.Str("body"), 12000),
            Prerelease = c.TryGet("prerelease", out var pr) && pr.ValueKind == JsonValueKind.True
        };
        foreach (var a in c.Arr("assets"))
        {
            var name = a.Str("name"); var url = a.Str("browser_download_url");
            if (name == null || url == null) continue;
            rel.Assets.Add(new ReleaseAsset(name, url, a.Str("digest"), a.TryGet("size", out var s) && s.TryGetInt64(out var l) ? l : 0));
        }
        return rel.Tag.Length > 0 ? rel : null;
    }

    private static CachedRelease? ParseGitLab(string json, bool includePre)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            var upcoming = r.TryGet("upcoming_release", out var u) && u.ValueKind == JsonValueKind.True;
            if (upcoming && !includePre) continue;
            var rel = new CachedRelease { Tag = r.Str("tag_name", "name") ?? "", Prerelease = upcoming, Notes = Truncate(r.Str("description"), 12000) };
            var assets = r.Obj("assets");
            if (assets != null)
            {
                foreach (var l in assets.Value.Arr("links"))
                {
                    var name = l.Str("name"); var url = l.Str("direct_asset_url", "url");
                    if (name != null && url != null) rel.Assets.Add(new ReleaseAsset(name, url));
                }
            }
            if (rel.Tag.Length > 0) return rel;
        }
        return null;
    }

    // ------------------------------------------------------------------ comprobación

    /// <param name="force">true = consulta remota aunque se haya comprobado hace poco (comprobación manual).</param>
    public static async Task<CheckResult> CheckUpdateAsync(GameEntry g, QuiverLibrary? lib, AppConfig cfg, bool force = false, CancellationToken ct = default)
    {
        try
        {
            var installed = ReadInstalledVersion(g.FolderPath) ?? "";
            g.InstalledAssetName = ReadInstalledAssetName(g.FolderPath);
            UiThread.RunSync(() => g.InstalledVersion = installed);
            QuiverService.ApplyKnownLatest(g, lib);

            if (string.IsNullOrWhiteSpace(g.Repository)) return CheckResult.NoRepository;

            var res = await FetchAsync(g.Provider, g.Repository!, cfg, force, ct).ConfigureAwait(false);
            if (res.Release != null && !res.Release.NotFound && res.Release.Tag.Length > 0)
            {
                var asset = ReleaseAssetPicker.Pick(res.Release.Assets, g.AssetFilter, g.InstalledAssetName);
                UiThread.RunSync(() =>
                {
                    g.LatestVersion = res.Release.Tag;
                    g.LatestNotes = res.Release.Notes;
                    g.UpdateAssetUrl = asset?.Url; g.UpdateAssetName = asset?.Name; g.UpdateAssetDigest = asset?.Digest;
                });
            }
            if (res.Status is CheckResult.RateLimited or CheckResult.NotFound or CheckResult.Error && string.IsNullOrWhiteSpace(g.LatestVersion))
                return res.Status;
            if (g.HasUpdate) return CheckResult.UpdateAvailable;
            if (g.InstalledUnknown && !string.IsNullOrWhiteSpace(g.LatestVersion)) return CheckResult.LatestOnly;
            return res.Status is CheckResult.RateLimited or CheckResult.Error ? res.Status : CheckResult.UpToDate;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"CheckUpdate {g.Name}", ex);
            return CheckResult.Error;
        }
    }

    /// <summary>Comprueba varias entradas en paralelo (4 a la vez).</summary>
    public static async Task<Dictionary<CheckResult, int>> CheckManyAsync(IReadOnlyList<GameEntry> games, QuiverLibrary? lib, AppConfig cfg,
        bool force, IProgress<(int done, int total)>? progress, CancellationToken ct = default)
    {
        var counts = new Dictionary<CheckResult, int>();
        int done = 0;
        using var gate = new SemaphoreSlim(4);
        var tasks = games.Select(async g =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var r = await CheckUpdateAsync(g, lib, cfg, force, ct).ConfigureAwait(false);
                lock (counts) counts[r] = counts.TryGetValue(r, out var n) ? n + 1 : 1;
            }
            finally
            {
                gate.Release();
                progress?.Report((Interlocked.Increment(ref done), games.Count));
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return counts;
    }

    // ------------------------------------------------------------------ instalación

    public sealed record UpdateOutcome(bool Ok, string Message, bool Manual = false);

    /// <summary>Versión y paquete concretos elegidos por el usuario ("Instalar una versión concreta…").</summary>
    public sealed record InstallChoice(string Version, ReleaseAsset Asset);

    public static async Task<UpdateOutcome> UpdateAsync(GameEntry g, AppConfig cfg, IProgress<string>? progress = null, CancellationToken ct = default,
        InstallChoice? choice = null)
    {
        if (choice == null && string.IsNullOrWhiteSpace(g.UpdateAssetUrl) && !string.IsNullOrWhiteSpace(g.Repository))
        {
            progress?.Report($"Buscando paquete de Windows para {g.Name}...");
            await CheckUpdateAsync(g, null, cfg, force: true, ct).ConfigureAwait(false);
        }
        if (choice == null && string.IsNullOrWhiteSpace(g.UpdateAssetUrl))
            return new UpdateOutcome(false, "El release no tiene un paquete de Windows descargable.");

        var running = RunningProcessesIn(g.FolderPath);
        if (running.Count > 0)
            return new UpdateOutcome(false, $"Cierra el juego antes de actualizar ({string.Join(", ", running)}).");

        var url = choice?.Asset.Url ?? g.UpdateAssetUrl!;
        var name = choice?.Asset.Name ?? (string.IsNullOrWhiteSpace(g.UpdateAssetName) ? Path.GetFileName(new Uri(url).AbsolutePath) : g.UpdateAssetName!);
        if (string.IsNullOrWhiteSpace(name)) name = "update.zip";
        name = PathUtil.SafeFileName(name, 150);
        var version = choice?.Version ?? g.LatestVersion;
        var digest = choice != null ? choice.Asset.Digest : g.UpdateAssetDigest;

        var tempRoot = Path.Combine(Path.GetTempPath(), "RecompVaultUpdate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        UndoSession? undo = null;
        int kept = 0;
        try
        {
            var downloadPath = Path.Combine(tempRoot, name);
            var err = await DownloadAsync(url, downloadPath, cfg, s => progress?.Report($"Descargando {g.Name} {version}: {s}"), ct).ConfigureAwait(false);
            if (choice == null && err != null && (err.Contains("404") || err.Contains("410")) && !string.IsNullOrWhiteSpace(g.Repository))
            {
                // el asset de la caché ya no existe: pedir el release actual y reintentar una vez
                progress?.Report("El paquete ya no existe, buscando el release actual...");
                await CheckUpdateAsync(g, null, cfg, force: true, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(g.UpdateAssetUrl) && g.UpdateAssetUrl != url)
                {
                    url = g.UpdateAssetUrl!; version = g.LatestVersion; digest = g.UpdateAssetDigest;
                    name = PathUtil.SafeFileName(g.UpdateAssetName ?? Path.GetFileName(new Uri(url).AbsolutePath), 150);
                    downloadPath = Path.Combine(tempRoot, name);
                    err = await DownloadAsync(url, downloadPath, cfg, s => progress?.Report($"Descargando {g.Name} {version}: {s}"), ct).ConfigureAwait(false);
                }
            }
            if (err != null) return new UpdateOutcome(false, "Descarga fallida: " + err);

            string sha;
            await using (var s = File.OpenRead(downloadPath))
                sha = Convert.ToHexString(await SHA256.HashDataAsync(s, ct).ConfigureAwait(false)).ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(digest) && digest!.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report("Verificando SHA-256...");
                var expected = digest["sha256:".Length..].Trim();
                if (!sha.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    return new UpdateOutcome(false, "La verificación SHA-256 no coincide; descarga descartada.");
            }

            progress?.Report($"Instalando {g.Name} {version}...");
            var lower = name.ToLowerInvariant();

            if (ReleaseAssetPicker.IsInstaller(name))
            {
                // Un instalador no se puede aplicar en silencio: se deja en la carpeta y se abre.
                var dest = Path.Combine(g.FolderPath, name);
                File.Copy(downloadPath, dest, true);
                try { Process.Start(new ProcessStartInfo { FileName = dest, WorkingDirectory = g.FolderPath, UseShellExecute = true }); } catch { }
                return new UpdateOutcome(false, $"Es un instalador ({name}); se ha abierto para que lo completes. La versión se marcará cuando vuelvas a comprobar.", Manual: true);
            }

            // A partir de aquí todo lo que se toca queda en la copia de deshacer; si algo falla se revierte.
            undo = UndoSession.Begin(g.FolderPath, g.Name, g.InstalledVersion, version, name);
            if (lower.EndsWith(".exe"))
            {
                undo.BeforeWrite(name);
                File.Copy(downloadPath, Path.Combine(g.FolderPath, name), true);
            }
            else
            {
                var extractDir = Path.Combine(tempRoot, "extract");
                Directory.CreateDirectory(extractDir);
                var extracted = await ExtractAsync(downloadPath, lower, extractDir, ct).ConfigureAwait(false);
                if (extracted != null) { undo.Rollback(); return new UpdateOutcome(false, extracted); }
                var copyErr = ApplyExtracted(g.FolderPath, extractDir, undo, ProtectedPatternsFor(g, cfg), out kept);
                if (copyErr != null)
                {
                    var rb = undo.Rollback();
                    return new UpdateOutcome(false, copyErr + (rb == null ? " No se ha cambiado nada (se restauró lo anterior)." : " ATENCIÓN: " + rb));
                }
            }

            if (!string.IsNullOrWhiteSpace(version))
            {
                // Mismos ficheros y formato que Quiver, para que ambos launchers vean la misma versión instalada.
                undo.BeforeWrite("version.txt");
                File.WriteAllText(Path.Combine(g.FolderPath, "version.txt"), version);
                undo.BeforeWrite("installed-build.txt");
                File.WriteAllText(Path.Combine(g.FolderPath, "installed-build.txt"), $"{name}\n{sha}\n");
            }
            undo.Commit(Math.Max(1, cfg.UndoBackupsToKeep));
            g.InstalledAssetName = name;

            var launch = ScannerService.ResolveLaunch(g.FolderPath, Path.GetFileName(g.FolderPath), cfg);
            UiThread.RunSync(() =>
            {
                g.InstalledVersion = version;
                if (!string.IsNullOrWhiteSpace(launch)) g.LaunchPath = launch;
                g.HasUndo = true;
                g.RefreshLaunchState();
            });
            Log.Info($"Actualizado {g.Name} a {version} desde {url}");
            return new UpdateOutcome(true, $"Actualizado: {g.Name} → {version}" + (kept > 0 ? $" ({kept} ficheros protegidos conservados)" : "") +
                ". Si no es el juego correcto: ↶ deshace la actualización.");
        }
        catch (OperationCanceledException)
        {
            var rb = undo?.Rollback();
            return new UpdateOutcome(false, "Cancelado." + (rb != null ? " " + rb : ""));
        }
        catch (Exception ex)
        {
            Log.Error($"Error actualizando {g.Name}", ex);
            var rb = undo?.Rollback();
            return new UpdateOutcome(false, ex.Message + (rb == null ? (undo != null ? " (se restauró lo anterior)" : "") : " ATENCIÓN: " + rb));
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static async Task<string?> DownloadAsync(string url, string dest, AppConfig cfg, Action<string> report, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            var token = cfg.GitHubToken?.Trim();
            // El token solo se envía a GitHub (nunca a otros hosts de descarga).
            if (!string.IsNullOrEmpty(token) && new Uri(url).Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(30));
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return $"HTTP {(int)resp.StatusCode}";
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            await using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
            var buffer = new byte[1 << 16];
            long read = 0; int n; var sw = Stopwatch.StartNew();
            while ((n = await src.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, n), cts.Token).ConfigureAwait(false);
                read += n;
                if (sw.ElapsedMilliseconds > 400)
                {
                    sw.Restart();
                    report(total > 0 ? $"{read * 100 / total}% ({read / 1048576} / {total / 1048576} MB)" : $"{read / 1048576} MB");
                }
            }
            if (total > 0 && read < total) return "descarga incompleta";
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Extrae el paquete. Devuelve null si va bien o el mensaje de error.</summary>
    private static async Task<string?> ExtractAsync(string archive, string lowerName, string dest, CancellationToken ct)
    {
        try
        {
            if (lowerName.EndsWith(".zip") || IsZip(archive))
            {
                await Task.Run(() => ZipFile.ExtractToDirectory(archive, dest, overwriteFiles: true), ct).ConfigureAwait(false);
                return null;
            }
            if (lowerName.EndsWith(".tar.gz") || lowerName.EndsWith(".tgz"))
            {
                await using var fs = File.OpenRead(archive);
                await using var gz = new GZipStream(fs, CompressionMode.Decompress);
                await TarFile.ExtractToDirectoryAsync(gz, dest, overwriteFiles: true, ct).ConfigureAwait(false);
                return null;
            }
            if (lowerName.EndsWith(".tar"))
            {
                await TarFile.ExtractToDirectoryAsync(archive, dest, overwriteFiles: true, ct).ConfigureAwait(false);
                return null;
            }
            // .7z / .rar / .tar.xz: con 7-Zip si está instalado
            var sevenZip = Find7Zip();
            if (sevenZip == null)
                return $"El paquete es {Path.GetExtension(lowerName)} y no se encontró 7-Zip. Instala 7-Zip (7-zip.org) o actualiza a mano.";
            var psi = new ProcessStartInfo(sevenZip)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var a in new[] { "x", "-y", "-o" + dest, archive }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var stderr = p.StandardError.ReadToEndAsync(ct);
            _ = p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            if (p.ExitCode > 1) return "7-Zip falló al extraer: " + (await stderr).Trim();
            // .tar.xz → queda un .tar dentro
            var innerTar = Directory.GetFiles(dest, "*.tar").FirstOrDefault();
            if (innerTar != null && Directory.GetFileSystemEntries(dest).Length == 1)
            {
                var inner = Path.Combine(dest, "_tar");
                Directory.CreateDirectory(inner);
                await TarFile.ExtractToDirectoryAsync(innerTar, inner, true, ct).ConfigureAwait(false);
                File.Delete(innerTar);
            }
            return null;
        }
        catch (InvalidDataException) { return "El paquete descargado está dañado o no es un archivo válido."; }
    }

    private static bool IsZip(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> b = stackalloc byte[4];
            return fs.Read(b) == 4 && b[0] == 'P' && b[1] == 'K' && b[2] == 3 && b[3] == 4;
        }
        catch { return false; }
    }

    private static string? Find7Zip()
    {
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var p = Path.Combine(root, "7-Zip", "7z.exe");
            if (File.Exists(p)) return p;
        }
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { var p = Path.Combine(dir.Trim(), "7z.exe"); if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    private static readonly HashSet<string> NeverOverwrite = new(StringComparer.OrdinalIgnoreCase)
    { "version.txt", "installed-build.txt", "selected_executable.txt", "install-incomplete.txt", "LastPlayed.txt" };

    /// <summary>
    /// Copia lo extraído encima de la carpeta del juego (quitando carpetas envoltorio), apuntando cada
    /// fichero en la copia de deshacer. Al primer fallo se detiene (el llamador revierte). Devuelve error o null.
    /// </summary>
    /// <summary>Patrones protegidos efectivos: globales + los del juego.</summary>
    public static List<string> ProtectedPatternsFor(GameEntry g, AppConfig cfg)
    {
        lock (ConfigService.Sync)
        {
            var list = new List<string>(cfg.ProtectedPatterns ?? new());
            if (cfg.ProtectedByGame.TryGetValue(g.FolderPath, out var own)) list.AddRange(GlobMatcher.Parse(own));
            return list;
        }
    }

    private static string? ApplyExtracted(string targetFolder, string extractDir, UndoSession undo, List<string> protectedPatterns, out int kept)
    {
        kept = 0;
        var source = extractDir;
        // como Quiver: solo se "desenvuelve" una carpeta única sin ficheros al lado
        for (int i = 0; i < 4; i++)
        {
            var dirs = Directory.GetDirectories(source);
            var files = Directory.GetFiles(source);
            if (dirs.Length == 1 && files.Length == 0) source = dirs[0];
            else break;
        }
        if (!Directory.EnumerateFileSystemEntries(source).Any()) return "El paquete está vacío.";

        Directory.CreateDirectory(targetFolder);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            if (NeverOverwrite.Contains(rel)) continue;
            // fichero protegido que ya existe: se conserva el del usuario
            if (protectedPatterns.Count > 0 && File.Exists(Path.Combine(targetFolder, rel)) && GlobMatcher.IsMatch(rel, protectedPatterns))
            {
                kept++;
                continue;
            }
            if (rel.StartsWith(UndoSession.RootDirName, StringComparison.OrdinalIgnoreCase)) continue;
            var dest = Path.Combine(targetFolder, rel);
            try
            {
                undo.BeforeWrite(rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, true);
            }
            catch (Exception ex)
            {
                Log.Warn($"No se pudo copiar {rel}: {ex.Message}");
                return $"No se pudo reemplazar {rel} (¿en uso?): {ex.Message}.";
            }
        }
        return null;
    }

    /// <summary>Deshace la última actualización hecha por RecompVault en esta carpeta.</summary>
    public static UpdateOutcome UndoLastUpdate(GameEntry g, AppConfig cfg)
    {
        var running = RunningProcessesIn(g.FolderPath);
        if (running.Count > 0) return new UpdateOutcome(false, $"Cierra el juego antes de deshacer ({string.Join(", ", running)}).");
        var m = UndoStore.Latest(g.FolderPath);
        if (m == null) return new UpdateOutcome(false, "No hay ninguna actualización que deshacer en esta carpeta.");
        var err = UndoStore.RestoreLatest(g.FolderPath);
        if (err != null) return new UpdateOutcome(false, err);

        var installed = ReadInstalledVersion(g.FolderPath) ?? "";
        var assetName = ReadInstalledAssetName(g.FolderPath);
        var launch = ScannerService.ResolveLaunch(g.FolderPath, Path.GetFileName(g.FolderPath), cfg);
        var hasMore = UndoStore.Latest(g.FolderPath) != null;
        UiThread.RunSync(() =>
        {
            g.InstalledVersion = installed;
            g.InstalledAssetName = assetName;
            g.LaunchPath = launch;
            g.HasUndo = hasMore;
            g.RefreshLaunchState();
        });
        Log.Info($"Deshecha la actualización de {g.Name} ({m.FromVersion} ← {m.ToVersion})");
        return new UpdateOutcome(true, $"Deshecha la actualización de {g.Name}: vuelve a {(string.IsNullOrWhiteSpace(m.FromVersion) ? "la versión anterior" : m.FromVersion)}.");
    }

    /// <summary>Procesos cuyo ejecutable está dentro de la carpeta del juego.</summary>
    public static List<string> RunningProcessesIn(string folder)
    {
        var list = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (path != null && PathUtil.IsSameOrInside(path, folder)) list.Add(p.ProcessName);
            }
            catch { }
            finally { p.Dispose(); }
        }
        return list;
    }
}
