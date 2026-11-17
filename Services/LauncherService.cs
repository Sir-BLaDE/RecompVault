using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using RecompVault.Models;

namespace RecompVault.Services;

public static class LauncherService
{
    /// <summary>Lanza el juego. Devuelve null si va bien o el motivo del fallo (nunca lanza excepción).</summary>
    public static string? Launch(GameEntry g, AppConfig cfg, out Process? process)
    {
        process = null;
        if (string.IsNullOrWhiteSpace(g.LaunchPath) || !File.Exists(g.LaunchPath))
            return "No se encuentra el ejecutable. Elige uno con ℹ (Detección).";
        try
        {
            var args = ConfigService.Get(cfg.LaunchArgs, g.FolderPath);
            var admin = ConfigService.ListContains(cfg, cfg.RunAsAdmin, g.FolderPath);
            var psi = new ProcessStartInfo
            {
                FileName = g.LaunchPath,
                WorkingDirectory = g.LaunchDir,
                UseShellExecute = true,
                Arguments = args ?? ""
            };
            if (admin) psi.Verb = "runas";
            process = Process.Start(psi);
            g.LastPlayed = DateTime.Now;
            WriteQuiverLastPlayed(g.FolderPath, g.LastPlayed);
            Log.Info($"Lanzado {g.Name}: {g.LaunchPath} {args}{(admin ? " (admin)" : "")}");
            return null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "Lanzamiento cancelado (permiso de administrador denegado).";
        }
        catch (Exception ex)
        {
            Log.Error($"No se pudo lanzar {g.Name}", ex);
            return ex.Message;
        }
    }

    /// <summary>LastPlayed.txt con el formato de Quiver ("yyyy-MM-dd HH:mm:ss"), para que ambos launchers coincidan.</summary>
    public static void WriteQuiverLastPlayed(string folder, DateTime when)
    {
        try { if (Directory.Exists(folder)) File.WriteAllText(Path.Combine(folder, "LastPlayed.txt"), when.ToString("yyyy-MM-dd HH:mm:ss")); }
        catch { }
    }

    public static DateTime? ReadQuiverLastPlayed(string folder)
    {
        try
        {
            var p = Path.Combine(folder, "LastPlayed.txt");
            if (!File.Exists(p)) return null;
            return DateTime.TryParseExact(File.ReadAllText(p).Trim(), "yyyy-MM-dd HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Mide cuánto tiempo está abierto el juego: el proceso lanzado y cualquier proceso cuyo ejecutable
    /// esté dentro de la carpeta del juego (los launchers suelen abrir otro proceso y cerrarse).
    /// Devuelve los segundos jugados.
    /// </summary>
    public static async Task<long> TrackSessionAsync(GameEntry g, Process? started, CancellationToken ct = default)
    {
        var start = DateTime.Now;
        var lastSeen = start;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                bool alive = false;
                try { alive = started != null && !started.HasExited; } catch { }
                if (!alive) alive = UpdateService.RunningProcessesIn(g.FolderPath).Count > 0;
                if (!alive) break;
                lastSeen = DateTime.Now;
                if ((lastSeen - start).TotalHours > 24) break; // salvaguarda
                await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally { try { started?.Dispose(); } catch { } }
        var secs = (long)(lastSeen - start).TotalSeconds;
        return secs < 15 ? 0 : secs;
    }

    public static string? OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return "La carpeta no existe.";
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { }
    }
}
