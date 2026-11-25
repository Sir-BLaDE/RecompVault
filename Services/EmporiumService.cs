using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using RecompVault.Core;

namespace RecompVault.Services;

/// <summary>
/// Catálogo de The Gaming Emporium guardado en %APPDATA%\RecompVault\emporium_catalog.json.
/// El escaneo solo lee la copia local; la descarga va en segundo plano una vez al día (con ETag).
/// </summary>
public static class EmporiumService
{
    public static readonly string FilePath = Path.Combine(ConfigService.Dir, "emporium_catalog.json");
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly object Lock = new();
    private static CacheFile? _cache;

    public sealed class CacheFile
    {
        public DateTime FetchedUtc { get; set; }
        public Dictionary<string, string> ETags { get; set; } = new();
        public List<EmporiumEntry> Entries { get; set; } = new();
    }

    /// <summary>Proyectos guardados (vacío si nunca se ha descargado o está desactivado).</summary>
    public static IReadOnlyList<EmporiumEntry> Entries(AppConfig cfg)
    {
        if (!cfg.UseEmporium) return Array.Empty<EmporiumEntry>();
        lock (Lock)
        {
            if (_cache == null)
            {
                try
                {
                    _cache = File.Exists(FilePath) ? JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(FilePath), Json) : null;
                }
                catch (Exception ex) { Log.Warn("Catálogo de Gaming Emporium ilegible: " + ex.Message); }
                _cache ??= new CacheFile();
            }
            return _cache.Entries;
        }
    }

    public static DateTime? LastFetchUtc { get { lock (Lock) return _cache?.FetchedUtc is { } d && d > DateTime.MinValue ? d : null; } }

    public static bool IsStale(AppConfig cfg)
    {
        Entries(cfg);
        lock (Lock) return _cache == null || _cache.Entries.Count == 0 || DateTime.UtcNow - _cache.FetchedUtc > MaxAge;
    }

    /// <summary>
    /// Descarga las categorías con proyectos para PC. Devuelve true si el catálogo cambió (para volver a emparejar).
    /// </summary>
    public static async Task<(bool changed, string? error)> RefreshAsync(AppConfig cfg, bool force, CancellationToken ct = default)
    {
        if (!cfg.UseEmporium) return (false, null);
        if (!force && !IsStale(cfg)) return (false, null);
        CacheFile current;
        lock (Lock) current = _cache ?? new CacheFile();

        using var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RecompVault/" + SelfUpdateService.CurrentVersion + " (+https://github.com/Sir-BLaDE/RecompVault)");

        var entries = new List<EmporiumEntry>();
        var etags = new Dictionary<string, string>();
        bool anyChanged = false;
        foreach (var cat in EmporiumCatalog.Categories)
        {
            var url = $"{EmporiumCatalog.BaseUrl}/categories/{cat}/";
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (!force && current.Entries.Count > 0 && current.ETags.TryGetValue(cat, out var et) && et.Length > 0)
                    req.Headers.TryAddWithoutValidation("If-None-Match", et);
                using var resp = await http.SendAsync(req, ct);
                if (resp.StatusCode == HttpStatusCode.NotModified)
                {
                    entries.AddRange(current.Entries.Where(e => e.Category == cat));
                    etags[cat] = current.ETags[cat];
                    continue;
                }
                if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");
                var html = await resp.Content.ReadAsStringAsync(ct);
                var parsed = EmporiumCatalog.ParseCategoryPage(html).Where(e => e.Category == cat || e.Category.Length == 0).ToList();
                if (parsed.Count == 0) throw new InvalidDataException("la página no tiene proyectos (¿ha cambiado la web?)");
                entries.AddRange(parsed);
                if (resp.Headers.ETag != null) etags[cat] = resp.Headers.ETag.Tag;
                anyChanged = true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warn($"Gaming Emporium ({cat}): {ex.Message}");
                // si falla una categoría se conserva lo que ya había de ella
                entries.AddRange(current.Entries.Where(e => e.Category == cat));
                if (current.ETags.TryGetValue(cat, out var old)) etags[cat] = old;
                if (current.Entries.Count == 0) return (false, ex.Message);
            }
        }

        var dedup = entries.GroupBy(e => (e.Repository.ToLowerInvariant(), e.Title)).Select(g => g.First()).ToList();
        var next = new CacheFile { FetchedUtc = DateTime.UtcNow, ETags = etags, Entries = dedup };
        lock (Lock) _cache = next;
        try
        {
            Directory.CreateDirectory(ConfigService.Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(next, Json));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null, true); else File.Move(tmp, FilePath);
        }
        catch (Exception ex) { Log.Warn("No se pudo guardar el catálogo de Gaming Emporium: " + ex.Message); }
        Log.Info($"Gaming Emporium: {dedup.Count} proyectos con repositorio" + (anyChanged ? "" : " (sin cambios)"));
        return (anyChanged, null);
    }
}
