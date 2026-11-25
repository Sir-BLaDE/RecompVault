using System.IO;
using System.Text.Json;

namespace RecompVault.Services;

public class AppConfig
{
    public string RootPath { get; set; } = @"D:\Juegos\-RECOMPILED-DECOMPILED-";
    public List<string> Favorites { get; set; } = new();
    public Dictionary<string, string> LaunchOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase); // folderPath -> launchPath
    public Dictionary<string, string> CoverOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);  // folderPath -> imagePath
    public Dictionary<string, string> NameOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);   // folderPath -> nombre
    public Dictionary<string, DateTime> LastPlayed { get; set; } = new(StringComparer.OrdinalIgnoreCase);    // folderPath -> fecha
    public string? SteamGridDbApiKey { get; set; }
    public string? RawgApiKey { get; set; }
    public string? IgdbClientId { get; set; }
    public string? IgdbClientSecret { get; set; }
    public bool AutoCoverAttempted { get; set; }
    public List<string> Hidden { get; set; } = new();
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
    public bool WindowMaximized { get; set; }
    public bool CompactSystems { get; set; }
    /// <summary>Usar el catálogo de thegamingemporium.com para vincular recomps que Quiver no conoce.</summary>
    public bool UseEmporium { get; set; } = true;
    /// <summary>Comprobar al arrancar (y cada 6 h) si hay una versión nueva de RecompVault en GitHub.</summary>
    public bool CheckAppUpdates { get; set; } = true;
    /// <summary>Versión de RecompVault que el usuario decidió omitir (no se vuelve a avisar con diálogo).</summary>
    public string? SkippedAppVersion { get; set; }

    // Integración con Quiver
    public string QuiverPath { get; set; } = "";           // vacío = autodetectar
    public bool QuiverSyncEnabled { get; set; } = true;
    /// <summary>Muestra también las apps instaladas por Quiver que no están dentro de la carpeta raíz.</summary>
    public bool IncludeQuiverAppsOutsideRoot { get; set; } = true;
    /// <summary>
    /// Vincular automáticamente por coincidencia aproximada. Desactivado por defecto: solo se vincula por ruta
    /// o nombre de carpeta exactos; las aproximadas se muestran como sugerencia en ℹ.
    /// </summary>
    public bool FuzzyQuiverMatching { get; set; } = false;
    public bool AutoUpdateRecomps { get; set; } = false;
    public bool AutoCheckUpdates { get; set; } = true;
    /// <summary>Horas mínimas entre consultas automáticas a GitHub/GitLab para el mismo repositorio.</summary>
    public int UpdateCheckIntervalHours { get; set; } = 6;
    public bool IncludePrereleases { get; set; } = true;
    public string? GitHubToken { get; set; } // opcional: sube el límite de la API de 60 a 5000 peticiones/hora
    /// <summary>Copias de deshacer que se guardan por juego (solo los ficheros reemplazados).</summary>
    public int UndoBackupsToKeep { get; set; } = 1;
    /// <summary>Vínculo elegido a mano en "Detección": folderPath → repositorio/app de Quiver (o desvinculado).</summary>
    public Dictionary<string, LinkOverride> LinkOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Versiones fijadas (folderPath → tag): no se marcan como desactualizadas ni se auto-actualizan.</summary>
    public Dictionary<string, string> PinnedVersions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Patrones que nunca se sobrescriben al actualizar si ya existen (partidas, ajustes...).</summary>
    public List<string> ProtectedPatterns { get; set; } = RecompVault.Core.GlobMatcher.DefaultProtected.ToList();
    /// <summary>Patrones protegidos adicionales por juego (folderPath → "patrón; patrón").</summary>
    public Dictionary<string, string> ProtectedByGame { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Al actualizar a mano, mostrar antes las notas del release y el paquete.</summary>
    public bool ConfirmUpdatesWithChangelog { get; set; } = true;
    /// <summary>Tiempo jugado acumulado en segundos (folderPath → s).</summary>
    public Dictionary<string, long> PlaySeconds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Argumentos de lanzamiento por juego.</summary>
    public Dictionary<string, string> LaunchArgs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Juegos que se lanzan como administrador.</summary>
    public List<string> RunAsAdmin { get; set; } = new();
}

public class LinkOverride
{
    /// <summary>true = no vincular a ningún repositorio (sin actualizaciones).</summary>
    public bool Unlinked { get; set; }
    /// <summary>folderName de la app de Quiver elegida (opcional).</summary>
    public string? QuiverFolderName { get; set; }
    public string? Repository { get; set; }
    public string? Provider { get; set; }
    public string? AssetFilter { get; set; }
    /// <summary>Confirmado por huella (SHA-256 del paquete o CRC32 del ejecutable frente al release).</summary>
    public bool Verified { get; set; }
    public string? VerifiedNote { get; set; }
}

