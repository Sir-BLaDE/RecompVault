using System.IO;
namespace RecompVault.Core;

public static class PathUtil
{
    /// <summary>Ruta normalizada para comparar (absoluta, separador '\', sin barra final, minúsculas).</summary>
    public static string Norm(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return "";
        var s = p.Trim().Trim('"');
        try { s = Path.GetFullPath(s); } catch { }
        return s.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
    }

    /// <summary>true si <paramref name="child"/> es la misma carpeta que <paramref name="parent"/> o está dentro.</summary>
    public static bool IsSameOrInside(string child, string parent)
    {
        var c = Norm(child); var p = Norm(parent);
        if (c.Length == 0 || p.Length == 0) return false;
        return c == p || c.StartsWith(p + "\\", StringComparison.Ordinal);
    }

    /// <summary>Solo letras y dígitos en minúsculas: "Zelda64Recompiled-v1.2" → "zelda64recompiledv12".</summary>
    public static string NormName(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        return new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    /// <summary>Nombre de fichero seguro.</summary>
    public static string SafeFileName(string? s, int max = 80)
    {
        if (string.IsNullOrWhiteSpace(s)) return "x";
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        s = s.Replace('\\', '_').Replace('/', '_').Replace(':', '_').Trim();
        return s.Length > max ? s[..max] : s;
    }

    /// <summary>Hash corto y estable (no usar string.GetHashCode: cambia en cada ejecución en .NET).</summary>
    public static string StableHash(string? s)
    {
        var bytes = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(Norm(s)));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }
}
