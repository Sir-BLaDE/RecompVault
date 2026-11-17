using System.IO;
using System.Text;

namespace RecompVault.Core;

/// <summary>
/// Lectura/escritura del formato VDF binario de Steam (userdata\&lt;id&gt;\config\shortcuts.vdf).
/// Tipos: 0x00 mapa, 0x01 texto, 0x02 entero de 32 bits, 0x08 fin de mapa.
/// </summary>
public sealed class VdfNode
{
    public Dictionary<string, object> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    // conserva el orden de inserción para reescribir igual
    public List<string> Order { get; } = new();

    public void Set(string key, object value)
    {
        if (!Values.ContainsKey(key)) Order.Add(key);
        Values[key] = value;
    }

    public object? Get(string key) => Values.TryGetValue(key, out var v) ? v : null;
}

public static class BinaryVdf
{
    public static VdfNode Read(byte[] data)
    {
        int pos = 0;
        var root = new VdfNode();
        ReadMap(data, ref pos, root);
        return root;
    }

    private static void ReadMap(byte[] d, ref int pos, VdfNode node)
    {
        while (pos < d.Length)
        {
            var type = d[pos++];
            if (type == 0x08) return;
            var key = ReadString(d, ref pos);
            switch (type)
            {
                case 0x00:
                    var child = new VdfNode();
                    ReadMap(d, ref pos, child);
                    node.Set(key, child);
                    break;
                case 0x01:
                    node.Set(key, ReadString(d, ref pos));
                    break;
                case 0x02:
                    node.Set(key, BitConverter.ToInt32(d, pos)); pos += 4;
                    break;
                case 0x07: // uint64 (raro en shortcuts)
                    node.Set(key, BitConverter.ToUInt64(d, pos)); pos += 8;
                    break;
                default:
                    throw new InvalidDataException($"Tipo VDF desconocido 0x{type:X2} en {pos - 1}");
            }
        }
    }

    private static string ReadString(byte[] d, ref int pos)
    {
        int start = pos;
        while (pos < d.Length && d[pos] != 0) pos++;
        var s = Encoding.UTF8.GetString(d, start, pos - start);
        pos++; // nulo
        return s;
    }

    public static byte[] Write(VdfNode root)
    {
        using var ms = new MemoryStream();
        WriteMap(ms, root);
        ms.WriteByte(0x08);
        return ms.ToArray();
    }

    private static void WriteMap(Stream s, VdfNode node)
    {
        foreach (var key in node.Order)
        {
            var v = node.Values[key];
            switch (v)
            {
                case VdfNode child:
                    s.WriteByte(0x00); WriteString(s, key); WriteMap(s, child); s.WriteByte(0x08); break;
                case string str:
                    s.WriteByte(0x01); WriteString(s, key); WriteString(s, str); break;
                case int i:
                    s.WriteByte(0x02); WriteString(s, key); s.Write(BitConverter.GetBytes(i)); break;
                case ulong u:
                    s.WriteByte(0x07); WriteString(s, key); s.Write(BitConverter.GetBytes(u)); break;
            }
        }
    }

    private static void WriteString(Stream s, string str)
    {
        var b = Encoding.UTF8.GetBytes(str);
        s.Write(b);
        s.WriteByte(0);
    }
}

public static class SteamShortcuts
{
    /// <summary>appid de un acceso no-Steam (el que usa Steam para la carpeta grid): crc32(exe + nombre) | 0x80000000.</summary>
    public static uint ShortcutAppId(string quotedExe, string appName) =>
        Crc32(Encoding.UTF8.GetBytes(quotedExe + appName)) | 0x80000000;

    /// <summary>Añade (o actualiza si ya existe el mismo exe) un acceso en el contenido de shortcuts.vdf.</summary>
    public static (byte[] data, uint appId, bool updated) AddOrUpdate(byte[]? existing, string name, string exePath, string startDir, string iconPath, string launchOptions, IEnumerable<string> tags)
    {
        var root = existing is { Length: > 0 } ? BinaryVdf.Read(existing) : new VdfNode();
        if (root.Get("shortcuts") is not VdfNode list) { list = new VdfNode(); root.Set("shortcuts", list); }

        var exe = "\"" + exePath + "\"";
        var appId = ShortcutAppId(exe, name);
        VdfNode? entry = null;
        foreach (var k in list.Order)
        {
            if (list.Values[k] is VdfNode n && n.Get("Exe") is string e && string.Equals(e.Trim('"'), exePath, StringComparison.OrdinalIgnoreCase))
            { entry = n; break; }
        }
        bool updated = entry != null;
        if (entry == null)
        {
            var nextIndex = list.Order.Select(k => int.TryParse(k, out var i) ? i : -1).DefaultIfEmpty(-1).Max() + 1;
            entry = new VdfNode();
            list.Set(nextIndex.ToString(), entry);
        }
        entry.Set("appid", unchecked((int)appId));
        entry.Set("AppName", name);
        entry.Set("Exe", exe);
        entry.Set("StartDir", "\"" + startDir + "\"");
        entry.Set("icon", iconPath);
        entry.Set("ShortcutPath", "");
        entry.Set("LaunchOptions", launchOptions ?? "");
        if (entry.Get("IsHidden") == null) entry.Set("IsHidden", 0);
        if (entry.Get("AllowDesktopConfig") == null) entry.Set("AllowDesktopConfig", 1);
        if (entry.Get("AllowOverlay") == null) entry.Set("AllowOverlay", 1);
        if (entry.Get("OpenVR") == null) entry.Set("OpenVR", 0);
        if (entry.Get("Devkit") == null) entry.Set("Devkit", 0);
        if (entry.Get("DevkitGameID") == null) entry.Set("DevkitGameID", "");
        if (entry.Get("DevkitOverrideAppID") == null) entry.Set("DevkitOverrideAppID", 0);
        if (entry.Get("LastPlayTime") == null) entry.Set("LastPlayTime", 0);
        if (entry.Get("FlatpakAppID") == null) entry.Set("FlatpakAppID", "");
        var tagNode = new VdfNode();
        int t = 0;
        foreach (var tag in tags.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)) tagNode.Set((t++).ToString(), tag);
        entry.Set("tags", tagNode);
        return (BinaryVdf.Write(root), appId, updated);
    }

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
