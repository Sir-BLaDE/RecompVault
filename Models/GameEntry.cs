using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using RecompVault.Core;

namespace RecompVault.Models;

public class GameEntry : INotifyPropertyChanged
{
    private string _name = "";
    public string Name { get => _name; set { if (Set(ref _name, value)) { OnPropertyChanged(nameof(DisplayName)); OnPropertyChanged(nameof(HasRecompName)); } } }

    private string _recompName = "";
    public string RecompName { get => _recompName; set { if (Set(ref _recompName, value)) OnPropertyChanged(nameof(HasRecompName)); } }

    public bool HasRecompName => !string.IsNullOrWhiteSpace(RecompName) && !RecompName.Equals(Name, StringComparison.OrdinalIgnoreCase);

    private string _system = "";
    public string System { get => _system; set { if (Set(ref _system, value)) OnPropertyChanged(nameof(SystemInitial)); } }

    private string _folderPath = "";
    public string FolderPath { get => _folderPath; set => Set(ref _folderPath, value); }

    private string _launchPath = "";
    public string LaunchPath { get => _launchPath; set { if (Set(ref _launchPath, value)) { OnPropertyChanged(nameof(CanLaunch)); OnPropertyChanged(nameof(LaunchDir)); } } }

    public string? Notes { get; set; }

    private bool _favorite;
    public bool Favorite { get => _favorite; set => Set(ref _favorite, value); }

    private bool _isHidden;
    public bool IsHidden { get => _isHidden; set => Set(ref _isHidden, value); }

    public DateTime LastPlayed { get; set; }

    /// <summary>El nombre viene de gametitles.json (más fiable que una coincidencia difusa con Quiver).</summary>
    public bool NameFromTitleMap { get; set; }

    // ---------------------------------------------------------------- origen de cada dato (ventana "Detección")
    public string NameSource { get; set; } = "";
    public string LaunchSource { get; set; } = "";
    private string _linkSource = "Sin vincular";
    public string LinkSource { get => _linkSource; set { if (Set(ref _linkSource, value ?? "")) { OnPropertyChanged(nameof(IsFuzzyLink)); OnPropertyChanged(nameof(ReviewFlags)); } } }

    private bool _hasUndo;
    /// <summary>Hay una copia para deshacer la última actualización hecha por RecompVault.</summary>
    public bool HasUndo { get => _hasUndo; set => Set(ref _hasUndo, value); }

