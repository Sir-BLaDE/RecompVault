using System.IO;
using System.Text;

namespace RecompVault.Core;

public sealed record ZipEntryInfo(string Name, uint Crc32, long Size);

/// <summary>
/// Lee el directorio central de un .zip a partir de su final (sin tener el archivo entero): sirve para
/// comparar los ficheros instalados con los de un release de GitHub descargando solo unos KB.
/// Soporta ZIP64.
/// </summary>
public static class ZipIndex
{
    public sealed record Location(long Offset, long Size);

    /// <summary>
    /// Busca el registro final en <paramref name="tail"/> (los últimos bytes del archivo).
    /// Devuelve dónde está el directorio central, o null si no es un zip válido.
    /// </summary>
    public static Location? FindCentralDirectory(byte[] tail, long fileLength)
    {
        for (int i = tail.Length - 22; i >= 0; i--)
        {
            if (U32(tail, i) != 0x06054b50) continue;
            long cdSize = U32(tail, i + 12);
            long cdOffset = U32(tail, i + 16);
            if (cdOffset == 0xFFFFFFFF || cdSize == 0xFFFFFFFF)
            {
                // ZIP64: localizador 20 bytes antes del registro final
                int loc = i - 20;
                if (loc < 0 || U32(tail, loc) != 0x07064b50) return null;
                long z64Offset = (long)U64(tail, loc + 8);
                long tailStart = fileLength - tail.Length;
                int z = (int)(z64Offset - tailStart);
                if (z < 0 || z + 56 > tail.Length || U32(tail, z) != 0x06064b50) return null;
                cdSize = (long)U64(tail, z + 40);
                cdOffset = (long)U64(tail, z + 48);
            }
            if (cdOffset < 0 || cdSize <= 0 || cdOffset + cdSize > fileLength) return null;
            return new Location(cdOffset, cdSize);
        }
        return null;
    }

    public static List<ZipEntryInfo> ParseCentralDirectory(byte[] cd)
    {
        var list = new List<ZipEntryInfo>();
        int p = 0;
        while (p + 46 <= cd.Length && U32(cd, p) == 0x02014b50)
        {
            ushort flags = U16(cd, p + 8);
            uint crc = U32(cd, p + 16);
            long size = U32(cd, p + 24);
            int nameLen = U16(cd, p + 28), extraLen = U16(cd, p + 30), commentLen = U16(cd, p + 32);
            if (p + 46 + nameLen + extraLen + commentLen > cd.Length) break;
            var enc = (flags & 0x0800) != 0 ? Encoding.UTF8 : Encoding.Latin1;
            var name = enc.GetString(cd, p + 46, nameLen);
            if (size == 0xFFFFFFFF)
            {
                // campo extra ZIP64 (id 0x0001): primero el tamaño sin comprimir
                int e = p + 46 + nameLen, end = e + extraLen;
                while (e + 4 <= end)
                {
                    ushort id = U16(cd, e), len = U16(cd, e + 2);
                    if (id == 0x0001 && len >= 8) { size = (long)U64(cd, e + 4); break; }
                    e += 4 + len;
                }
            }
            if (!name.EndsWith('/')) list.Add(new ZipEntryInfo(name.Replace('\\', '/'), crc, size));
            p += 46 + nameLen + extraLen + commentLen;
        }
        return list;
    }

    /// <summary>¿Coincide el fichero local (nombre, tamaño y CRC32) con alguna entrada del zip?</summary>
    public static ZipEntryInfo? FindMatch(IEnumerable<ZipEntryInfo> entries, string fileName, long size, uint crc) =>
        entries.FirstOrDefault(e => e.Size == size && e.Crc32 == crc &&
                                    string.Equals(Path.GetFileName(e.Name), fileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>CRC32 (el mismo que guarda el zip) de un fichero local.</summary>
    public static uint FileCrc32(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        var buf = new byte[1 << 16];
        uint crc = 0xFFFFFFFF;
        int n;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < n; i++) crc = Table[(crc ^ buf[i]) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(i =>
    {
        var c = (uint)i;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static ushort U16(byte[] b, int i) => (ushort)(b[i] | b[i + 1] << 8);
    private static uint U32(byte[] b, int i) => (uint)(b[i] | b[i + 1] << 8 | b[i + 2] << 16 | b[i + 3] << 24);
    private static ulong U64(byte[] b, int i) => U32(b, i) | (ulong)U32(b, i + 4) << 32;
}
