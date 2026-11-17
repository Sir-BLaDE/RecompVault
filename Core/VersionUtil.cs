namespace RecompVault.Core;

/// <summary>
/// Comparación de versiones con la misma semántica que Quiver Launcher
/// (ReleaseVersionIdentity): "v1.2" == "1.2.0", las etiquetas (-beta, -nightly...)
/// solo son iguales si coinciden literalmente y los valores de "no sé" de Quiver
/// ("0.0.0", "v0.0.0", "unknown") cuentan como versión desconocida.
/// </summary>
public static class VersionUtil
{
    private static readonly HashSet<string> UnknownMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "unknown", "desconocida", "instalada", "0.0.0", "v0.0.0", "0", "v0", "0.0", "v0.0", "null", "none"
    };

    /// <summary>Devuelve la versión limpia o null si es vacía o un marcador de "desconocida".</summary>
    public static string? Clean(string? version)
    {
        if (version == null) return null;
        var v = version.Trim().Trim('﻿').Trim();
        var nl = v.IndexOfAny(new[] { '\r', '\n' });
        if (nl >= 0) v = v[..nl].Trim();
        return UnknownMarkers.Contains(v) ? null : v;
    }

    public static string Normalize(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "0.0.0";
        var normalized = StripBuild(version.Trim().TrimStart('v', 'V'));
        var labelAt = normalized.IndexOfAny(new[] { '-', ' ', '\t' });
        if (labelAt >= 0) normalized = normalized[..labelAt];

        var segments = new List<string>();
        foreach (var part in normalized.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            int len = 0;
            while (len < part.Length && char.IsDigit(part[len])) len++;
            if (len == 0) break;
            // evita overflow en Version con números absurdos (fechas tipo 20261117)
            var digits = part[..len].TrimStart('0');
            if (digits.Length == 0) digits = "0";
            if (digits.Length > 9) digits = digits[..9];
            segments.Add(digits);
        }
        while (segments.Count < 3) segments.Add("0");
        return string.Join(".", segments.Take(4));
    }

    public static bool IsNewer(string candidate, string baseline)
    {
        try { return new Version(Normalize(candidate)).CompareTo(new Version(Normalize(baseline))) > 0; }
        catch { return false; }
    }

    public static bool Equivalent(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        var ia = Identity(a); var ib = Identity(b);
        if (ia.Equals(ib, StringComparison.OrdinalIgnoreCase)) return true;
        if (HasLabel(a) || HasLabel(b)) return false;
        // Tags sin ningún dígito (p.ej. "nightly", "latest") solo son iguales literalmente.
        if (!a.Any(char.IsDigit) || !b.Any(char.IsDigit)) return false;
        try { return new Version(Normalize(a)).Equals(new Version(Normalize(b))); }
        catch { return false; }
    }

    /// <summary>
    /// Hay actualización solo si se conocen ambas versiones, no son equivalentes
    /// y la instalada no es más nueva que la publicada (p.ej. una prerelease instalada a mano).
    /// </summary>
    public static bool IsUpdate(string? installed, string? latest)
    {
        var i = Clean(installed); var l = Clean(latest);
        if (i == null || l == null) return false;
        if (Equivalent(i, l)) return false;
        if (IsNewer(i, l)) return false;
        return true;
    }

    private static string StripBuild(string v) { var p = v.IndexOf('+'); return p >= 0 ? v[..p] : v; }
    private static string Identity(string v) => StripBuild(v.Trim().TrimStart('v', 'V'));
    private static bool HasLabel(string v) => Identity(v).IndexOfAny(new[] { '-', ' ', '\t' }) >= 0;
}
