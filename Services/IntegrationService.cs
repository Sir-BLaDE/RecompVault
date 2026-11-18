using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Win32;
using RecompVault.Core;
using RecompVault.Models;

namespace RecompVault.Services;

/// <summary>Accesos directos del escritorio y juegos no-Steam.</summary>
public static class IntegrationService
{
    // ------------------------------------------------------------------ escritorio

    public static string? CreateDesktopShortcut(GameEntry g, AppConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(g.LaunchPath) || !File.Exists(g.LaunchPath)) return "El juego no tiene ejecutable.";
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var lnk = Path.Combine(desktop, PathUtil.SafeFileName(g.Name, 100) + ".lnk");
            var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell no disponible");
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic sc = shell.CreateShortcut(lnk);
            sc.TargetPath = g.LaunchPath;
            sc.WorkingDirectory = g.LaunchDir;
            sc.Arguments = ConfigService.Get(cfg.LaunchArgs, g.FolderPath) ?? "";
            sc.IconLocation = g.LaunchPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? g.LaunchPath + ",0" : "";
            sc.Description = $"{g.Name} ({g.System}) — RecompVault";
            sc.Save();
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("Acceso directo", ex);
            return ex.Message;
        }
    }

    // ------------------------------------------------------------------ Steam

    public sealed record SteamUser(string Id, string Name, string ConfigDir);

    public static string? SteamPath()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var p = k?.GetValue("SteamPath") as string;
            if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p)) return Path.GetFullPath(p);
        }
        catch { }
        var def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Directory.Exists(def) ? def : null;
    }

    public static bool SteamRunning() => Process.GetProcessesByName("steam").Length > 0;

    /// <summary>Usuarios de Steam con carpeta en userdata (con nombre si aparece en loginusers.vdf).</summary>
    public static List<SteamUser> SteamUsers()
    {
        var list = new List<SteamUser>();
        var steam = SteamPath();
        if (steam == null) return list;
        var names = new Dictionary<string, string>();
        try
        {
            // loginusers.vdf (texto): SteamID64 → PersonaName; userdata usa el AccountID (32 bits)
            var login = Path.Combine(steam, "config", "loginusers.vdf");
            if (File.Exists(login))
            {
                string? current = null;
                foreach (var raw in File.ReadAllLines(login))
                {
                    var l = raw.Trim();
                    if (l.StartsWith("\"7656")) current = l.Trim('"');
                    else if (current != null && l.StartsWith("\"PersonaName\""))
                    {
                        var parts = l.Split('"', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 3 && ulong.TryParse(current, out var id64))
                            names[(id64 - 76561197960265728UL).ToString()] = parts[^1];
                    }
                }
            }
        }
        catch { }
        foreach (var d in QuiverLibrary.SafeDirs(Path.Combine(steam, "userdata")))
        {
            var id = Path.GetFileName(d);
            if (!id.All(char.IsDigit) || id == "0") continue;
            list.Add(new SteamUser(id, names.TryGetValue(id, out var n) ? n : "usuario " + id, Path.Combine(d, "config")));
        }
        return list.OrderByDescending(u => Directory.GetLastWriteTimeUtc(u.ConfigDir)).ToList();
    }

    /// <summary>Añade el juego a shortcuts.vdf y copia la portada a la carpeta grid. Devuelve error o null.</summary>
    public static string? AddToSteam(GameEntry g, AppConfig cfg, SteamUser user)
    {
        if (string.IsNullOrWhiteSpace(g.LaunchPath) || !File.Exists(g.LaunchPath)) return "El juego no tiene ejecutable.";
        try
        {
            Directory.CreateDirectory(user.ConfigDir);
            var vdf = Path.Combine(user.ConfigDir, "shortcuts.vdf");
            var existing = File.Exists(vdf) ? File.ReadAllBytes(vdf) : null;
            if (existing != null) File.Copy(vdf, vdf + ".recompvault.bak", true); // copia de seguridad
            var icon = g.LaunchPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? g.LaunchPath : "";
            var (data, appId, updated) = SteamShortcuts.AddOrUpdate(existing, g.Name, g.LaunchPath, g.LaunchDir, icon,
                ConfigService.Get(cfg.LaunchArgs, g.FolderPath) ?? "", new[] { "RecompVault", g.System });
            File.WriteAllBytes(vdf, data);

            // portada vertical para la biblioteca de Steam
            var cover = ConfigService.Get(cfg.CoverOverrides, g.FolderPath);
            if (cover != null && File.Exists(cover))
            {
                var grid = Path.Combine(user.ConfigDir, "grid");
                Directory.CreateDirectory(grid);
                File.Copy(cover, Path.Combine(grid, $"{appId}p{Path.GetExtension(cover)}"), true);
            }
            Log.Info($"Steam: {(updated ? "actualizado" : "añadido")} {g.Name} (appid {appId}) para {user.Name}");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("Añadir a Steam", ex);
            return ex.Message;
        }
    }
}

