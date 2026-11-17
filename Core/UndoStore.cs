using System.IO;
using System.Text.Json;

namespace RecompVault.Core;

/// <summary>Lo que hizo una actualización, para poder deshacerla.</summary>
public sealed class UndoManifest
{
    public DateTime CreatedUtc { get; set; }
    public string? GameName { get; set; }
    public string? FromVersion { get; set; }
    public string? ToVersion { get; set; }
    public string? AssetName { get; set; }
    /// <summary>Ficheros que existían y se reemplazaron (la copia original está en files\).</summary>
    public List<string> Replaced { get; set; } = new();
    /// <summary>Ficheros que no existían y creó la actualización.</summary>
    public List<string> Created { get; set; } = new();
    /// <summary>Carpetas que creó la actualización (se borran si quedan vacías).</summary>
    public List<string> CreatedDirs { get; set; } = new();
    public long BackupBytes { get; set; }
}

/// <summary>
/// Copia de deshacer de una actualización. Antes de escribir cada fichero se llama a
/// <see cref="BeforeWrite"/>: si ya existía se MUEVE a la copia (misma unidad: instantáneo y sin
/// duplicar espacio hasta que se escribe el nuevo); si no, se apunta como creado.
/// Se guarda en &lt;juego&gt;\.recompvault-undo\&lt;fecha&gt;\ (las carpetas que empiezan por "." las
/// ignoran el escáner y la búsqueda de ejecutables).
/// </summary>
public sealed class UndoSession
{
    public const string RootDirName = ".recompvault-undo";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _game;
    private readonly string _dir;
    private readonly UndoManifest _m;
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private bool _finished;

    public string Directory => _dir;
    public UndoManifest Manifest => _m;

    private UndoSession(string game, string dir, UndoManifest m) { _game = game; _dir = dir; _m = m; }

    public static UndoSession Begin(string gameFolder, string? gameName, string? fromVersion, string? toVersion, string? assetName)
    {
        var root = Path.Combine(gameFolder, RootDirName);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var dir = Path.Combine(root, stamp);
        for (int i = 2; System.IO.Directory.Exists(dir); i++) dir = Path.Combine(root, $"{stamp}-{i}");
        System.IO.Directory.CreateDirectory(Path.Combine(dir, "files"));
        try { File.SetAttributes(root, File.GetAttributes(root) | FileAttributes.Hidden); } catch { }
        var m = new UndoManifest
        {
            CreatedUtc = DateTime.UtcNow, GameName = gameName,
            FromVersion = fromVersion, ToVersion = toVersion, AssetName = assetName
        };
        return new UndoSession(gameFolder, dir, m);
    }

    /// <summary>Llamar justo antes de escribir <paramref name="relPath"/> (relativo a la carpeta del juego).</summary>
    public void BeforeWrite(string relPath)
    {
        relPath = Normalize(relPath);
        if (!_seen.Add(relPath)) return;
        var dest = Path.Combine(_game, relPath);

        // carpetas nuevas que habrá que crear
        var parent = Path.GetDirectoryName(relPath);
        var missing = new List<string>();
        while (!string.IsNullOrEmpty(parent) && !System.IO.Directory.Exists(Path.Combine(_game, parent)))
        {
            missing.Add(parent);
            parent = Path.GetDirectoryName(parent);
        }
        missing.Reverse();
        foreach (var d in missing)
            if (!_m.CreatedDirs.Contains(d, StringComparer.OrdinalIgnoreCase)) _m.CreatedDirs.Add(d);

        if (File.Exists(dest))
        {
            var backup = Path.Combine(_dir, "files", relPath);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            var attr = File.GetAttributes(dest);
            if ((attr & FileAttributes.ReadOnly) != 0) File.SetAttributes(dest, attr & ~FileAttributes.ReadOnly);
            File.Move(dest, backup, overwrite: true);
            _m.Replaced.Add(relPath);
            try { _m.BackupBytes += new FileInfo(backup).Length; } catch { }
        }
        else
        {
            _m.Created.Add(relPath);
        }
    }

    /// <summary>La actualización terminó bien: se guarda el manifiesto y se podan copias antiguas.</summary>
    public void Commit(int keep = 1)
    {
        if (_finished) return;
        _finished = true;
        File.WriteAllText(Path.Combine(_dir, "manifest.json"), JsonSerializer.Serialize(_m, Json));
        UndoStore.Prune(_game, Math.Max(1, keep));
    }

    /// <summary>La actualización falló a medias: se deja todo como estaba.</summary>
    public string? Rollback()
    {
        if (_finished) return null;
        _finished = true;
        var err = UndoStore.Apply(_game, _dir, _m);
        return err;
    }

