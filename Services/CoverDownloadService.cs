using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using RecompVault.Core;
using RecompVault.Models;

namespace RecompVault.Services;

public record CoverCandidate(string Url, string? ThumbUrl, string Source, string Title);

/// <summary>
/// Portadas: SteamGridDB → RAWG → Libretro → IGDB → Quiver (icono local o appIconUrl).
/// Las imágenes se guardan en %APPDATA%\RecompVault\covers con nombre estable por carpeta de juego.
/// </summary>
public static class CoverDownloadService
{
    public static readonly HttpClient Http = CreateHttp();
    public static readonly string CacheDir = Path.Combine(ConfigService.Dir, "covers");
    private static string? _igdbToken;
    private static DateTime _igdbTokenExpiry = DateTime.MinValue;
    private static readonly SemaphoreSlim IgdbGate = new(1, 1);

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 8
        }) { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("RecompVault/1.1 (cover fetcher)");
        return c;
    }

    // ------------------------------------------------------------------ automático

    public static Task<bool> DownloadAsync(GameEntry g, AppConfig cfg, IProgress<string>? progress = null, CancellationToken ct = default)
        => DownloadAsync(g, cfg.SteamGridDbApiKey ?? "", cfg, progress, ct);

    public static async Task<bool> DownloadAsync(GameEntry g, string apiKey, AppConfig cfg, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var name = g.Name;
        progress?.Report($"Buscando portada: {name}...");

        var sgdbKey = string.IsNullOrWhiteSpace(apiKey) ? (cfg.SteamGridDbApiKey ?? "") : apiKey;
        if (!string.IsNullOrWhiteSpace(sgdbKey))
        {
            try
            {
                var list = new List<CoverCandidate>();
                await SearchSgdbAsync(name, sgdbKey, list, maxGames: 1, maxPerGame: 1, ct).ConfigureAwait(false);
                if (list.Count > 0 && await SaveImageAsync(g, list[0].Url, cfg, "sgdb", ct).ConfigureAwait(false)) { Log.Info($"Portada SteamGridDB: {name}"); return true; }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"SteamGridDB {name}: {ex.Message}"); }
        }

        if (!string.IsNullOrWhiteSpace(cfg.RawgApiKey))
        {
            try
            {
                var url = await TryRawgAsync(name, cfg.RawgApiKey!, ct).ConfigureAwait(false);
                if (url != null && await SaveImageAsync(g, url, cfg, "rawg", ct).ConfigureAwait(false)) { Log.Info($"Portada RAWG: {name}"); return true; }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"RAWG {name}: {ex.Message}"); }
        }

        try
        {
            var (url, bytes) = await TryLibretroAsync(name, g.System, ct).ConfigureAwait(false);
            if (bytes != null && SaveBytes(g, bytes, cfg, "libretro")) { Log.Info($"Portada Libretro: {name} ({url})"); return true; }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Libretro {name}: {ex.Message}"); }

        if (!string.IsNullOrWhiteSpace(cfg.IgdbClientId) && !string.IsNullOrWhiteSpace(cfg.IgdbClientSecret))
        {
            try
            {
                var url = await TryIgdbAsync(name, cfg.IgdbClientId!, cfg.IgdbClientSecret!, ct).ConfigureAwait(false);
                if (url != null && await SaveImageAsync(g, url, cfg, "igdb", ct).ConfigureAwait(false)) { Log.Info($"Portada IGDB: {name}"); return true; }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"IGDB {name}: {ex.Message}"); }
        }

        if (await TryQuiverAsync(g, cfg, ct).ConfigureAwait(false)) return true;

        Log.Info($"Sin portada: {name}");
        return false;
    }

    /// <summary>Solo Quiver (sin consultar otros servicios): icono personalizado local y, si no, appIconUrl.</summary>
    public static async Task<bool> TryQuiverAsync(GameEntry g, AppConfig cfg, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(g.QuiverLocalIconPath) && string.IsNullOrWhiteSpace(g.QuiverAppIconUrl))
            {
                var lib = QuiverService.Load(cfg);
                var m = lib == null ? null : QuiverMatcher.MatchExact(lib, g.FolderPath);
                if (m != null) QuiverService.Enrich(g, m, cfg);
            }
            if (!string.IsNullOrWhiteSpace(g.QuiverLocalIconPath) && File.Exists(g.QuiverLocalIconPath))
            {
                var bytes = await File.ReadAllBytesAsync(g.QuiverLocalIconPath, ct).ConfigureAwait(false);
                if (SaveBytes(g, bytes, cfg, "quiver")) return true;
            }
            if (!string.IsNullOrWhiteSpace(g.QuiverAppIconUrl))
                return await SaveImageAsync(g, g.QuiverAppIconUrl!, cfg, "quiver", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Portada Quiver {g.Name}: {ex.Message}"); }
        return false;
    }

    public static async Task<int> DownloadAllAsync(IEnumerable<GameEntry> games, AppConfig cfg, IProgress<string>? progress, CancellationToken ct = default)
    {
        int count = 0, i = 0;
        var list = games.ToList();
        foreach (var g in list)
        {
            ct.ThrowIfCancellationRequested();
            i++;
            if (g.CoverImage != null) continue;
            progress?.Report($"Portadas ({i}/{list.Count}): {g.Name}...");
            if (await DownloadAsync(g, cfg, null, ct).ConfigureAwait(false)) count++;
            await Task.Delay(150, ct).ConfigureAwait(false); // cortesía con las APIs
        }
        return count;
    }

    // ------------------------------------------------------------------ búsqueda manual

    public static Task<bool> ApplyFromUrlAsync(GameEntry g, string imgUrl, AppConfig cfg, string sourceTag = "manual")
        => SaveImageAsync(g, imgUrl, cfg, sourceTag, CancellationToken.None);

    public static async Task<bool> ApplyFromFileAsync(GameEntry g, string path, AppConfig cfg)
    {
        try { return SaveBytes(g, await File.ReadAllBytesAsync(path).ConfigureAwait(false), cfg, "manual"); }
        catch { return false; }
    }

    public static async Task<List<CoverCandidate>> SearchAsync(string source, string query, GameEntry g, AppConfig cfg, CancellationToken ct = default)
    {
        source = (source ?? "").ToLowerInvariant();
        var list = new List<CoverCandidate>();
        try
        {
            switch (source)
            {
                case "quiver": SearchQuiver(query, g, cfg, list); break;
                case "steamgriddb":
                case "sgdb": await SearchSgdbAsync(query, cfg.SteamGridDbApiKey ?? "", list, 3, 12, ct).ConfigureAwait(false); break;
                case "rawg": await SearchRawgAsync(query, cfg, list, ct).ConfigureAwait(false); break;
                case "libretro": await SearchLibretroAsync(query, g, list, ct).ConfigureAwait(false); break;
                case "igdb": await SearchIgdbAsync(query, cfg, list, ct).ConfigureAwait(false); break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Búsqueda {source} '{query}': {ex.Message}"); }
        return list;
    }

    /// <summary>¿Está configurado este origen? (para avisar en vez de devolver "sin resultados").</summary>
    public static string? MissingCredentials(string source, AppConfig cfg) => source.ToLowerInvariant() switch
    {
        "steamgriddb" or "sgdb" when string.IsNullOrWhiteSpace(cfg.SteamGridDbApiKey) => "Falta la API key de SteamGridDB (botón 🔑SGDB).",
        "rawg" when string.IsNullOrWhiteSpace(cfg.RawgApiKey) => "Falta la API key de RAWG (botón 🔑RAWG).",
        "igdb" when string.IsNullOrWhiteSpace(cfg.IgdbClientId) || string.IsNullOrWhiteSpace(cfg.IgdbClientSecret) => "Faltan las credenciales de IGDB (botón 🔑IGDB).",
        _ => null
    };

    private static void SearchQuiver(string query, GameEntry g, AppConfig cfg, List<CoverCandidate> list)
    {
        var lib = QuiverService.Load(cfg);
        if (lib != null)
        {
            var exact = QuiverMatcher.MatchExact(lib, g.FolderPath);
            if (exact?.App != null) AddQuiverApp(exact.App, list);
            var q = PathUtil.NormName(query);
            if (q.Length >= 3)
            {
                foreach (var app in lib.Apps.Where(a => PathUtil.NormName(a.Name).Contains(q) || PathUtil.NormName(a.FolderName).Contains(q)).Take(12))
                    AddQuiverApp(app, list);
            }
        }
        if (!string.IsNullOrWhiteSpace(g.QuiverLocalIconPath) && File.Exists(g.QuiverLocalIconPath) && list.All(c => c.Url != g.QuiverLocalIconPath))
            list.Add(new CoverCandidate(g.QuiverLocalIconPath!, g.QuiverLocalIconPath, "Quiver (local)", g.Name));
        if (!string.IsNullOrWhiteSpace(g.QuiverAppIconUrl) && list.All(c => c.Url != g.QuiverAppIconUrl))
            list.Add(new CoverCandidate(g.QuiverAppIconUrl!, g.QuiverAppIconUrl, "Quiver (URL)", g.Name));
    }

    private static void AddQuiverApp(QuiverApp app, List<CoverCandidate> list)
    {
        if (app.CustomIconPath.Length > 0 && File.Exists(app.CustomIconPath) && list.All(c => c.Url != app.CustomIconPath))
            list.Add(new CoverCandidate(app.CustomIconPath, app.CustomIconPath, "Quiver (local)", app.Name));
        if (app.AppIconUrl.Length > 0 && list.All(c => c.Url != app.AppIconUrl))
            list.Add(new CoverCandidate(app.AppIconUrl, app.AppIconUrl, "Quiver (URL)", app.Name));
    }

    private static async Task SearchSgdbAsync(string query, string key, List<CoverCandidate> list, int maxGames, int maxPerGame, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        var auth = key.Trim().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? key.Trim() : "Bearer " + key.Trim();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://www.steamgriddb.com/api/v2/search/autocomplete/{Uri.EscapeDataString(query)}");
        req.Headers.TryAddWithoutValidation("Authorization", auth);
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) { Log.Warn($"SteamGridDB búsqueda HTTP {(int)resp.StatusCode}"); return; }
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return;

        // la mejor coincidencia de nombre primero (el autocompletado a veces pone otro juego delante)
        var games = data.EnumerateArray()
            .Select(it => (id: it.TryGetProperty("id", out var i) && i.TryGetInt32(out var v) ? v : 0, name: it.Str("name") ?? query))
            .Where(x => x.id != 0)
            .OrderByDescending(x => NameScore(query, x.name))
            .Take(maxGames).ToList();

        foreach (var (gameId, gname) in games)
        {
            int added = 0;
            foreach (var q in new[] { "?dimensions=600x900,660x930&types=static", "?types=static", "" })
            {
                using var req2 = new HttpRequestMessage(HttpMethod.Get, $"https://www.steamgriddb.com/api/v2/grids/game/{gameId}{q}");
                req2.Headers.TryAddWithoutValidation("Authorization", auth);
                using var resp2 = await Http.SendAsync(req2, ct).ConfigureAwait(false);
                if (!resp2.IsSuccessStatusCode) continue;
                using var doc2 = JsonDocument.Parse(await resp2.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                if (!doc2.RootElement.TryGetProperty("data", out var grids) || grids.ValueKind != JsonValueKind.Array) continue;
                foreach (var gr in grids.EnumerateArray())
                {
                    var url = gr.Str("url");
                    if (url == null || list.Any(c => c.Url == url)) continue;
                    list.Add(new CoverCandidate(url, gr.Str("thumb") ?? url, "SteamGridDB", gname));
                    if (++added >= maxPerGame) break;
                }
                if (added > 0) break;
            }
        }
    }

    private static async Task SearchRawgAsync(string query, AppConfig cfg, List<CoverCandidate> list, CancellationToken ct)
    {
        var key = cfg.RawgApiKey ?? "";
        if (string.IsNullOrWhiteSpace(key)) return;
        var json = await Http.GetStringAsync($"https://api.rawg.io/api/games?key={Uri.EscapeDataString(key)}&search={Uri.EscapeDataString(query)}&page_size=12", ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        foreach (var r in doc.RootElement.Arr("results"))
        {
            var u = r.Str("background_image");
            if (u != null) list.Add(new CoverCandidate(u, u, "RAWG", r.Str("name") ?? query));
        }
    }

    private static async Task SearchLibretroAsync(string query, GameEntry g, List<CoverCandidate> list, CancellationToken ct)
    {
        if (!LibretroSystems.TryGetValue(g.System, out var sys)) return;
        foreach (var v in LibretroVariants(query))
        {
            var url = LibretroUrl(sys, v);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, url);
                using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) list.Add(new CoverCandidate(url, url, "Libretro", v));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
            if (list.Count >= 12) break;
        }
    }

    private static async Task SearchIgdbAsync(string query, AppConfig cfg, List<CoverCandidate> list, CancellationToken ct)
    {
        var results = await IgdbQueryAsync(query, cfg.IgdbClientId ?? "", cfg.IgdbClientSecret ?? "", 12, ct).ConfigureAwait(false);
        foreach (var (name, imageId) in results)
            list.Add(new CoverCandidate($"https://images.igdb.com/igdb/image/upload/t_cover_big/{imageId}.jpg",
                $"https://images.igdb.com/igdb/image/upload/t_cover_small/{imageId}.jpg", "IGDB", name));
    }

    // ------------------------------------------------------------------ proveedores

    private static readonly Dictionary<string, string> LibretroSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SNES"] = "Nintendo - Super Nintendo Entertainment System",
        ["N64"] = "Nintendo - Nintendo 64",
        ["GBA"] = "Nintendo - Game Boy Advance",
        ["MEGADRIVE"] = "Sega - Mega Drive - Genesis",
        ["PSX"] = "Sony - PlayStation",
        ["NES"] = "Nintendo - Nintendo Entertainment System",
        ["GB"] = "Nintendo - Game Boy",
        ["GBC"] = "Nintendo - Game Boy Color",
        ["GC"] = "Nintendo - GameCube",
        ["PCE"] = "NEC - PC Engine - TurboGrafx 16",
        ["DS"] = "Nintendo - Nintendo DS",
        ["PS2"] = "Sony - PlayStation 2",
        ["PSP"] = "Sony - PlayStation Portable",
        ["3DS"] = "Nintendo - Nintendo 3DS",
        ["WII"] = "Nintendo - Wii",
        ["X360"] = "Microsoft - Xbox 360",
        ["XBOX"] = "Microsoft - Xbox",
        ["DREAMCAST"] = "Sega - Dreamcast",
        ["SATURN"] = "Sega - Saturn",
        ["ARCADE"] = "MAME",
    };

    private static string LibretroUrl(string sys, string name) =>
        $"https://thumbnails.libretro.com/{Uri.EscapeDataString(sys)}/Named_Boxarts/{Uri.EscapeDataString(name)}.png";

    private static IEnumerable<string> LibretroVariants(string name)
    {
        // Libretro sustituye &*/:`<>?\| por _
        var baseName = name;
        foreach (var c in new[] { '&', '*', '/', ':', '`', '<', '>', '?', '\\', '|', '"' }) baseName = baseName.Replace(c, '_');
        var bases = new List<string> { baseName };
        if (name.Contains(':')) bases.Add(name.Split(':')[0].Trim());
        if (name.Contains(" - ")) bases.Add(name.Split(" - ")[0].Trim());
        // "The Legend of Zelda: X" → "Legend of Zelda, The - X"
        if (name.StartsWith("The ", StringComparison.OrdinalIgnoreCase))
        {
            var rest = name[4..];
            var parts = rest.Split(':', 2);
            bases.Add(parts.Length == 2 ? $"{parts[0].Trim()}, The - {parts[1].Trim()}" : $"{rest}, The");
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in bases)
            foreach (var suffix in new[] { " (USA)", " (USA, Europe)", " (World)", " (Europe)", "", " (Japan)", " (En,Fr,De,Es,It)" })
                if (seen.Add(b + suffix)) yield return b + suffix;
    }

    private static async Task<(string? url, byte[]? bytes)> TryLibretroAsync(string name, string system, CancellationToken ct)
    {
        if (!LibretroSystems.TryGetValue(system, out var sys)) return (null, null);
        foreach (var v in LibretroVariants(name).Take(14))
        {
            var url = LibretroUrl(sys, v);
            try
            {
                using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                    if (ImageLoader.LooksLikeImage(bytes)) return (url, bytes);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        return (null, null);
    }

    private static async Task<string?> GetIgdbTokenAsync(string clientId, string clientSecret, CancellationToken ct)
    {
        await IgdbGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrEmpty(_igdbToken) && DateTime.UtcNow < _igdbTokenExpiry) return _igdbToken;
            var url = $"https://id.twitch.tv/oauth2/token?client_id={Uri.EscapeDataString(clientId)}&client_secret={Uri.EscapeDataString(clientSecret)}&grant_type=client_credentials";
            using var resp = await Http.PostAsync(url, null, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) { Log.Warn($"IGDB token HTTP {(int)resp.StatusCode}"); return null; }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            _igdbToken = doc.RootElement.Str("access_token");
            var expires = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600;
            _igdbTokenExpiry = DateTime.UtcNow.AddSeconds(Math.Max(60, expires - 120));
            return _igdbToken;
        }
        finally { IgdbGate.Release(); }
    }

    private static async Task<List<(string name, string imageId)>> IgdbQueryAsync(string query, string id, string secret, int limit, CancellationToken ct)
    {
        var result = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret)) return result;
        var token = await GetIgdbTokenAsync(id, secret, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token)) return result;
        var search = query.Replace("\"", " ").Replace("\n", " ").Replace("\r", " ");
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.igdb.com/v4/games");
        req.Headers.TryAddWithoutValidation("Client-ID", id);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        req.Content = new StringContent($"search \"{search}\"; fields name,cover.image_id; limit {limit};", System.Text.Encoding.UTF8, "text/plain");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return result;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            var cover = r.Obj("cover");
            var imageId = cover?.Str("image_id");
            if (imageId != null) result.Add((r.Str("name") ?? query, imageId));
        }
        return result;
    }

    private static async Task<string?> TryIgdbAsync(string name, string clientId, string clientSecret, CancellationToken ct)
    {
        var results = await IgdbQueryAsync(name, clientId, clientSecret, 5, ct).ConfigureAwait(false);
        if (results.Count == 0) return null;
        var best = results.OrderByDescending(r => NameScore(name, r.name)).First();
        return NameScore(name, best.name) >= 20 ? $"https://images.igdb.com/igdb/image/upload/t_cover_big/{best.imageId}.jpg" : null;
    }

    private static async Task<string?> TryRawgAsync(string name, string apiKey, CancellationToken ct)
    {
        var json = await Http.GetStringAsync($"https://api.rawg.io/api/games?key={Uri.EscapeDataString(apiKey)}&search={Uri.EscapeDataString(name)}&page_size=5", ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var best = doc.RootElement.Arr("results")
            .Select(r => (name: r.Str("name") ?? "", img: r.Str("background_image")))
            .Where(r => r.img != null)
            .OrderByDescending(r => NameScore(name, r.name)).FirstOrDefault();
        return best.img != null && NameScore(name, best.name) >= 20 ? best.img : null;
    }

    private static int NameScore(string a, string b)
    {
        var na = PathUtil.NormName(a); var nb = PathUtil.NormName(b);
        if (na.Length == 0 || nb.Length == 0) return 0;
        if (na == nb) return 100;
        if (na.Contains(nb) || nb.Contains(na)) return 70;
        var ta = a.ToLowerInvariant().Split(new[] { ' ', ':', '-', '_', '\'', '’', '.' }, StringSplitOptions.RemoveEmptyEntries);
        var tb = b.ToLowerInvariant().Split(new[] { ' ', ':', '-', '_', '\'', '’', '.' }, StringSplitOptions.RemoveEmptyEntries);
        return ta.Count(x => x.Length > 1 && tb.Contains(x)) * 10;
    }

    // ------------------------------------------------------------------ guardado

    private static async Task<bool> SaveImageAsync(GameEntry g, string imgUrl, AppConfig cfg, string source, CancellationToken ct)
    {
        try
        {
            if (File.Exists(imgUrl)) return SaveBytes(g, await File.ReadAllBytesAsync(imgUrl, ct).ConfigureAwait(false), cfg, source);
            using var resp = await Http.GetAsync(imgUrl, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return SaveBytes(g, bytes, cfg, source);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"No se pudo descargar la portada {imgUrl}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Guarda la imagen con nombre estable, borra la anterior de la caché y la aplica.</summary>
    private static bool SaveBytes(GameEntry g, byte[] bytes, AppConfig cfg, string source)
    {
        if (bytes.Length < 300 || !ImageLoader.LooksLikeImage(bytes)) return false;
        var img = ImageLoader.Load(bytes);
        if (img == null) return false;
        try
        {
            Directory.CreateDirectory(CacheDir);
            var stem = $"{PathUtil.SafeFileName(Path.GetFileName(g.FolderPath), 40)}_{PathUtil.StableHash(g.FolderPath)}";
            var dest = Path.Combine(CacheDir, $"{stem}_{source}{ImageLoader.ExtensionFor(bytes)}");
            File.WriteAllBytes(dest, bytes);

            string? previous = null;
            ConfigService.Update(cfg, c =>
            {
                previous = c.CoverOverrides.TryGetValue(g.FolderPath, out var p) ? p : null;
                c.CoverOverrides[g.FolderPath] = dest;
            });
            // solo se borran imágenes de nuestra caché, nunca ficheros del usuario
            if (previous != null && !string.Equals(previous, dest, StringComparison.OrdinalIgnoreCase) &&
                PathUtil.IsSameOrInside(previous, CacheDir))
            {
                try { File.Delete(previous); } catch { }
            }
            UiThread.RunSync(() => g.CoverImage = img);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo guardar la portada de {g.Name}: {ex.Message}");
            return false;
        }
    }

    public static void ClearAllCachedCovers(AppConfig cfg)
    {
        try { if (Directory.Exists(CacheDir)) Directory.Delete(CacheDir, true); } catch { }
        ConfigService.Update(cfg, c =>
        {
            foreach (var k in c.CoverOverrides.Where(kv => PathUtil.IsSameOrInside(kv.Value, CacheDir)).Select(kv => kv.Key).ToList())
                c.CoverOverrides.Remove(k);
        });
    }
}
