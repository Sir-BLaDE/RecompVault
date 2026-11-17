using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RecompVault.Models;

namespace RecompVault.Services;

public static class ArtworkService
{
    private static readonly string[] CoverNames = { "cover", "banner", "logo", "artwork", "poster", "folder", "icon" };
    private static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".bmp", ".ico", ".webp" };

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern uint ExtractIconEx(string szFileName, int nIconIndex, IntPtr[] phiconLarge, IntPtr[] phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static void Apply(GameEntry g, AppConfig cfg)
    {
        // cover override from config
        string? coverPath = null;
        if (cfg.CoverOverrides.TryGetValue(g.FolderPath, out var ov) && File.Exists(ov)) coverPath = ov;
        if (coverPath == null) coverPath = FindCoverFile(g.FolderPath);

        if (coverPath != null)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(coverPath, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                SetCover(g, bmp);
            }
            catch { }
        }

        // logo from exe icon (fallback if no cover)
        if (g.LogoImage == null && !string.IsNullOrWhiteSpace(g.LaunchPath) && File.Exists(g.LaunchPath))
        {
            try { SetLogo(g, ExtractIcon(g.LaunchPath)); } catch { }
        }
    }

    private static void SetCover(GameEntry g, ImageSource? src)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess()) dispatcher.Invoke(() => g.CoverImage = src);
        else g.CoverImage = src;
    }

    private static void SetLogo(GameEntry g, ImageSource? src)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess()) dispatcher.Invoke(() => g.LogoImage = src);
        else g.LogoImage = src;
    }

    private static string? FindCoverFile(string folder)
    {
        try
        {
            // root level first
            foreach (var name in CoverNames)
                foreach (var ext in ImageExts)
                {
                    var p = Path.Combine(folder, name + ext);
                    if (File.Exists(p)) return p;
                }
            // one level deep
            foreach (var sub in Directory.GetDirectories(folder))
            {
                foreach (var name in CoverNames)
                    foreach (var ext in ImageExts)
                    {
                        var p = Path.Combine(sub, name + ext);
                        if (File.Exists(p)) return p;
                    }
            }
            // any image file named with cover-ish
            foreach (var f in Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly))
            {
                var n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ImageExts.Contains(ext) && (n.Contains("cover") || n.Contains("banner") || n.Contains("poster")))
                    return f;
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
            ExtractIconEx(exePath, 0, large, small, 1);
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