    internal static string Normalize(string rel) => rel.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
}

public static class UndoStore
{
    private static string Root(string gameFolder) => Path.Combine(gameFolder, UndoSession.RootDirName);

    /// <summary>Copias completas (con manifiesto), de la más reciente a la más antigua.</summary>
    public static List<(string dir, UndoManifest manifest)> List(string gameFolder)
    {
        var result = new List<(string, UndoManifest)>();
        var root = Root(gameFolder);
        if (!Directory.Exists(root)) return result;
        foreach (var d in QuiverLibrary.SafeDirs(root).OrderByDescending(x => Path.GetFileName(x), StringComparer.Ordinal))
        {
            try
            {
                var mf = Path.Combine(d, "manifest.json");
                if (!File.Exists(mf)) continue;
                var m = JsonSerializer.Deserialize<UndoManifest>(File.ReadAllText(mf));
                if (m != null) result.Add((d, m));
            }
            catch { }
        }
        // por fecha del manifiesto, no por nombre de carpeta
        return result.OrderByDescending(x => x.Item2.CreatedUtc).ToList();
    }

    public static UndoManifest? Latest(string gameFolder) => List(gameFolder).Select(x => x.manifest).FirstOrDefault();

    /// <summary>Deshace la última actualización. Devuelve null si todo fue bien o el error.</summary>
    public static string? RestoreLatest(string gameFolder)
    {
        var latest = List(gameFolder).FirstOrDefault();
        if (latest.dir == null) return "No hay ninguna actualización que deshacer.";
        return Apply(gameFolder, latest.dir, latest.manifest);
    }

    internal static string? Apply(string gameFolder, string sessionDir, UndoManifest m)
    {
        var errors = new List<string>();
        // 1) quitar lo que creó la actualización
        foreach (var rel in Enumerable.Reverse(m.Created))
        {
            try
            {
                var p = Path.Combine(gameFolder, rel);
                if (File.Exists(p))
                {
                    var attr = File.GetAttributes(p);
                    if ((attr & FileAttributes.ReadOnly) != 0) File.SetAttributes(p, attr & ~FileAttributes.ReadOnly);
                    File.Delete(p);
                }
            }
            catch (Exception ex) { errors.Add($"{rel}: {ex.Message}"); }
        }
        // 2) devolver los originales
        foreach (var rel in m.Replaced)
        {
            try
            {
                var backup = Path.Combine(sessionDir, "files", rel);
                var dest = Path.Combine(gameFolder, rel);
                if (!File.Exists(backup)) { errors.Add($"{rel}: falta la copia"); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (File.Exists(dest))
                {
                    var attr = File.GetAttributes(dest);
                    if ((attr & FileAttributes.ReadOnly) != 0) File.SetAttributes(dest, attr & ~FileAttributes.ReadOnly);
                }
                File.Move(backup, dest, overwrite: true);
            }
            catch (Exception ex) { errors.Add($"{rel}: {ex.Message}"); }
        }
        // 3) carpetas nuevas que hayan quedado vacías (de la más profunda a la menos)
        foreach (var rel in m.CreatedDirs.OrderByDescending(d => d.Length))
        {
            try
            {
                var p = Path.Combine(gameFolder, rel);
                if (Directory.Exists(p) && !Directory.EnumerateFileSystemEntries(p).Any()) Directory.Delete(p);
            }
            catch { }
        }

        if (errors.Count > 0)
        {
            // se conserva la copia para poder reintentar
            return $"No se pudieron restaurar {errors.Count} ficheros (¿juego abierto?): {string.Join("; ", errors.Take(3))}";
        }
        try { Directory.Delete(sessionDir, true); } catch { }
        CleanupRoot(gameFolder);
        return null;
    }

    public static void Discard(string gameFolder)
    {
        try { if (Directory.Exists(Root(gameFolder))) Directory.Delete(Root(gameFolder), true); } catch { }
    }

    /// <summary>Deja solo las <paramref name="keep"/> copias más recientes (y borra sesiones a medias).</summary>
    public static void Prune(string gameFolder, int keep)
    {
        var root = Root(gameFolder);
        if (!Directory.Exists(root)) return;
        var keepDirs = List(gameFolder).Take(keep).Select(x => x.dir).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var d in QuiverLibrary.SafeDirs(root))
        {
            if (keepDirs.Contains(d)) continue;
            try { Directory.Delete(d, true); } catch { }
        }
        CleanupRoot(gameFolder);
    }

    private static void CleanupRoot(string gameFolder)
    {
        var root = Root(gameFolder);
        try { if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root); } catch { }
    }
}
