using System.IO;
using System.Text.Json;

namespace RecompVault.Core;

/// <summary>Lectura tolerante de JSON: Quiver 3.x escribe tanto camelCase como PascalCase y a veces BOM.</summary>
public static class JsonUtil
{
    public static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static JsonDocument? ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            ReadOnlyMemory<byte> mem = bytes;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) mem = mem[3..];
            return JsonDocument.Parse(mem, Options);
        }
        catch { return null; }
    }

    public static bool TryGet(this JsonElement e, string name, out JsonElement value)
    {
        value = default;
        if (e.ValueKind != JsonValueKind.Object) return false;
        if (e.TryGetProperty(name, out value)) return true;
        foreach (var p in e.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        }
        return false;
    }

    /// <summary>Texto no vacío del campo, o null.</summary>
    public static string? Str(this JsonElement e, params string[] names)
    {
        foreach (var n in names)
        {
            if (e.TryGet(n, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
        }
        return null;
    }

    public static JsonElement? Obj(this JsonElement e, string name)
        => e.TryGet(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    public static IEnumerable<JsonElement> Arr(this JsonElement e, string name)
        => e.TryGet(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

    public static List<string> Strings(this JsonElement e, string name)
        => e.Arr(name).Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()?.Trim() ?? "")
            .Where(s => s.Length > 0).ToList();
}
