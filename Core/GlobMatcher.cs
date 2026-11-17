using System.IO;
using System.Text.RegularExpressions;

namespace RecompVault.Core;

/// <summary>
/// Patrones de ficheros protegidos (no se sobrescriben al actualizar), relativos a la carpeta del juego:
/// "*.ini" (cualquier nivel), "saves/**" (carpeta y todo su contenido), "config/user.json" (ruta exacta).
/// Sin distinguir mayúsculas; acepta '/' y '\'.
/// </summary>
public static class GlobMatcher
{
    public static readonly string[] DefaultProtected =
    {
        "saves/**", "save/**", "savedata/**", "saved/**", "userdata/**", "user/**",
        "*.sav", "*.srm", "*.state", "*.mcr", "*.mcd", "portable.txt"
    };

    public static List<string> Parse(string? text) =>
        (text ?? "").Split(new[] { ';', '\n', '\r', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static bool IsMatch(string relativePath, IEnumerable<string> patterns) => patterns.Any(p => IsMatch(relativePath, p));

    public static bool IsMatch(string relativePath, string pattern)
    {
        var path = relativePath.Replace('\\', '/').TrimStart('/');
        var pat = pattern.Trim().Replace('\\', '/').TrimStart('/');
        if (pat.Length == 0) return false;
        // sin '/' → se compara con el nombre del fichero en cualquier nivel
        if (!pat.Contains('/')) return Rx(pat).IsMatch(Path.GetFileName(path));
        if (pat.EndsWith("/**")) pat = pat[..^3] + "/**"; // normaliza
        return Rx(pat).IsMatch(path);
    }

    private static readonly Dictionary<string, Regex> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static Regex Rx(string pat)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(pat, out var r)) return r;
            var sb = new System.Text.StringBuilder("^");
            for (int i = 0; i < pat.Length; i++)
            {
                var c = pat[i];
                if (c == '*' && i + 1 < pat.Length && pat[i + 1] == '*')
                {
                    sb.Append(".*"); i++;
                    if (i + 1 < pat.Length && pat[i + 1] == '/') i++; // "**/" también vale para cero carpetas
                }
                else if (c == '*') sb.Append("[^/]*");
                else if (c == '?') sb.Append("[^/]");
                else sb.Append(Regex.Escape(c.ToString()));
            }
            sb.Append('$');
            r = new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            Cache[pat] = r;
            return r;
        }
    }
}
