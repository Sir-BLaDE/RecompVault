using System.IO;
using RecompVault.Core;
using RecompVault.Models;

namespace RecompVault.Services;

/// <summary>Puente entre <see cref="QuiverLibrary"/> (núcleo) y los modelos de la interfaz.</summary>
public static class QuiverService
{
    /// <summary>Ruta de Quiver detectada (y guardada en la config si se autodetectó). Null si no hay.</summary>
    public static string? ResolvePath(AppConfig cfg)
    {
        string configured, root;
        lock (ConfigService.Sync) { configured = cfg.QuiverPath; root = cfg.RootPath; }
        var path = QuiverLibrary.Detect(configured, root);
        if (path != null && !string.Equals(path, configured, StringComparison.OrdinalIgnoreCase))
            ConfigService.Update(cfg, c => c.QuiverPath = path);
        return path;
    }

    public static QuiverLibrary? Load(AppConfig cfg)
    {
        if (!cfg.QuiverSyncEnabled) return null;
        try { return QuiverLibrary.Load(ResolvePath(cfg)); }
        catch (Exception ex) { Log.Error("Error leyendo Quiver", ex); return null; }
    }

    /// <summary>Aplica nombre, repositorio, icono y versión conocida por Quiver a la entrada.</summary>
    public static void Enrich(GameEntry g, QuiverMatch m, AppConfig cfg, bool allowRename = true)
    {
        var app = m.App;
        var hasCustomName = ConfigService.Get(cfg.NameOverrides, g.FolderPath) != null;
        if (app != null)
        {
            if (allowRename && !hasCustomName && !string.IsNullOrWhiteSpace(app.Name)) { g.Name = CleanQuiverName(app.Name); g.NameSource = "Quiver (apps.json)"; }
            g.QuiverFolderName = app.FolderName;
            g.QuiverAppIconUrl = string.IsNullOrWhiteSpace(app.AppIconUrl) ? null : app.AppIconUrl;
            if (!string.IsNullOrWhiteSpace(app.CustomIconPath) && File.Exists(app.CustomIconPath)) g.QuiverLocalIconPath = app.CustomIconPath;
            if (!string.IsNullOrWhiteSpace(app.Project)) g.Project = app.Project;
        }
        else if (m.Catalog != null)
        {
            g.QuiverFolderName ??= m.Catalog.FolderName;
        }
        var repo = m.Repository;
        if (!string.IsNullOrWhiteSpace(repo)) { g.Repository = repo; g.Provider = m.Provider; }
        if (!string.IsNullOrWhiteSpace(m.AssetFilter)) g.AssetFilter = m.AssetFilter;
        g.LinkSource = string.IsNullOrWhiteSpace(m.Kind) ? "Quiver" : m.Kind;
    }

    /// <summary>Quita el vínculo con Quiver/repositorio.</summary>
    public static void Unlink(GameEntry g)
    {
        g.Repository = null; g.Provider = "github"; g.AssetFilter = null; g.Project = null;
        g.QuiverFolderName = null; g.QuiverAppIconUrl = null; g.QuiverLocalIconPath = null;
        g.UpdateAssetUrl = null; g.UpdateAssetName = null; g.UpdateAssetDigest = null;
        g.LatestVersion = "";
    }

    /// <summary>Aplica el vínculo elegido a mano en la ventana "Detección" (tiene prioridad sobre lo automático).</summary>
    public static void ApplyLinkOverride(GameEntry g, QuiverLibrary? lib, AppConfig cfg)
    {
        LinkOverride? ov;
        lock (ConfigService.Sync) cfg.LinkOverrides.TryGetValue(g.FolderPath, out ov);
        if (ov == null) return;
        if (ov.Unlinked) { Unlink(g); g.LinkSource = "Manual: sin vincular"; return; }

        var app = !string.IsNullOrWhiteSpace(ov.QuiverFolderName) ? lib?.AppByFolderName(ov.QuiverFolderName!) : null;
        if (app != null || !string.IsNullOrWhiteSpace(ov.Repository)) Unlink(g);
        if (app != null) Enrich(g, new QuiverMatch(app, lib!.CatalogFor(app), "Manual: app de Quiver elegida"), cfg);
        if (!string.IsNullOrWhiteSpace(ov.Repository))
        {
            g.Repository = ov.Repository!.Trim().Trim('/');
            g.Provider = string.IsNullOrWhiteSpace(ov.Provider) ? "github" : ov.Provider!.ToLowerInvariant();
            g.LinkSource = app != null ? "Manual: app de Quiver + repositorio" : "Manual: repositorio";
        }
        if (ov.AssetFilter != null) g.AssetFilter = string.IsNullOrWhiteSpace(ov.AssetFilter) ? null : ov.AssetFilter.Trim();
        if (ov.Verified) g.LinkSource = "Verificado por huella — " + (ov.VerifiedNote ?? "");
    }

    /// <summary>Aplica la última versión conocida por Quiver (sin red). Nunca toca la instalada.</summary>
    public static void ApplyKnownLatest(GameEntry g, QuiverLibrary? lib)
    {
        if (lib == null || string.IsNullOrWhiteSpace(g.Repository)) return;
        var latest = lib.LatestKnown(g.Provider, g.Repository);
        if (latest == null) return;
        // si ya tenemos una versión más nueva (p.ej. de GitHub), no retroceder
        if (!string.IsNullOrWhiteSpace(g.LatestVersion) && !VersionUtil.IsNewer(latest, g.LatestVersion)) return;
        UiThread.RunSync(() => g.LatestVersion = latest);
        var rel = lib.Release(g.Provider, g.Repository);
        if (rel != null && VersionUtil.Equivalent(rel.Version, latest))
        {
            var a = ReleaseAssetPicker.Pick(rel.Assets, g.AssetFilter, g.InstalledAssetName);
            if (a != null) { g.UpdateAssetUrl = a.Url; g.UpdateAssetName = a.Name; g.UpdateAssetDigest = a.Digest; }
        }
    }

    /// <summary>Quiver añade sufijos " (Recomp)", " (Decomp)"... al nombre: se quitan para el título.</summary>
    private static string CleanQuiverName(string name)
    {
        var n = name.Trim();
        foreach (var suffix in new[] { " (Recomp)", " (Decomp)", " (Recreation)", " (Port)", " (Remake)" })
            if (n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return n[..^suffix.Length].Trim();
        return n;
    }
}