/// <summary>Avisa de versiones nuevas de RecompVault publicadas en GitHub Releases.</summary>
public static class SelfUpdateService
{
    public const string Repo = "Sir-BLaDE/RecompVault";

    public static string CurrentVersion => typeof(SelfUpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static string ReleasesUrl => $"https://github.com/{Repo}/releases";

    public sealed record AppRelease(string Tag, string Url, string Notes, DateTime? Published, bool IsNewer);

    /// <summary>Último release publicado en GitHub (null si no hay red o falla la API).</summary>
    public static async Task<AppRelease?> LatestAsync(string? token = null)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RecompVault/" + CurrentVersion);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            if (!string.IsNullOrWhiteSpace(token)) http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Trim());
            using var resp = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            if (!resp.IsSuccessStatusCode) { Log.Warn($"Actualización de RecompVault: GitHub respondió {(int)resp.StatusCode}"); return null; }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var tag = doc.RootElement.Str("tag_name");
            if (tag == null) return null;
            var url = doc.RootElement.Str("html_url") ?? ReleasesUrl;
            var notes = doc.RootElement.Str("body") ?? "";
            DateTime? pub = DateTime.TryParse(doc.RootElement.Str("published_at"), out var p) ? p.ToLocalTime() : null;
            return new AppRelease(tag, url, notes, pub, VersionUtil.IsNewer(tag, CurrentVersion));
        }
        catch (Exception ex) { Log.Warn($"Actualización de RecompVault: {ex.Message}"); return null; }
    }

    /// <summary>
    /// Avisa de la versión nueva y, si el usuario quiere, abre la página del release en GitHub.
    /// No descarga ni sustituye nada: la actualización la hace el usuario.
    /// </summary>
    /// <returns>true si se abrió GitHub.</returns>
    public static bool Prompt(AppRelease r, AppConfig cfg, bool manual)
    {
        var notes = r.Notes.Length > 4000 ? r.Notes[..4000] + "\n…" : r.Notes;
        var when = r.Published is { } d ? $" (publicada el {d:dd/MM/yyyy})" : "";
        var msg = $"Hay una versión nueva de RecompVault: {r.Tag}{when}.\nTienes la {CurrentVersion}.\n\n" +
                  "Se abrirá la página del release en GitHub para que descargues RecompVault.exe y sustituyas el actual " +
                  "(cierra RecompVault antes de reemplazarlo). Tus ajustes están en %APPDATA%\\RecompVault y no se pierden.";
        var options = manual ? new[] { "Abrir GitHub", "Ahora no" } : new[] { "Abrir GitHub", "Más tarde", "Omitir esta versión" };
        var choice = DialogService.Choose("Actualización de RecompVault", msg, string.IsNullOrWhiteSpace(notes) ? null : notes, options);
        if (choice == 0) { LauncherService.OpenUrl(r.Url); return true; }
        if (choice == 2) ConfigService.Update(cfg, c => c.SkippedAppVersion = r.Tag);
        return false;
    }
}
