using System.IO;
namespace RecompVault.Core;

public sealed record ReleaseAsset(string Name, string Url, string? Digest = null, long Size = 0);

/// <summary>Elige el paquete de Windows x64 más probable de un release de GitHub/GitLab.</summary>
public static class ReleaseAssetPicker
{
    private static readonly string[] OtherOs =
        { "linux", "android", "macos", "osx", "darwin", "mac-", "-mac", "_mac", "apple", "ios", "appimage", "flatpak", ".deb", ".rpm", ".apk", ".dmg", ".pkg",
          "switch", "psvita", "vita", "psp", "3ds", "wiiu", "steamdeck", "freebsd", "haiku", "web", "wasm", "emscripten" };

    private static readonly string[] Junk =
        { "source", "src.", "-src", "_src", "symbols", ".pdb", "debuginfo", "dsym", ".sha256", ".sha1", ".md5", ".sig", ".asc", ".txt", ".json",
          ".md", ".yml", ".yaml", ".blockmap", "checksum", "sbom", ".nupkg", ".sym" };

    private static readonly string[] Installable = { ".zip", ".7z", ".rar", ".tar.gz", ".tgz", ".tar.xz", ".exe", ".msi" };

    /// <param name="filter">releaseAssetFilter de Quiver: texto contenido o comodines * ?</param>
    /// <param name="installedFileName">Paquete instalado (installed-build.txt): se prefiere el de la misma "forma".</param>
    public static ReleaseAsset? Pick(IEnumerable<ReleaseAsset> assets, string? filter = null, string? installedFileName = null)
    {
        var list = assets.Where(a => MatchesFilter(a.Name, filter)).ToList();
        var shape = Shape(installedFileName);
        ReleaseAsset? best = null; int bestScore = int.MinValue;
        foreach (var a in list)
        {
            var s = Score(a.Name);
            if (s < 0) continue;
            if (shape.Length > 0 && Shape(a.Name) == shape) s += 150;
            if (s > bestScore) { bestScore = s; best = a; }
        }
        return best;
    }

    /// <summary>Misma semántica que Quiver (RepositorySourceHelper.MatchesReleaseAssetFilter).</summary>
    public static bool MatchesFilter(string name, string? filter)
    {
        var f = filter?.Trim();
        if (string.IsNullOrEmpty(f)) return true;
        if (f.IndexOfAny(new[] { '*', '?' }) < 0) return (name ?? "").Contains(f, StringComparison.OrdinalIgnoreCase);
        var pattern = "^" + System.Text.RegularExpressions.Regex.Escape(f).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        try
        {
            return System.Text.RegularExpressions.Regex.IsMatch(name ?? "", pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch { return false; }
    }

    /// <summary>Nombre sin números ni separadores: "Game-v1.2.0-windows.zip" y "Game-v1.3.0-windows.zip" tienen la misma forma.</summary>
    public static string Shape(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "" : new string(name.ToLowerInvariant().Where(char.IsLetter).ToArray()).Replace("v", "");

    public static int Score(string name)
    {
        var n = (name ?? "").ToLowerInvariant();
        if (n.Length == 0) return int.MinValue;
        if (!Installable.Any(n.EndsWith)) return -1000;
        if (Junk.Any(n.Contains)) return -1000;

        int score = 0;
        bool win = n.Contains("windows") || n.Contains("win64") || n.Contains("win-x64") || n.Contains("win_x64") || n.Contains("win32") ||
                   n.Contains("-win.") || n.Contains("-win-") || n.Contains("_win.") || n.Contains("_win_") || n.Contains("_win-") || n.Contains(".win.") ||
                   n.Contains("msvc") || n.Contains("mingw") || n.EndsWith(".exe") || n.EndsWith(".msi");
        if (win) score += 60;
        if (OtherOs.Any(n.Contains) && !win) score -= 200;
        else if (OtherOs.Any(n.Contains)) score -= 40;

        if (n.Contains("x64") || n.Contains("x86_64") || n.Contains("amd64") || n.Contains("win64")) score += 15;
        if (n.Contains("arm64") || n.Contains("aarch64") || n.Contains("armv7")) score -= 50;
        if (n.Contains("x86") && !n.Contains("x86_64") && !n.Contains("x86-64")) score -= 10;
        if (n.Contains("debug") || n.Contains("-dbg")) score -= 25;
        if (n.Contains("portable")) score += 8;
        if (n.Contains("setup") || n.Contains("installer")) score -= 6;
        if (n.Contains("nosse") || n.Contains("legacy") || n.Contains("compat")) score -= 5;
        if (n.Contains("avx2") || n.Contains("avx512")) score -= 3;

        if (n.EndsWith(".zip")) score += 20;
        else if (n.EndsWith(".exe")) score += 12;
        else if (n.EndsWith(".7z")) score += 10;
        else if (n.EndsWith(".tar.gz") || n.EndsWith(".tgz")) score += 4;
        else if (n.EndsWith(".msi")) score += 2;

        // Un paquete sin marca de SO puede ser válido (muchos recomps solo publican Windows),
        // pero solo si no hay nada mejor: score bajo pero >= 0.
        return score;
    }

    public static bool IsInstaller(string name)
    {
        var n = (name ?? "").ToLowerInvariant();
        return n.EndsWith(".msi") || (n.EndsWith(".exe") && (n.Contains("setup") || n.Contains("installer") || n.Contains("install")));
    }
}
