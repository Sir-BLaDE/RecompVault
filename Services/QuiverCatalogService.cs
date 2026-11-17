using System.IO;
using System.Net.Http;
using System.Text.Json;
using RecompVault.Core;

namespace RecompVault.Services;

public sealed class CatalogApp
{
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string ProjectType { get; set; } = "";
    public List<string> Consoles { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public List<string> SupportedOS { get; set; } = new();
    public List<string> Games { get; set; } = new();
    public string? Artwork { get; set; }
    public string? Capsule { get; set; }
    public string? Header { get; set; }
    public string? Hero { get; set; }
    public string? Logo { get; set; }
    public string FolderName { get; set; } = "";
    public string? AssetFilter { get; set; }
    public string? Developer { get; set; }
    public int Recommended { get; set; }
    public DateTime? AddedAt { get; set; }
    public DateTime? LastReleaseAt { get; set; }
    public string? LastReleaseVersion { get; set; }
    public string? VerifiedVersion { get; set; }
    public string? AiLevel { get; set; }

    public bool RunsOnWindows => SupportedOS.Count == 0 || SupportedOS.Any(o => o.Contains("win", StringComparison.OrdinalIgnoreCase));
    public string BestArt => Capsule ?? Artwork ?? Header ?? Hero ?? Logo ?? "";
}

public sealed record CatalogPage(List<CatalogApp> Items, string? NextCursor);
public sealed record CatalogProject(string Provider, string Repository, string? Author);

/// <summary>
/// Cliente del catálogo público de Quiver Launcher (api.quiverlauncher.com/api/v1, el mismo que usa la
/// pestaña "Explorar" de Quiver). Si el dominio principal no responde, usa el de respaldo.
/// </summary>
public static class QuiverCatalogService
{
    private const string Primary = "https://api.quiverlauncher.com/api/v1";
    private const string Fallback = "https://famous-wildebeest-660.convex.site/api/v1";
    public const string Website = "https://quiverlauncher.com";
    private static bool _useFallback;
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("RecompVault/" + SelfUpdateService.CurrentVersion);
        return h;
    }

    public static string AppPage(string slug) => $"{Website}/apps/{Uri.EscapeDataString(slug)}";

    private static async Task<JsonDocument?> GetAsync(string path, CancellationToken ct)
    {
        foreach (var useFallback in _useFallback ? new[] { true } : new[] { false, true })
        {
            try
            {
                using var resp = await Http.GetAsync((useFallback ? Fallback : Primary) + path, ct).ConfigureAwait(false);
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
                if (!resp.IsSuccessStatusCode)
                {
                    if (!useFallback && (int)resp.StatusCode >= 499) continue;
                    throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");
                }
                if (useFallback) _useFallback = true;
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return JsonDocument.Parse(json);
            }
            catch (HttpRequestException) when (!useFallback) { }
            catch (TaskCanceledException) when (!useFallback && !ct.IsCancellationRequested) { }
        }
        throw new HttpRequestException("No se pudo contactar con el catálogo de Quiver.");
    }

    /// <param name="sort">"updated", "added", "rating" o "name".</param>
    public static async Task<CatalogPage> GetAppsAsync(string sort, string? search, string? cursor, CancellationToken ct = default)
    {
        var p = new List<string> { "limit=100" };
        if (!string.IsNullOrWhiteSpace(search)) p.Add("search=" + Uri.EscapeDataString(search.Trim()));
        else p.Add("sort=" + Uri.EscapeDataString(sort));
        if (!string.IsNullOrWhiteSpace(cursor)) p.Add("cursor=" + Uri.EscapeDataString(cursor));
        using var doc = await GetAsync("/apps?" + string.Join("&", p), ct).ConfigureAwait(false);
        var items = new List<CatalogApp>();
        string? next = null;
        if (doc != null)
        {
            var root = doc.RootElement;
            foreach (var a in root.Arr("items")) items.Add(Parse(a));
            var done = root.TryGet("isDone", out var d) && d.ValueKind == JsonValueKind.True;
            next = done ? null : root.Str("nextCursor");
        }
        return new CatalogPage(items, next);
    }

    public static async Task<CatalogProject?> GetProjectAsync(string slug, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"/apps/{Uri.EscapeDataString(slug)}", ct).ConfigureAwait(false);
        var project = doc?.RootElement.Obj("project");
        var repo = project?.Str("repository");
        if (repo == null) return null;
        if (repo.StartsWith("http", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(repo, UriKind.Absolute, out var u)) repo = u.AbsolutePath.Trim('/');
        return new CatalogProject((project?.Str("provider") ?? "github").ToLowerInvariant(), repo, project?.Str("author"));
    }

    private static DateTime? Ms(JsonElement e, string name)
    {
        if (!e.TryGet(name, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var ms) || ms <= 0) return null;
        try { return DateTimeOffset.FromUnixTimeMilliseconds((long)ms).LocalDateTime; } catch { return null; }
    }

    private static CatalogApp Parse(JsonElement a)
    {
        var art = a.Obj("libraryArt");
        var launcher = a.Obj("launcher");
        return new CatalogApp
        {
            Slug = a.Str("slug") ?? "",
            Name = a.Str("name") ?? a.Str("slug") ?? "?",
            Description = a.Str("description") ?? "",
            ProjectName = a.Str("projectName") ?? "",
            ProjectType = a.Str("projectType") ?? "",
            Consoles = a.Strings("consoles"),
            Tags = a.Strings("tags"),
            SupportedOS = a.Strings("supportedOS"),
            Games = a.Arr("games").Select(gm => gm.Str("title") ?? "").Where(t => t.Length > 0).ToList(),
            Artwork = a.Str("artwork"),
            Capsule = art?.Str("capsule"), Header = art?.Str("header"), Hero = art?.Str("hero"), Logo = art?.Str("logo"),
            FolderName = launcher?.Str("folderName") ?? a.Str("slug") ?? "",
            AssetFilter = launcher?.Str("releaseAssetFilter"),
            Developer = a.Obj("developer")?.Str("name"),
            Recommended = a.TryGet("recommended", out var r) && r.TryGetInt32(out var ri) ? ri : 0,
            AddedAt = Ms(a, "addedAt"),
            LastReleaseAt = Ms(a, "lastReleaseAt"),
            LastReleaseVersion = a.Str("lastReleaseVersion"),
            VerifiedVersion = a.Obj("verified")?.Str("version"),
            AiLevel = a.Str("aiLevel"),
        };
    }
}
