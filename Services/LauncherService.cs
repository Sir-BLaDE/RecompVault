using System.Diagnostics;
using System.IO;
using RecompVault.Models;

namespace RecompVault.Services;

public static class LauncherService
{
    public static void Launch(GameEntry g)
    {
        if (string.IsNullOrWhiteSpace(g.LaunchPath) || !File.Exists(g.LaunchPath)) return;
        var psi = new ProcessStartInfo
        {
            FileName = g.LaunchPath,
            WorkingDirectory = g.LaunchDir,
            UseShellExecute = true
        };
        Process.Start(psi);
        g.LastPlayed = DateTime.Now;
    }

    public static void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) return;
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }
}