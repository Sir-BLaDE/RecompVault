using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using RecompVault.Models;

namespace RecompVault.Services;

public static class LauncherService
{
    /// <summary>Lanza el juego. Devuelve null si va bien o el motivo del fallo (nunca lanza excepción).</summary>
    public static string? Launch(GameEntry g)
    {
        if (string.IsNullOrWhiteSpace(g.LaunchPath) || !File.Exists(g.LaunchPath))
            return "No se encuentra el ejecutable. Elige uno con el botón ⚙.";
        try
        {
            var ext = Path.GetExtension(g.LaunchPath).ToLowerInvariant();
            var psi = new ProcessStartInfo
            {
                FileName = g.LaunchPath,
                WorkingDirectory = g.LaunchDir,
                UseShellExecute = true
            };
            Process.Start(psi);
            g.LastPlayed = DateTime.Now;
            Log.Info($"Lanzado {g.Name}: {g.LaunchPath}");
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
