using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RecompVault.Models;

namespace RecompVault.Services;

/// <summary>
/// Iconos de sistema: siluetas blancas de las consolas del set "monochrome" de RetroArch
/// (libretro/retroarch-assets, PNG con transparencia, el mismo estilo en todos los sistemas).
/// Se descargan una vez a %APPDATA%\RecompVault\system_logos_v3. Un PNG propio en
/// system_logos_custom\&lt;SISTEMA&gt;.png tiene prioridad.
/// </summary>
public static class SystemLogoService
{
    private static readonly HttpClient Http = Create();
    private static readonly string CacheDir = Path.Combine(ConfigService.Dir, "system_logos_v3");
    public static readonly string CustomDir = Path.Combine(ConfigService.Dir, "system_logos_custom");

    // commit fijo: las URLs no cambian aunque el repositorio se reorganice
    private const string Base = "https://raw.githubusercontent.com/libretro/retroarch-assets/8c918e2872482139273374ef4cc6c7aff34d7585/xmb/monochrome/png/";

    private static readonly Dictionary<string, string> Icons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["N64"] = "Nintendo - Nintendo 64",
        ["SNES"] = "Nintendo - Super Nintendo Entertainment System",
        ["NES"] = "Nintendo - Nintendo Entertainment System",
        ["GB"] = "Nintendo - Game Boy",
        ["GBC"] = "Nintendo - Game Boy Color",
        ["GBA"] = "Nintendo - Game Boy Advance",
        ["GC"] = "Nintendo - GameCube",
        ["DS"] = "Nintendo - Nintendo DS",
        ["3DS"] = "Nintendo - Nintendo 3DS",
        ["WII"] = "Nintendo - Wii",
        ["WIIU"] = "Nintendo - Wii U",
        ["SWITCH"] = "Nintendo - Switch",
        ["SWITCH 2"] = "Nintendo - Switch",
        ["PSX"] = "Sony - PlayStation",
        ["PS2"] = "Sony - PlayStation 2",
        ["PS3"] = "Sony - PlayStation 3",
        ["PS4"] = "Sony - PlayStation 4",
        ["PSP"] = "Sony - PlayStation Portable",
        ["XBOX"] = "Microsoft - Xbox",
        ["X360"] = "Microsoft - Xbox 360",
        ["MEGADRIVE"] = "Sega - Mega Drive - Genesis",
        ["SATURN"] = "Sega - Saturn",
        ["DREAMCAST"] = "Sega - Dreamcast",
        ["PCE"] = "NEC - PC Engine - TurboGrafx 16",
        ["Arcade"] = "Arcade Controller",
        ["PC"] = "IBM - PC and Compatibles",
    };

    private static readonly object Gate = new();
    private static readonly Dictionary<string, (ImageSource img, bool alpha)> Loaded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> InFlight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Failed = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Items que esperan una descarga en curso (la lista de sistemas se reconstruye al filtrar).</summary>
    private static readonly Dictionary<string, List<WeakReference<SystemItem>>> Waiting = new(StringComparer.OrdinalIgnoreCase);

    private static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("RecompVault (system icons)");
        return c;
    }

    public static void Apply(SystemItem item)
    {
        if (item.Name == "Todos") return;
        lock (Gate)
        {
            if (Loaded.TryGetValue(item.Name, out var l)) { Set(item, l.img, l.alpha); return; }
        }
        // 1) propio del usuario
        foreach (var ext in new[] { ".png", ".webp", ".jpg", ".jpeg" })
        {
            var custom = Path.Combine(CustomDir, Sanitize(item.Name) + ext);
            if (File.Exists(custom) && TryLoad(custom, out var ci, out var ca)) { Remember(item.Name, ci, ca); Set(item, ci, ca); return; }
        }
        // 2) caché
        var cached = Path.Combine(CacheDir, Sanitize(item.Name) + ".png");
        if (File.Exists(cached) && TryLoad(cached, out var img, out var alpha)) { Remember(item.Name, img, alpha); Set(item, img, alpha); return; }
        // 3) descarga (una vez por sesión y sistema)
        if (!Icons.TryGetValue(item.Name, out var file)) return;
        lock (Gate)
        {
            if (Failed.Contains(item.Name)) return;
            if (!Waiting.TryGetValue(item.Name, out var w)) Waiting[item.Name] = w = new();
            w.Add(new WeakReference<SystemItem>(item));
            if (!InFlight.Add(item.Name)) return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync(Base + Uri.EscapeDataString(file) + ".png");
                if (!ImageLoader.LooksLikeImage(bytes)) throw new InvalidDataException("no es una imagen");
                Directory.CreateDirectory(CacheDir);
                await File.WriteAllBytesAsync(cached, bytes);
                if (TryLoad(cached, out var dimg, out var dalpha))
                {
                    Remember(item.Name, dimg, dalpha);
                    List<WeakReference<SystemItem>> targets;
                    lock (Gate) { targets = Waiting.TryGetValue(item.Name, out var w) ? w.ToList() : new(); Waiting.Remove(item.Name); }
                    foreach (var wr in targets) if (wr.TryGetTarget(out var t)) Set(t, dimg, dalpha);
                }
            }
            catch (Exception ex)
            {
                lock (Gate) Failed.Add(item.Name);
                Log.Warn($"Icono de sistema {item.Name}: {ex.Message}");
            }
            finally { lock (Gate) InFlight.Remove(item.Name); }
        });
    }

    /// <summary>Se vuelve a aplicar a los SystemItem nuevos (la lista se reconstruye al filtrar).</summary>
    private static void Remember(string name, ImageSource img, bool alpha) { lock (Gate) Loaded[name] = (img, alpha); }

    private static bool TryLoad(string path, out ImageSource img, out bool alpha)
    {
        img = null!; alpha = false;
        var b = ImageLoader.Load(path, 256);
        if (b == null) return false;
        img = b;
        alpha = HasTransparency(b);
        return true;
    }

    /// <summary>¿Tiene píxeles transparentes de verdad? (el formato no basta: hay PNG opacos con canal alfa).</summary>
    public static bool HasTransparency(BitmapSource src)
    {
        try
        {
            var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            if (w == 0 || h == 0) return false;
            var stride = w * 4;
            var px = new byte[stride * h];
            conv.CopyPixels(px, stride, 0);
            int transparent = 0, total = w * h;
            for (int i = 3; i < px.Length; i += 4) if (px[i] < 200) transparent++;
            return transparent > total / 20; // más de un 5 %
        }
        catch { return false; }
    }

    private static void Set(SystemItem item, ImageSource img, bool alpha) =>
        UiThread.Run(() => { item.LogoImage = img; item.LogoHasAlpha = alpha; item.HasLogo = true; });

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }
}
