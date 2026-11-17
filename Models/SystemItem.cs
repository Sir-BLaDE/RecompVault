using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace RecompVault.Models;

public class SystemItem : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "🎮";
    public string Accent { get; set; } = "#7C5CFF";
    public int Count { get; set; }

    private ImageSource? _logoImage;
    public ImageSource? LogoImage { get => _logoImage; set { _logoImage = value; OnPropertyChanged(); } }

    private bool _hasLogo;
    public bool HasLogo { get => _hasLogo; set { _hasLogo = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowBadge)); } }

    public bool ShowBadge => !HasLogo;

    public static SystemItem FromName(string name, int count = 0)
    {
        var (icon, accent) = name switch
        {
            "Todos" => ("★", "#7C5CFF"),
            "3DS" => ("3D", "#D32F2F"),
            "Arcade" => ("AR", "#FF9800"),
            "DS" => ("DS", "#607D8B"),
            "GBA" => ("GB", "#7E57C2"),
            "GC" => ("GC", "#6A1B9A"),
            "MEGADRIVE" => ("MD", "#1565C0"),
            "N64" => ("64", "#2E7D32"),
            "PC" => ("PC", "#546E7A"),
            "PCE" => ("PC", "#EF6C00"),
            "PS2" => ("PS", "#283593"),
            "PS4" => ("PS", "#003791"),
            "PSX" => ("PS", "#37474F"),
            "SNES" => ("SN", "#8E24AA"),
            "SWITCH" => ("SW", "#E60012"),
            "SWITCH 2" => ("S2", "#C62828"),
            "WII" => ("WI", "#00ACC1"),
            "WIIU" => ("WU", "#0097A7"),
            "X360" => ("X3", "#107C10"),
            "XBOX" => ("XB", "#2E7D32"),
            _ => (name.Length >= 2 ? name.Substring(0, 2).ToUpperInvariant() : name.ToUpperInvariant(), "#7C5CFF")
        };
        return new SystemItem { Name = name, Icon = icon, Accent = accent, Count = count };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public override string ToString() => Name;
}