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
    private static readonly string CacheDir = Path.Combine(ConfigService.Dir, "system_logos_v2");
    /// <summary>Logos propios del usuario: %APPDATA%\RecompVault\system_logos_custom\&lt;SISTEMA&gt;.png (tienen prioridad).</summary>
    public static readonly string CustomDir = Path.Combine(ConfigService.Dir, "system_logos_custom");
    /// <summary>Sistemas ya intentados en esta sesión (evita repetir descargas fallidas en cada filtro).</summary>
    private static readonly HashSet<string> Attempted = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ImageSource> Loaded = new(StringComparer.OrdinalIgnoreCase);

    // Curated: prefer official wordmark/logos (con letras). Using Commons redirect for reliable thumbnails.
    private static readonly Dictionary<string, string[]> LogoSources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["3DS"] = new[] { Commons("Nintendo_3DS_logo.svg") },
        ["Arcade"] = new[] { Commons("MAMELogo.svg") },
        ["DS"] = new[] { Commons("Nintendo_DS_Logo.svg") },
        ["GBA"] = new[] { Commons("Gameboy_advance_logo.svg") },
        ["GC"] = new[] { Commons("Nintendo_GameCube_Official_Logo.svg"), Commons("Nintendo_Gamecube_Logo.svg") },
        ["MEGADRIVE"] = new[] { Commons("Mega_Drive_Logo.svg"), Commons("Sega_genesis_logo.svg") },
        ["N64"] = new[] { Commons("Nintendo_64_wordmark.svg") },
        ["PC"] = new[] { Commons("Windows_logo_-_2012.svg") },
        ["PCE"] = new[] { Commons("TurboGrafx-16_Logo.svg"), Commons("PC_Engine_logo.svg"), Commons("TurboGrafx16logo.jpg") },
        ["PS2"] = new[] { Commons("PlayStation_2_logo.svg") },
        ["PS4"] = new[] { Commons("PlayStation_4_logo_and_wordmark.svg") },
        ["PSX"] = new[] { Commons("Playstation_logo_colour.svg"), Commons("PlayStation_logo.svg") },
        ["SNES"] = new[] { Commons("Super_Nintendo_Entertainment_System_logo.svg"), Commons("Nintendo_Super_Famicom_logo.svg") },
        ["SWITCH"] = new[] { Commons("NintendoSwitchLogo.svg") },
        ["SWITCH 2"] = new[] { Commons("Nintendo_Switch_2_logo.svg") },
        ["WII"] = new[] { Commons("Wii.svg") },
        ["WIIU"] = new[] { Commons("WiiU.svg") },
        ["X360"] = new[] { Commons("Xbox_360_wordmark.svg"), Commons("Xbox_360_logo.svg") },
        ["DREAMCAST"] = new[] { Commons("Dreamcast_logo.svg") },
        ["SATURN"] = new[] { Commons("Sega_Saturn_Logo.svg") },
        ["PS3"] = new[] { Commons("PlayStation_3_logo_(2009).svg"), Commons("PlayStation3Logo.svg") },
        ["PSP"] = new[] { Commons("PSP_Logo.svg") },
        ["NES"] = new[] { Commons("NES_logo.svg") },
        ["GB"] = new[] { Commons("Game_Boy_logo.svg") },
        ["GBC"] = new[] { Commons("Game_Boy_Color_logo.svg") },
        ["XBOX"] = new[] { Commons("Xbox_logo_(2005).svg"), Commons("Xbox_2005.svg") },
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
        // logo propio del usuario
        foreach (var ext in new[] { ".png", ".webp", ".jpg" })
        {
            var custom = Path.Combine(CustomDir, Sanitize(item.Name) + ext);
            if (File.Exists(custom)) { Set(item, Load(custom)); return; }
        }
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
                    var url = $"https://commons.wikimedia.org/w/index.php?title=Special:Redirect/file/{Uri.EscapeDataString(file)}&width=640";
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

    private static ImageSource? Load(string path) => ImageLoader.Load(path, 480);

    private static void Set(SystemItem item, ImageSource? img)
    {
        if (img == null) return;
        var alpha = SystemItem.HasAlpha(img);
        UiThread.Run(() => { item.LogoImage = img; item.LogoHasAlpha = alpha; item.HasLogo = true; });
    }

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }
}