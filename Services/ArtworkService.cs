using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RecompVault.Models;

namespace RecompVault.Services;

/// <summary>Carga de imágenes congeladas (usables desde cualquier hilo) y reducidas al tamaño de la tarjeta.</summary>
public static class ImageLoader
{
    /// <summary>Ancho de decodificación de portadas: tarjeta de 220 px × 2 para pantallas HiDPI.</summary>
    public const int CoverDecodeWidth = 440;

    public static BitmapSource? Load(string path, int decodeWidth = CoverDecodeWidth)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            // Leer a memoria evita dejar el fichero bloqueado (se puede borrar/reemplazar después).
            var bytes = File.ReadAllBytes(path);
            return Load(bytes, decodeWidth);
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo cargar la imagen {path}: {ex.Message}");
            return null;
        }
    }

    public static BitmapSource? Load(byte[] bytes, int decodeWidth = CoverDecodeWidth)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            // .ico con varios tamaños u otros formatos raros: decodificador genérico
            try
            {
                using var ms = new MemoryStream(bytes);
                var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var frame = dec.Frames.OrderByDescending(f => f.PixelWidth).First();
                frame.Freeze();
                return frame;
            }
            catch { return null; }
        }
    }

    public static bool LooksLikeImage(byte[] b)
    {
        if (b.Length < 12) return false;
        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return true;           // PNG
        if (b[0] == 0xFF && b[1] == 0xD8) return true;                                           // JPEG
        if (b[0] == 'G' && b[1] == 'I' && b[2] == 'F') return true;                              // GIF
        if (b[0] == 'B' && b[1] == 'M') return true;                                             // BMP
        if (b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E') return true; // WEBP
        if (b[0] == 0 && b[1] == 0 && b[2] == 1 && b[3] == 0) return true;                       // ICO
        return false;
    }

    public static string ExtensionFor(byte[] b)
    {
        if (b.Length >= 4 && b[0] == 0x89 && b[1] == 0x50) return ".png";
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xD8) return ".jpg";
        if (b.Length >= 3 && b[0] == 'G' && b[1] == 'I') return ".gif";
        if (b.Length >= 2 && b[0] == 'B' && b[1] == 'M') return ".bmp";
        if (b.Length >= 12 && b[0] == 'R' && b[8] == 'W') return ".webp";
        if (b.Length >= 4 && b[0] == 0 && b[2] == 1) return ".ico";
        return ".png";
    }
}

public static class UiThread
{
    public static void Run(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) a();
        else d.BeginInvoke(a);
    }

    public static void RunSync(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) a();
        else d.Invoke(a);
    }
}

public static class ArtworkService
{
    private static readonly string[] CoverNames = { "cover", "boxart", "poster", "banner", "folder", "artwork", "logo", "icon" };
    private static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".ico" };

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string szFileName, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// Aplica portada (override del usuario → imagen en la carpeta → icono personalizado de Quiver) y
    /// el icono del ejecutable como logo. Seguro desde hilos de fondo.
    /// </summary>
    /// <param name="detached">true si la entrada aún no está enlazada a la interfaz (escaneo): se asigna sin pasar por el Dispatcher.</param>
    public static void Apply(GameEntry g, AppConfig cfg, bool detached = false)
    {
        string? coverPath = null;
        var ov = ConfigService.Get(cfg.CoverOverrides, g.FolderPath);
        if (ov != null && File.Exists(ov)) coverPath = ov;
        coverPath ??= FindCoverFile(g.FolderPath);
        if (coverPath == null && !string.IsNullOrWhiteSpace(g.QuiverLocalIconPath) && File.Exists(g.QuiverLocalIconPath))
            coverPath = g.QuiverLocalIconPath;

        var cover = coverPath != null ? ImageLoader.Load(coverPath) : null;
        ImageSource? logo = null;
        if (!string.IsNullOrWhiteSpace(g.LaunchPath) && File.Exists(g.LaunchPath) &&
            g.LaunchPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            try { logo = ExtractIcon(g.LaunchPath); } catch { }
        }
        void Set()
        {
            if (cover != null || coverPath == null) g.CoverImage = cover;
            g.LogoImage = logo;
        }
        if (detached) Set(); else UiThread.RunSync(Set);
    }

    public static void SetCover(GameEntry g, string path)
    {
        var img = ImageLoader.Load(path);
        if (img != null) UiThread.RunSync(() => g.CoverImage = img);
    }

    private static string? FindCoverFile(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return null;
            foreach (var name in CoverNames)
                foreach (var ext in ImageExts)
                {
                    var p = Path.Combine(folder, name + ext);
                    if (File.Exists(p) && new FileInfo(p).Length > 2048) return p; // evita iconos diminutos
                }
            foreach (var f in Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (!ImageExts.Contains(ext)) continue;
                var n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                if (n.Contains("cover") || n.Contains("boxart") || n.Contains("poster") || n.Contains("banner")) return f;
            }
            foreach (var sub in Directory.EnumerateDirectories(folder).Take(20))
                foreach (var name in CoverNames.Take(4))
                    foreach (var ext in ImageExts)
                    {
                        var p = Path.Combine(sub, name + ext);
                        if (File.Exists(p) && new FileInfo(p).Length > 2048) return p;
                    }
        }
        catch { }
        return null;
    }

    private static ImageSource? ExtractIcon(string exePath)
    {
        var large = new IntPtr[1];
        var small = new IntPtr[1];
        try
        {
            if (ExtractIconEx(exePath, 0, large, small, 1) == 0) return null;
            IntPtr h = large[0] != IntPtr.Zero ? large[0] : small[0];
            if (h == IntPtr.Zero) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(h, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
            if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
        }
    }
}
