using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
namespace RecompVault.Models;

public class GameEntry : INotifyPropertyChanged
{
    private string _name = "";
    public string Name { get => _name; set { _name = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); } }

    private string _recompName = "";
    public string RecompName { get => _recompName; set { _recompName = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasRecompName)); } }

    public bool HasRecompName => !string.IsNullOrWhiteSpace(RecompName) && !RecompName.Equals(Name, StringComparison.OrdinalIgnoreCase);

    private string _system = "";
    public string System { get => _system; set { _system = value; OnPropertyChanged(); } }

    private string _folderPath = "";
    public string FolderPath { get => _folderPath; set { _folderPath = value; OnPropertyChanged(); } }

    private string _launchPath = "";
    public string LaunchPath { get => _launchPath; set { _launchPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanLaunch)); } }

    public string? Notes { get; set; }

    private bool _favorite;
    public bool Favorite { get => _favorite; set { _favorite = value; OnPropertyChanged(); } }

    private bool _isHidden;
    public bool IsHidden { get => _isHidden; set { _isHidden = value; OnPropertyChanged(); } }

    public DateTime LastPlayed { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Path.GetFileName(FolderPath) : Name;

    public bool CanLaunch => !string.IsNullOrWhiteSpace(LaunchPath) && File.Exists(LaunchPath);
    public string SystemInitial => string.IsNullOrWhiteSpace(System) ? "?" : System.Substring(0, 1).ToUpperInvariant();

    private ImageSource? _coverImage;
    public ImageSource? CoverImage { get => _coverImage; set { _coverImage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasCover)); } }

    private ImageSource? _logoImage;
    public ImageSource? LogoImage { get => _logoImage; set { _logoImage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasLogo)); } }

    public bool HasCover => CoverImage != null;
    public bool HasLogo => LogoImage != null;

    public string LaunchDir => string.IsNullOrEmpty(LaunchPath) ? FolderPath : Path.GetDirectoryName(LaunchPath)!;

    // Quiver / update metadata
    public string? Repository { get; set; }
    public string? Project { get; set; }
    public string? QuiverFolderName { get; set; }
    public string? QuiverAppIconUrl { get; set; }
    public string? QuiverLocalIconPath { get; set; }

    private string _installedVersion = "";
    public string InstalledVersion { get => _installedVersion; set { _installedVersion = value; OnPropertyChanged(); OnPropertyChanged(nameof(VersionInfo)); OnPropertyChanged(nameof(HasVersionInfo)); } }

    private string _latestVersion = "";
    public string LatestVersion { get => _latestVersion; set { _latestVersion = value; OnPropertyChanged(); OnPropertyChanged(nameof(VersionInfo)); OnPropertyChanged(nameof(HasUpdate)); OnPropertyChanged(nameof(HasVersionInfo)); } }

    public bool HasUpdate => !string.IsNullOrWhiteSpace(LatestVersion) && !string.IsNullOrWhiteSpace(InstalledVersion) &&
        !SameVer(LatestVersion, InstalledVersion);
    public bool HasVersionInfo => !string.IsNullOrWhiteSpace(InstalledVersion) || !string.IsNullOrWhiteSpace(LatestVersion);
    public string VersionInfo => HasUpdate ? $"{InstalledVersion} → {LatestVersion}" : (!string.IsNullOrWhiteSpace(InstalledVersion) ? InstalledVersion : (LatestVersion ?? ""));

    private static bool SameVer(string a, string b)
    {
        static string N(string s){ s=(s??"").Trim(); if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s=s[1..]; return s; }
        var na=N(a); var nb=N(b);
        if (na.Equals(nb, StringComparison.OrdinalIgnoreCase)) return true;
        if (b == "instalada") return false; // placeholder means unknown baseline but update flagged
        return false;
    }

    public string? UpdateAssetUrl { get; set; }
    public string? UpdateAssetName { get; set; }
    public string? UpdateAssetDigest { get; set; }

    private bool _isUpdating;
    public bool IsUpdating { get => _isUpdating; set { _isUpdating = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}