/// <summary>
/// Configuración en %APPDATA%\RecompVault\config.json.
/// Todas las lecturas/escrituras pasan por <see cref="Sync"/> para que los hilos de portadas,
/// actualizaciones y la interfaz no corrompan el fichero ni los diccionarios.
/// </summary>
public static class ConfigService
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RecompVault");
    private static readonly string FilePath = Path.Combine(Dir, "config.json");

    /// <summary>Bloqueo compartido para mutar AppConfig desde cualquier hilo.</summary>
    public static readonly object Sync = new();

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip };

    public static AppConfig Load()
    {
        lock (Sync)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts);
                    if (cfg != null) return Normalize(cfg);
                }
            }
            catch (Exception ex)
            {
                // No machacar la configuración del usuario: se guarda una copia del fichero dañado.
                try { File.Copy(FilePath, FilePath + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true); } catch { }
                Log.Error("Config corrupta, se carga la de por defecto", ex);
            }
            return new AppConfig();
        }
    }

    /// <summary>Los diccionarios deserializados pierden el comparador; se reconstruyen sin distinguir mayúsculas.</summary>
    private static AppConfig Normalize(AppConfig c)
    {
        c.Favorites ??= new(); c.Hidden ??= new();
        c.LaunchOverrides = new(c.LaunchOverrides ?? new(), StringComparer.OrdinalIgnoreCase);
        c.CoverOverrides = new(c.CoverOverrides ?? new(), StringComparer.OrdinalIgnoreCase);
        c.NameOverrides = new(c.NameOverrides ?? new(), StringComparer.OrdinalIgnoreCase);
        c.LastPlayed = new(c.LastPlayed ?? new(), StringComparer.OrdinalIgnoreCase);
        c.LinkOverrides = new(c.LinkOverrides ?? new(), StringComparer.OrdinalIgnoreCase);
        c.PinnedVersions = new(c.PinnedVersions ?? new(), StringComparer.OrdinalIgnoreCase);
        c.ProtectedByGame = new(c.ProtectedByGame ?? new(), StringComparer.OrdinalIgnoreCase);
        c.PlaySeconds = new(c.PlaySeconds ?? new(), StringComparer.OrdinalIgnoreCase);
        c.LaunchArgs = new(c.LaunchArgs ?? new(), StringComparer.OrdinalIgnoreCase);
        c.RunAsAdmin ??= new();
        c.ProtectedPatterns ??= RecompVault.Core.GlobMatcher.DefaultProtected.ToList();
        if (c.UndoBackupsToKeep < 1) c.UndoBackupsToKeep = 1;
        c.Favorites = c.Favorites.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        c.Hidden = c.Hidden.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (c.UpdateCheckIntervalHours < 0) c.UpdateCheckIntervalHours = 0;
        return c;
    }

    /// <summary>Guardado atómico (fichero temporal + reemplazo) y serializado bajo bloqueo.</summary>
    public static void Save(AppConfig cfg)
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var json = JsonSerializer.Serialize(cfg, JsonOpts);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null, true);
                else File.Move(tmp, FilePath);
            }
            catch (Exception ex) { Log.Error("No se pudo guardar la configuración", ex); }
        }
    }

    /// <summary>Modifica y guarda en una sola operación protegida.</summary>
    public static void Update(AppConfig cfg, Action<AppConfig> change)
    {
        lock (Sync)
        {
            change(cfg);
            Save(cfg);
        }
    }

    public static bool ListContains(AppConfig cfg, List<string> list, string value)
    {
        lock (Sync) return list.Contains(value, StringComparer.OrdinalIgnoreCase);
    }

    public static string? Get(Dictionary<string, string> dict, string key)
    {
        lock (Sync) return dict.TryGetValue(key, out var v) ? v : null;
    }
}

/// <summary>Registro sencillo en %APPDATA%\RecompVault\recompvault.log (rota a los 2 MB).</summary>
public static class Log
{
    private static readonly object Gate = new();
    public static readonly string FilePath = Path.Combine(ConfigService.Dir, "recompvault.log");

    public static void Info(string msg) => Write("INFO ", msg);
    public static void Warn(string msg) => Write("WARN ", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex == null ? msg : msg + ": " + ex);

    private static void Write(string level, string msg)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(ConfigService.Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 2_000_000)
                {
                    File.Copy(FilePath, FilePath + ".1", true);
                    File.WriteAllText(FilePath, "");
                }
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {msg}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
