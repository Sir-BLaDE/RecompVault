using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using RecompVault.Models;

namespace RecompVault.Services;

public record CoverCandidate(string Url, string? ThumbUrl, string Source, string Title);

public static class CoverDownloadService
{
    private static readonly HttpClient Http = CreateHttp();
    private static readonly string CacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RecompVault", "covers");
    private static string? _igdbToken;
    private static DateTime _igdbTokenExpiry = DateTime.MinValue;

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        try { c.DefaultRequestHeaders.UserAgent.ParseAdd("RecompVault/1.0 (cover fetcher)"); } catch { }
        return c;
    }

    // Compat overloads
    public static Task<bool> DownloadAsync(GameEntry g, AppConfig cfg, IProgress<string>? progress = null)
        => DownloadAsync(g, cfg.SteamGridDbApiKey ?? "", cfg, progress);

    /// <summary>Descarga portada: SteamGridDB primero, luego RAWG/Libretro/IGDB y Quiver como último recurso.</summary>
    public static async Task<bool> DownloadAsync(GameEntry g, string apiKey, AppConfig cfg, IProgress<string>? progress = null)
    {
        progress?.Report($"Buscando portada: {g.Name}...");

        // 1) SteamGridDB (si hay API key) - prioridad máxima, grids verticales
        var sgdbKey = string.IsNullOrWhiteSpace(apiKey) ? (cfg.SteamGridDbApiKey ?? "") : apiKey;
        if (!string.IsNullOrWhiteSpace(sgdbKey))
        {
            try
            {
                var searchUrl = $"https://www.steamgriddb.com/api/v2/search/autocomplete/{Uri.EscapeDataString(g.Name)}";
                using var req = new HttpRequestMessage(HttpMethod.Get, searchUrl);
                var auth = sgdbKey.Trim().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? sgdbKey.Trim() : "Bearer " + sgdbKey.Trim();
                req.Headers.TryAddWithoutValidation("Authorization", auth);
                var resp = await Http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
                    {
                        var gameId = data[0].GetProperty("id").GetInt32();
                        string? imgUrl = null;
                        foreach (var q in new[] { "?dimensions=600x900", "?dimensions=660x930", "" })
                        {
                            var gridsUrl = $"https://www.steamgriddb.com/api/v2/grids/game/{gameId}{q}";
                            using var req2 = new HttpRequestMessage(HttpMethod.Get, gridsUrl);
                            req2.Headers.TryAddWithoutValidation("Authorization", auth);
                            var resp2 = await Http.SendAsync(req2);
                            if (!resp2.IsSuccessStatusCode) continue;
                            var json2 = await resp2.Content.ReadAsStringAsync();
                            using var doc2 = JsonDocument.Parse(json2);
                            if (doc2.RootElement.TryGetProperty("data", out var grids) && grids.GetArrayLength() > 0)
                            {
                                imgUrl = grids[0].TryGetProperty("url", out var u) ? u.GetString() : null;
                                if (!string.IsNullOrEmpty(imgUrl)) break;
                            }
                        }
                        if (!string.IsNullOrEmpty(imgUrl))
                        {
                            var ok = await SaveImageAsync(g, imgUrl, cfg, $"sgdb_{gameId}");
                            if (ok) { Log($"OK SteamGridDB: {g.Name}"); return true; }
                        }
                    }
                }
            }
            catch (Exception ex) { Log($"SteamGridDB error {g.Name}: {ex.Message}"); }
        }

        // 2) RAWG (si hay API key)
        if (!string.IsNullOrWhiteSpace(cfg.RawgApiKey))
        {
            try
            {
                var rawg = await TryRawgAsync(g, cfg.RawgApiKey!);
                if (!string.IsNullOrEmpty(rawg))
                {
                    var ok = await SaveImageAsync(g, rawg, cfg, $"rawg_{Math.Abs(g.Name.GetHashCode())}");
                    if (ok) { Log($"OK RAWG: {g.Name}"); return true; }
                }
            }
            catch (Exception ex) { Log($"RAWG error {g.Name}: {ex.Message}"); }
        }

        // 3) Libretro Thumbnails (sin API key, juegos retro)
        try
        {
            var lr = await TryLibretroAsync(g);
            if (!string.IsNullOrEmpty(lr))
            {
                var ok = await SaveImageAsync(g, lr, cfg, $"libretro_{Math.Abs(g.Name.GetHashCode())}");
                if (ok) { Log($"OK Libretro: {g.Name}"); return true; }
            }
        }
        catch (Exception ex) { Log($"Libretro error {g.Name}: {ex.Message}"); }

        // 4) IGDB (si hay credenciales Twitch)
        if (!string.IsNullOrWhiteSpace(cfg.IgdbClientId) && !string.IsNullOrWhiteSpace(cfg.IgdbClientSecret))
        {
            try
            {
                var ig = await TryIgdbAsync(g, cfg.IgdbClientId!, cfg.IgdbClientSecret!);
                if (!string.IsNullOrEmpty(ig))
                {
                    var ok = await SaveImageAsync(g, ig, cfg, $"igdb_{Math.Abs(g.Name.GetHashCode())}");
                    if (ok) { Log($"OK IGDB: {g.Name}"); return true; }
                }
            }
            catch (Exception ex) { Log($"IGDB error {g.Name}: {ex.Message}"); }
        }

        // 5) Quiver (último recurso): icono local en caché de Quiver o appIconUrl
        try
        {
            if ((string.IsNullOrWhiteSpace(g.QuiverLocalIconPath) && string.IsNullOrWhiteSpace(g.QuiverAppIconUrl)) && cfg.QuiverSyncEnabled)
            {
                var qPath = QuiverIntegrationService.ResolveQuiverPath(cfg);
                if (!string.IsNullOrEmpty(qPath))
                {
                    try
                    {
                        var apps = QuiverIntegrationService.LoadApps(qPath);
                        var info = QuiverIntegrationService.Match(g, apps);
                        if (info != null) QuiverIntegrationService.Enrich(g, info, qPath);
                    }
                    catch { }
                }
            }
            if (!string.IsNullOrWhiteSpace(g.QuiverLocalIconPath) && File.Exists(g.QuiverLocalIconPath))
            {
                var okLocal = await SaveLocalImageAsync(g, g.QuiverLocalIconPath, cfg, $"quiver_local_{SafeTag(g.FolderPath)}");
                if (okLocal) { Log($"OK Quiver local: {g.Name}"); return true; }
            }
            if (!string.IsNullOrWhiteSpace(g.QuiverAppIconUrl))
            {
                var okUrl = await SaveImageAsync(g, g.QuiverAppIconUrl!, cfg, $"quiver_{SafeTag(g.QuiverFolderName ?? g.Name)}");
                if (okUrl) { Log($"OK Quiver URL: {g.Name}"); return true; }
            }
        }
        catch (Exception ex) { Log($"Quiver cover error {g.Name}: {ex.Message}"); }

        Log($"FAIL: {g.Name}");
        return false;
    }

    public static Task<int> DownloadAllAsync(IEnumerable<GameEntry> games, AppConfig cfg, IProgress<string>? progress)
        => DownloadAllAsync(games, cfg.SteamGridDbApiKey ?? "", cfg, progress);

    public static async Task<int> DownloadAllAsync(IEnumerable<GameEntry> games, string apiKey, AppConfig cfg, IProgress<string>? progress)
    {
        int count = 0;
        var list = games.ToList();
        foreach (var g in list)
        {
            if (cfg.CoverOverrides.ContainsKey(g.FolderPath) && g.CoverImage != null) continue;
            progress?.Report($"Descargando portada: {g.Name}...");
            var ok = await DownloadAsync(g, apiKey, cfg, progress);
            if (ok) count++;
            await Task.Delay(180);
        }
        return count;
    }

    public static void ClearAllCachedCovers(AppConfig cfg)
    {
        try { if (Directory.Exists(CacheDir)) Directory.Delete(CacheDir, true); } catch { }
        try { Directory.CreateDirectory(CacheDir); } catch { }
        cfg.CoverOverrides.Clear();
        ConfigService.Save(cfg);
    }

    // --- Búsqueda online para selección manual ---
    public static async Task<bool> ApplyFromUrlAsync(GameEntry g, string imgUrl, AppConfig cfg, string sourceTag = "manual")
    {
        if (string.IsNullOrWhiteSpace(imgUrl)) return false;
        return await SaveImageAsync(g, imgUrl, cfg, $"{sourceTag}_{SafeTag(g.Name)}_{DateTime.Now.Ticks}");
    }
    public static async Task<List<CoverCandidate>> SearchAsync(string source, string query, GameEntry g, AppConfig cfg)
    {
        source = (source ?? "").ToLowerInvariant();
        var list = new List<CoverCandidate>();
        try
        {
            switch (source)
            {
                case "quiver": await SearchQuiverAsync(query, g, cfg, list); break;
                case "steamgriddb":
                case "sgdb": await SearchSgdbAsync(query, cfg, list); break;
                case "rawg": await SearchRawgAsync(query, cfg, list); break;
                case "libretro": await SearchLibretroAsync(query, g, cfg, list); break;
                case "igdb": await SearchIgdbAsync(query, cfg, list); break;
            }
        }
        catch (Exception ex) { Log($"Search error {source} '{query}': {ex.Message}"); }
        return list;
    }

    private static async Task SearchQuiverAsync(string query, GameEntry g, AppConfig cfg, List<CoverCandidate> list)
    {
        var qPath = QuiverIntegrationService.ResolveQuiverPath(cfg);
        if (!string.IsNullOrEmpty(qPath))
        {
            try
            {
                var apps = QuiverIntegrationService.LoadApps(qPath);
                var info = QuiverIntegrationService.Match(g, apps);
                // also try loose name match in apps by query
                if (info == null)
                {
                    var q = (query ?? g.Name).ToLowerInvariant();
                    var kv = apps.FirstOrDefault(a => (a.Value.Name ?? "").ToLowerInvariant().Contains(q) || q.Contains((a.Value.Name ?? "").ToLowerInvariant()));
                    info = kv.Value;
                    if (info != null && string.IsNullOrWhiteSpace(info.Name) && string.IsNullOrWhiteSpace(info.FolderName)) info = null;
                }
                if (info != null)
                {
                    QuiverIntegrationService.Enrich(g, info, qPath);
                    if (!string.IsNullOrWhiteSpace(g.QuiverLocalIconPath) && File.Exists(g.QuiverLocalIconPath))
                        list.Add(new CoverCandidate(g.QuiverLocalIconPath, g.QuiverLocalIconPath, "Quiver (local)", info.Name ?? g.Name));
                    if (!string.IsNullOrWhiteSpace(g.QuiverAppIconUrl))
                        list.Add(new CoverCandidate(g.QuiverAppIconUrl!, g.QuiverAppIconUrl!, "Quiver (URL)", info.Name ?? g.Name));
                }
            }
            catch { }
        }
        // also consider current game's known quiver fields
        if (!string.IsNullOrWhiteSpace(g.QuiverLocalIconPath) && File.Exists(g.QuiverLocalIconPath) && !list.Any(c => c.Url == g.QuiverLocalIconPath))
            list.Add(new CoverCandidate(g.QuiverLocalIconPath, g.QuiverLocalIconPath, "Quiver (local)", g.Name));
        if (!string.IsNullOrWhiteSpace(g.QuiverAppIconUrl) && !list.Any(c => c.Url == g.QuiverAppIconUrl))
            list.Add(new CoverCandidate(g.QuiverAppIconUrl!, g.QuiverAppIconUrl!, "Quiver (URL)", g.Name));
        await Task.CompletedTask;
    }

    private static async Task SearchSgdbAsync(string query, AppConfig cfg, List<CoverCandidate> list)
    {
        var key = cfg.SteamGridDbApiKey ?? "";
        if (string.IsNullOrWhiteSpace(key)) return;
        var auth = key.Trim().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? key.Trim() : "Bearer " + key.Trim();
        var searchUrl = $"https://www.steamgriddb.com/api/v2/search/autocomplete/{Uri.EscapeDataString(query)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, searchUrl);
        req.Headers.TryAddWithoutValidation("Authorization", auth);
        var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return;
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0) return;
        int gamesChecked = 0;
        foreach (var item in data.EnumerateArray())
        {
            if (gamesChecked++ >= 3) break;
            var gameId = item.GetProperty("id").GetInt32();
            var gname = item.TryGetProperty("name", out var gn) ? (gn.GetString() ?? query) : query;
            foreach (var q in new[] { "?dimensions=600x900&limit=12", "?dimensions=660x930&limit=12", "?limit=12" })
            {
                var gridsUrl = $"https://www.steamgriddb.com/api/v2/grids/game/{gameId}{q}";
                using var req2 = new HttpRequestMessage(HttpMethod.Get, gridsUrl);
                req2.Headers.TryAddWithoutValidation("Authorization", auth);
                var resp2 = await Http.SendAsync(req2);
                if (!resp2.IsSuccessStatusCode) continue;
                var json2 = await resp2.Content.ReadAsStringAsync();
                using var doc2 = JsonDocument.Parse(json2);
                if (doc2.RootElement.TryGetProperty("data", out var grids))
                {
                    foreach (var gr in grids.EnumerateArray())
                    {
                        var url = gr.TryGetProperty("url", out var u) ? u.GetString() : null;
                        var thumb = gr.TryGetProperty("thumb", out var t) ? t.GetString() : url;
                        if (!string.IsNullOrEmpty(url)) list.Add(new CoverCandidate(url, thumb, "SteamGridDB", gname));
                        if (list.Count(c => c.Source == "SteamGridDB") >= 24) return;
                    }
                }
                if (list.Any(c => c.Source == "SteamGridDB")) break;
            }
        }
    }

    private static async Task SearchRawgAsync(string query, AppConfig cfg, List<CoverCandidate> list)
    {
        var key = cfg.RawgApiKey ?? "";
        if (string.IsNullOrWhiteSpace(key)) return;
        var url = $"https://api.rawg.io/api/games?key={Uri.EscapeDataString(key)}&search={Uri.EscapeDataString(query)}&page_size=12";
        var json = await Http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results)) return;
        foreach (var r in results.EnumerateArray())
        {
            var name = r.TryGetProperty("name", out var n) ? (n.GetString() ?? query) : query;
            if (r.TryGetProperty("background_image", out var img) && img.ValueKind == JsonValueKind.String)
            {
                var u = img.GetString();
                if (!string.IsNullOrEmpty(u)) list.Add(new CoverCandidate(u, u, "RAWG", name));
            }
        }
    }

    private static async Task SearchLibretroAsync(string query, GameEntry g, AppConfig cfg, List<CoverCandidate> list)
    {
        if (!LibretroSystems.TryGetValue(g.System, out var sys)) return;
        var invalid = new[] { '&', '*', '/', ':', '<', '>', '?', '\\', '|', '"' };
        var baseName = query;
        foreach (var c in invalid) baseName = baseName.Replace(c, '_');
        var variants = new List<string> { baseName, baseName + " (USA)", baseName + " (World)", baseName + " (Europe)", baseName + " (Japan)", baseName + " (USA, Europe)" };
        if (baseName.Contains(":")) { var shortName = baseName.Split(':')[0].Trim(); variants.Add(shortName); variants.Add(shortName + " (USA)"); }
        if (baseName.Contains(" - ")) { var shortName = baseName.Split(new[] { " - " }, StringSplitOptions.None)[0].Trim(); variants.Add(shortName); variants.Add(shortName + " (USA)"); }
        foreach (var v in variants.Distinct().Take(12))
        {
            var url = $"https://thumbnails.libretro.com/{Uri.EscapeDataString(sys)}/Named_Boxarts/{Uri.EscapeDataString(v)}.png";
            try
            {
                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (resp.IsSuccessStatusCode)
                    list.Add(new CoverCandidate(url, url, "Libretro", v));
            }
            catch { }
            await Task.Delay(60);
            if (list.Count >= 12) break;
        }
    }

    private static async Task SearchIgdbAsync(string query, AppConfig cfg, List<CoverCandidate> list)
    {
        var id = cfg.IgdbClientId ?? ""; var secret = cfg.IgdbClientSecret ?? "";
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret)) return;
        var token = await GetIgdbTokenAsync(id, secret);
        if (string.IsNullOrEmpty(token)) return;
        var search = query.Replace("\"", " ").Replace("\n", " ").Replace("\r", " ");
        var body = $"search \"{search}\"; fields name,cover.image_id; limit 12;";
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.igdb.com/v4/games");
        req.Headers.TryAddWithoutValidation("Client-ID", id);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        req.Content = new StringContent(body, System.Text.Encoding.UTF8, "text/plain");
        var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return;
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            var name = r.TryGetProperty("name", out var n) ? (n.GetString() ?? query) : query;
            if (r.TryGetProperty("cover", out var cover) && cover.ValueKind == JsonValueKind.Object &&
                cover.TryGetProperty("image_id", out var iid) && iid.ValueKind == JsonValueKind.String)
            {
                var iidv = iid.GetString();
                if (!string.IsNullOrEmpty(iidv))
                {
                    var url = $"https://images.igdb.com/igdb/image/upload/t_cover_big/{iidv}.jpg";
                    var thumb = $"https://images.igdb.com/igdb/image/upload/t_cover_small/{iidv}.jpg";
                    list.Add(new CoverCandidate(url, thumb, "IGDB", name));
                }
            }
        }
    }

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
        ["ARCADE"] = "MAME",
    };

    private static async Task<string?> GetIgdbTokenAsync(string clientId, string clientSecret)
    {
        if (!string.IsNullOrEmpty(_igdbToken) && DateTime.UtcNow < _igdbTokenExpiry) return _igdbToken;
        var url = $"https://id.twitch.tv/oauth2/token?client_id={Uri.EscapeDataString(clientId)}&client_secret={Uri.EscapeDataString(clientSecret)}&grant_type=client_credentials";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("access_token", out var tok)) return null;
        _igdbToken = tok.GetString();
        var expires = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
        _igdbTokenExpiry = DateTime.UtcNow.AddSeconds(Math.Max(60, expires - 120));
        return _igdbToken;
    }

    private static async Task<string?> TryIgdbAsync(GameEntry g, string clientId, string clientSecret)
    {
        var token = await GetIgdbTokenAsync(clientId, clientSecret);
        if (string.IsNullOrEmpty(token)) return null;
        var search = g.Name.Replace("\"", " ").Replace("\n", " ").Replace("\r", " ");
        var body = $"search \"{search}\"; fields name,cover.image_id; limit 5;";
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.igdb.com/v4/games");
        req.Headers.TryAddWithoutValidation("Client-ID", clientId);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        req.Content = new StringContent(body, System.Text.Encoding.UTF8, "text/plain");
        var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) return null;
        JsonElement? best = null; int bestScore = -1;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            var name = r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            int score = NameScore(g.Name, name);
            if (score > bestScore) { bestScore = score; best = r; }
        }
        var pick = best ?? doc.RootElement[0];
        if (pick.TryGetProperty("cover", out var cover) && cover.ValueKind == JsonValueKind.Object &&
            cover.TryGetProperty("image_id", out var iid) && iid.ValueKind == JsonValueKind.String)
        {
            var id = iid.GetString();
            if (!string.IsNullOrEmpty(id)) return $"https://images.igdb.com/igdb/image/upload/t_cover_big/{id}.jpg";
        }
        return null;
    }

    private static async Task<string?> TryRawgAsync(GameEntry g, string apiKey)
    {
        try
        {
            var url = $"https://api.rawg.io/api/games?key={Uri.EscapeDataString(apiKey)}&search={Uri.EscapeDataString(g.Name)}&page_size=5";
            var json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
            JsonElement? best = null; int bestScore = -1;
            foreach (var r in results.EnumerateArray())
            {
                var name = r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                int score = NameScore(g.Name, name);
                if (score > bestScore) { bestScore = score; best = r; }
            }
            var pick = best ?? results[0];
            if (pick.TryGetProperty("background_image", out var img) && img.ValueKind == JsonValueKind.String)
                return img.GetString();
        }
        catch { }
        return null;
    }

    private static int NameScore(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return 100;
        var aa = a.ToLowerInvariant(); var bb = b.ToLowerInvariant();
        if (aa.Contains(bb) || bb.Contains(aa)) return 70;
        var ta = aa.Split(new[] { ' ', ':', '-', '_', '\'', '’' }, StringSplitOptions.RemoveEmptyEntries);
        var tb = bb.Split(new[] { ' ', ':', '-', '_', '\'', '’' }, StringSplitOptions.RemoveEmptyEntries);
        return ta.Count(x => tb.Contains(x)) * 10;
    }

    private static async Task<string?> TryLibretroAsync(GameEntry g)
    {
        if (!LibretroSystems.TryGetValue(g.System, out var sys)) return null;
        var invalid = new[] { '&', '*', '/', ':', '<', '>', '?', '\\', '|', '"' };
        var baseName = g.Name;
        foreach (var c in invalid) baseName = baseName.Replace(c, '_');
        var variants = new List<string> { baseName, baseName + " (USA)", baseName + " (World)", baseName + " (Europe)", baseName + " (Japan)", baseName + " (USA, Europe)", baseName + " (En,Fr,De,Es,It)" };
        if (baseName.Contains(":")) { var shortName = baseName.Split(':')[0].Trim(); variants.Add(shortName); variants.Add(shortName + " (USA)"); }
        if (baseName.Contains(" - ")) { var shortName = baseName.Split(new[] { " - " }, StringSplitOptions.None)[0].Trim(); variants.Add(shortName); variants.Add(shortName + " (USA)"); }
        foreach (var v in variants)
        {
            var url = $"https://thumbnails.libretro.com/{Uri.EscapeDataString(sys)}/Named_Boxarts/{Uri.EscapeDataString(v)}.png";
            try { using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead); if (resp.IsSuccessStatusCode) return url; } catch { }
            await Task.Delay(80);
        }
        return null;
    }

    private static async Task<bool> SaveImageAsync(GameEntry g, string imgUrl, AppConfig cfg, string fileTag)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var ext = GetExtFromUrl(imgUrl);
            var dest = Path.Combine(CacheDir, $"{fileTag}{ext}");
            using var imgResp = await Http.GetAsync(imgUrl);
            if (!imgResp.IsSuccessStatusCode) return false;
            var bytes = await imgResp.Content.ReadAsByteArrayAsync();
            if (bytes.Length < 300) return false;
            await File.WriteAllBytesAsync(dest, bytes);
            cfg.CoverOverrides[g.FolderPath] = dest;
            ConfigService.Save(cfg);
            ApplyCoverToGame(g, dest);
            return true;
        }
        catch { return false; }
    }

    private static async Task<bool> SaveLocalImageAsync(GameEntry g, string sourcePath, AppConfig cfg, string fileTag)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var ext = Path.GetExtension(sourcePath);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".png";
            var dest = Path.Combine(CacheDir, $"{fileTag}{ext}");
            var bytes = await File.ReadAllBytesAsync(sourcePath);
            if (bytes.Length < 100) return false;
            await File.WriteAllBytesAsync(dest, bytes);
            cfg.CoverOverrides[g.FolderPath] = dest;
            ConfigService.Save(cfg);
            ApplyCoverToGame(g, dest);
            return true;
        }
        catch { return false; }
    }

    private static void ApplyCoverToGame(GameEntry g, string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess()) dispatcher.Invoke(() => g.CoverImage = bmp);
            else g.CoverImage = bmp;
        }
        catch { }
    }

    private static string GetExtFromUrl(string url)
    {
        try
        {
            var u = url.Split('?', '#')[0];
            var ext = Path.GetExtension(u).ToLowerInvariant();
            if (ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp" or ".ico") return ext;
        }
        catch { }
        return ".png";
    }

    private static string SafeTag(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "x";
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        s = s.Replace('\\', '_').Replace('/', '_');
        if (s.Length > 80) s = s.Substring(0, 80);
        return s;
    }

    private static void Log(string msg)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "recompvault_covers.log"), DateTime.Now + " " + msg + "\n"); } catch { }
    }
}