    /// <summary>App instalada por Quiver fuera de la carpeta raíz (no se puede eliminar desde aquí).</summary>
    public bool IsExternal { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Path.GetFileName(FolderPath) : Name;

    public bool CanLaunch => !string.IsNullOrWhiteSpace(LaunchPath) && File.Exists(LaunchPath) && !IsUpdating;
    public string SystemInitial => string.IsNullOrWhiteSpace(System) ? "?" : System[..1].ToUpperInvariant();

    private ImageSource? _coverImage;
    public ImageSource? CoverImage { get => _coverImage; set { if (Set(ref _coverImage, value)) OnPropertyChanged(nameof(HasCover)); } }

    private ImageSource? _logoImage;
    public ImageSource? LogoImage { get => _logoImage; set { if (Set(ref _logoImage, value)) OnPropertyChanged(nameof(HasLogo)); } }

    public bool HasCover => CoverImage != null;
    public bool HasLogo => LogoImage != null;

    public string LaunchDir => string.IsNullOrEmpty(LaunchPath) ? FolderPath : (Path.GetDirectoryName(LaunchPath) ?? FolderPath);

    // ---------------------------------------------------------------- Quiver / actualizaciones
    public string? Repository { get; set; }
    /// <summary>"github" o "gitlab".</summary>
    public string Provider { get; set; } = "github";
    public string? Project { get; set; }
    public string? QuiverFolderName { get; set; }
    public string? QuiverAppIconUrl { get; set; }
    public string? QuiverLocalIconPath { get; set; }
    /// <summary>releaseAssetFilter de Quiver (repos con varios juegos).</summary>
    public string? AssetFilter { get; set; }
    /// <summary>Paquete instalado según installed-build.txt de Quiver (nombre de fichero).</summary>
    public string? InstalledAssetName { get; set; }

    private string _installedVersion = "";
    /// <summary>Versión instalada real (version.txt de la carpeta). Vacía = desconocida.</summary>
    public string InstalledVersion { get => _installedVersion; set { if (Set(ref _installedVersion, value ?? "")) RaiseVersion(); } }

    private string _latestVersion = "";
    public string LatestVersion { get => _latestVersion; set { if (Set(ref _latestVersion, value ?? "")) RaiseVersion(); } }

    private string _pinnedVersion = "";
    /// <summary>Versión fijada por el usuario: no se avisa ni se auto-actualiza.</summary>
    public string PinnedVersion { get => _pinnedVersion; set { if (Set(ref _pinnedVersion, value ?? "")) { OnPropertyChanged(nameof(IsPinned)); RaiseVersion(); } } }
    public bool IsPinned => !string.IsNullOrWhiteSpace(PinnedVersion);

    public bool HasUpdate => !IsPinned && VersionUtil.IsUpdate(InstalledVersion, LatestVersion);
    /// <summary>Hay una versión más nueva aunque esté fijada.</summary>
    public bool HasNewerThanInstalled => VersionUtil.IsUpdate(InstalledVersion, LatestVersion);
    public bool HasVersionInfo => !string.IsNullOrWhiteSpace(InstalledVersion) || !string.IsNullOrWhiteSpace(LatestVersion);
    public bool InstalledUnknown => string.IsNullOrWhiteSpace(InstalledVersion);

    public string VersionInfo
    {
        get
        {
            if (IsPinned) return $"📌 {(string.IsNullOrWhiteSpace(InstalledVersion) ? PinnedVersion : InstalledVersion)}" +
                                 (HasNewerThanInstalled ? $" (fijada; última {LatestVersion})" : " (fijada)");
            if (HasUpdate) return $"{InstalledVersion} → {LatestVersion}";
            if (!string.IsNullOrWhiteSpace(InstalledVersion))
                return string.IsNullOrWhiteSpace(LatestVersion) || VersionUtil.Equivalent(InstalledVersion, LatestVersion)
                    ? $"{InstalledVersion} ✓" : InstalledVersion;
            return string.IsNullOrWhiteSpace(LatestVersion) ? "" : $"Última: {LatestVersion} (instalada desconocida)";
        }
    }

    private void RaiseVersion()
    {
        OnPropertyChanged(nameof(HasUpdate)); OnPropertyChanged(nameof(HasVersionInfo));
        OnPropertyChanged(nameof(VersionInfo)); OnPropertyChanged(nameof(InstalledUnknown)); OnPropertyChanged(nameof(ReviewFlags));
    }

    /// <summary>Notas del último release (para confirmar antes de actualizar).</summary>
    public string? LatestNotes { get; set; }

    /// <summary>El vínculo con Quiver/repositorio es una coincidencia aproximada (no se auto-actualiza).</summary>
    public bool IsFuzzyLink => LinkSource.Contains("aproximada", StringComparison.OrdinalIgnoreCase);
    /// <summary>La carpeta la actualizó la versión anterior de RecompVault (sin copia para deshacer).</summary>
    private bool _updatedByOldVersion;
    public bool UpdatedByOldVersion { get => _updatedByOldVersion; set { if (Set(ref _updatedByOldVersion, value)) OnPropertyChanged(nameof(ReviewFlags)); } }

    /// <summary>Resumen de avisos para la revisión en bloque.</summary>
    public string ReviewFlags
    {
        get
        {
            var f = new List<string>();
            if (string.IsNullOrWhiteSpace(Repository)) f.Add("sin vincular");
            else if (IsFuzzyLink) f.Add("≈ aproximado");
            if (InstalledUnknown) f.Add("versión ?");
            if (HasUpdate) f.Add("actualización");
            if (IsPinned) f.Add("📌");
            if (UpdatedByOldVersion) f.Add("RV antiguo");
            if (string.IsNullOrWhiteSpace(LaunchPath)) f.Add("sin exe");
            return string.Join(" · ", f);
        }
    }

    /// <summary>Avisa de que algo del que depende ReviewFlags/Repository cambió.</summary>
    public void RaiseDetectionChanged()
    {
        OnPropertyChanged(nameof(Repository)); OnPropertyChanged(nameof(ReviewFlags)); OnPropertyChanged(nameof(LinkSource));
    }

    public string? UpdateAssetUrl { get; set; }
    public string? UpdateAssetName { get; set; }
    public string? UpdateAssetDigest { get; set; }

    private bool _isUpdating;
    public bool IsUpdating { get => _isUpdating; set { if (Set(ref _isUpdating, value)) OnPropertyChanged(nameof(CanLaunch)); } }

    /// <summary>Vuelve a evaluar CanLaunch (p.ej. tras actualizar ficheros en disco).</summary>
    public void RefreshLaunchState() => OnPropertyChanged(nameof(CanLaunch));

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(n);
        return true;
    }
}
