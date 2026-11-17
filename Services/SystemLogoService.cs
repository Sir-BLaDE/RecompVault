using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RecompVault.Models;

namespace RecompVault.Services;

public static class SystemLogoService
{
    private static readonly HttpClient Http = Create();
    private static readonly string CacheDir = Path.Combine(ConfigService.Dir, "system_logos");
    /// <summary>Sistemas ya intentados en esta sesión (evita repetir descargas fallidas en cada filtro).</summary>
    private static readonly HashSet<string> Attempted = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ImageSource> Loaded = new(StringComparer.OrdinalIgnoreCase);

    // Curated: prefer official wordmark/logos (con letras). Using Commons redirect for reliable thumbnails.
    private static readonly Dictionary<string, string[]> LogoSources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["3DS"] = new[] { Commons("Nintendo_3DS_logo.svg"), Wiki("Nintendo_3DS") },
        ["Arcade"] = new[] { Commons("MAMELogo.svg"), Wiki("Arcade_game") },
        ["DS"] = new[] { Commons("Nintendo_DS_Logo.svg"), Wiki("Nintendo_DS") },
        ["GBA"] = new[] { Commons("Gameboy_advance_logo.svg"), Wiki("Game_Boy_Advance") },
        ["GC"] = new[] { Commons("Nintendo_GameCube_Official_Logo.svg"), Commons("Nintendo_Gamecube_Logo.svg"), Wiki("Nintendo_GameCube") },
        ["MEGADRIVE"] = new[] { Wiki("Sega_Mega_Drive"), Commons("Sega_genesis_logo.svg") },
        ["N64"] = new[] { Commons("Nintendo_64_wordmark.svg"), Wiki("Nintendo_64") },
        ["PC"] = new[] { Commons("Windows_logo_-_2012.svg"), Wiki("Microsoft_Windows") },
        ["PCE"] = new[] { Commons("TurboGrafx16logo.jpg"), Wiki("TurboGrafx-16") },
        ["PS2"] = new[] { Commons("PlayStation_2_logo.svg"), Wiki("PlayStation_2") },
        ["PS4"] = new[] { Commons("PlayStation_4_logo_and_wordmark.svg"), Wiki("PlayStation_4") },
        ["PSX"] = new[] { Commons("Playstation_logo_colour.svg"), Commons("PlayStation_logo.svg"), Wiki("PlayStation_(console)") },
        ["SNES"] = new[] { Commons("Super_Nintendo_Entertainment_System_logo.svg"), Commons("Nintendo_Super_Famicom_logo.svg"), Wiki("Super_Nintendo_Entertainment_System") },
        ["SWITCH"] = new[] { Commons("NintendoSwitchLogo.svg"), Wiki("Nintendo_Switch") },
        ["SWITCH 2"] = new[] { Commons("Nintendo_Switch_2_logo.svg"), Wiki("Nintendo_Switch_2") },
        ["WII"] = new[] { Commons("Wii.svg"), Wiki("Wii") },
        ["WIIU"] = new[] { Commons("WiiU.svg"), Wiki("Wii_U") },
        ["X360"] = new[] { Commons("Xbox_360_wordmark.svg"), Commons("Xbox_360_logo.svg"), Wiki("Xbox_360") },
        ["XBOX"] = new[] { Commons("Xbox_logo_(2005).svg"), Commons("Xbox_2005.svg"), Wiki("Xbox_(console)") },
    };

    private static string Commons(string file) => "commons:" + file;
    private static string Wiki(string page) => "wiki:" + page;

    private static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        try { c.DefaultRequestHeaders.UserAgent.ParseAdd("RecompVault/1.0 (system logo cache)"); } catch { }
        return c;
    }

    public static void Apply(SystemItem item)
    {
        if (item.Name == "Todos") { item.HasLogo = false; return; }
        lock (Loaded)
        {
            if (Loaded.TryGetValue(item.Name, out var cached)) { Set(item, cached); return; }
            if (!LogoSources.ContainsKey(item.Name) || !Attempted.Add(item.Name))
            {
                var p = Path.Combine(CacheDir, Sanitize(item.Name) + ".png");
                if (!File.Exists(p)) return;
            }
        }
        try
        {
            Directory.CreateDirectory(CacheDir);
            var path = Path.Combine(CacheDir, Sanitize(item.Name) + ".png");
            if (File.Exists(path))
            {
                var img = Load(path);
                if (img != null) lock (Loaded) Loaded[item.Name] = img;
                Set(item, img);
                return;
            }
            // try download async in background, keep fallback badge meanwhile
            _ = Task.Run(async () =>
            {
                try
                {
                    var url = await ResolveUrlAsync(item.Name);
                    if (string.IsNullOrEmpty(url)) return;
                    var bytes = await Http.GetByteArrayAsync(url);
                    if (bytes == null || bytes.Length < 200 || !ImageLoader.LooksLikeImage(bytes)) return;
                    await File.WriteAllBytesAsync(path, bytes);
                    var img = Load(path);
                    if (img != null) lock (Loaded) Loaded[item.Name] = img;
                    Set(item, img);
                }
                catch { }
            });
        }
        catch { }
    }

    private static async Task<string?> ResolveUrlAsync(string system)
    {
        if (!LogoSources.TryGetValue(system, out var sources)) return null;
        foreach (var s in sources)
        {
            try
            {
                if (s.StartsWith("commons:", StringComparison.Ordinal))
                {
                    var file = s.Substring("commons:".Length);
                    var url = $"https://commons.wikimedia.org/w/index.php?title=Special:Redirect/file/{Uri.EscapeDataString(file)}&width=320";
                    using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    if (resp.IsSuccessStatusCode) return url;
                }
                else if (s.StartsWith("wiki:", StringComparison.Ordinal))
                {
                    var page = s.Substring("wiki:".Length);
                    var json = await Http.GetStringAsync($"https://en.wikipedia.org/api/rest_v1/page/summary/{Uri.EscapeDataString(page)}");
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("thumbnail", out var t) && t.TryGetProperty("source", out var src))
                        return src.GetString();
                }
            }
            catch { }
        }
        return null;
    }

    private static ImageSource? Load(string path) => ImageLoader.Load(path, 96);

    private static void Set(SystemItem item, ImageSource? img)
    {
        if (img == null) return;
        UiThread.Run(() => { item.LogoImage = img; item.HasLogo = true; });
    }

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }
}