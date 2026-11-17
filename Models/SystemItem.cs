using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RecompVault.Models;

/// <summary>Datos de presentación de un sistema (nombre completo, fabricante, año y colores de marca).</summary>
public sealed record SystemInfo(string FullName, string Maker, string Year, string Color1, string Color2, string Short)
{
    private static readonly Dictionary<string, SystemInfo> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Todos"] = new("Todos los juegos", "RecompVault", "", "#D0342C", "#2A0B09", "★"),
        ["N64"] = new("Nintendo 64", "Nintendo", "1996", "#1E8E3E", "#0A2E14", "64"),
        ["SNES"] = new("Super Nintendo", "Nintendo", "1990", "#7B3FB5", "#1F0E33", "SFC"),
        ["NES"] = new("Nintendo Entertainment System", "Nintendo", "1983", "#B71C1C", "#2B0707", "NES"),
        ["GB"] = new("Game Boy", "Nintendo", "1989", "#8BAC0F", "#1E2A06", "GB"),
        ["GBC"] = new("Game Boy Color", "Nintendo", "1998", "#6A1B9A", "#1E0A2C", "GBC"),
        ["GBA"] = new("Game Boy Advance", "Nintendo", "2001", "#4F3BB5", "#140F3A", "GBA"),
        ["GC"] = new("Nintendo GameCube", "Nintendo", "2001", "#6941A8", "#1A0F2E", "GC"),
        ["DS"] = new("Nintendo DS", "Nintendo", "2004", "#8796A5", "#1E252C", "DS"),
        ["3DS"] = new("Nintendo 3DS", "Nintendo", "2011", "#CE1E26", "#33070A", "3DS"),
        ["WII"] = new("Wii", "Nintendo", "2006", "#2AA6DA", "#062B3A", "Wii"),
        ["WIIU"] = new("Wii U", "Nintendo", "2012", "#0098C8", "#002A38", "Wii U"),
        ["SWITCH"] = new("Nintendo Switch", "Nintendo", "2017", "#E60012", "#3A0006", "NS"),
        ["SWITCH 2"] = new("Nintendo Switch 2", "Nintendo", "2025", "#E2001A", "#1A1A1F", "NS2"),
        ["PSX"] = new("PlayStation", "Sony", "1994", "#6C7A89", "#161C24", "PS1"),
        ["PS2"] = new("PlayStation 2", "Sony", "2000", "#2747A8", "#0A1230", "PS2"),
        ["PS3"] = new("PlayStation 3", "Sony", "2006", "#3B3B44", "#0B0B0E", "PS3"),
        ["PS4"] = new("PlayStation 4", "Sony", "2013", "#0050D0", "#00143A", "PS4"),
        ["PSP"] = new("PlayStation Portable", "Sony", "2004", "#4A4A55", "#111116", "PSP"),
        ["XBOX"] = new("Xbox", "Microsoft", "2001", "#3C9A2E", "#0C2409", "XB"),
        ["X360"] = new("Xbox 360", "Microsoft", "2005", "#7CC242", "#16300A", "360"),
        ["MEGADRIVE"] = new("Mega Drive / Genesis", "Sega", "1988", "#1F5FC4", "#081936", "MD"),
        ["SATURN"] = new("Sega Saturn", "Sega", "1994", "#3B4F8F", "#0D1226", "SAT"),
        ["DREAMCAST"] = new("Dreamcast", "Sega", "1998", "#F26B1D", "#3A1606", "DC"),
        ["PCE"] = new("PC Engine / TurboGrafx-16", "NEC", "1987", "#F07C1A", "#3A1A04", "PCE"),
        ["Arcade"] = new("Arcade", "Recreativas", "", "#F5A623", "#3A2504", "ARC"),
        ["PC"] = new("PC", "Windows", "", "#4C6B82", "#0E1820", "PC"),
        ["Quiver"] = new("Quiver Launcher", "Apps fuera de la carpeta raíz", "", "#7C5CFF", "#1A1240", "Q"),
    };

    public static SystemInfo For(string name) =>
        Known.TryGetValue(name ?? "", out var i) ? i
            : new SystemInfo(name ?? "?", "", "", "#5B6B86", "#141A26", (name ?? "?").Length > 3 ? name![..3].ToUpperInvariant() : (name ?? "?").ToUpperInvariant());
}

public class SystemItem : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Maker { get; set; } = "";
    public string Year { get; set; } = "";
    /// <summary>Línea secundaria del héroe: "Nintendo · 1996".</summary>
    public string Subtitle => string.Join(" · ", new[] { Maker, Year }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string Icon { get; set; } = "🎮";
    /// <summary>Color principal de marca (#RRGGBB).</summary>
    public string Accent { get; set; } = "#7C5CFF";
    /// <summary>Color oscuro para el degradado.</summary>
    public string Accent2 { get; set; } = "#141A26";
    public int Count { get; set; }
    public int UpdateCount { get; set; }
    public string CountLabel => Count == 1 ? "1 juego" : $"{Count} juegos";

    private ImageSource? _logoImage;
    public ImageSource? LogoImage { get => _logoImage; set { _logoImage = value; OnPropertyChanged(); OnPropertyChanged(nameof(LogoBrush)); } }

    /// <summary>Pincel con el logo, para usarlo como máscara (logo blanco tipo frontend).</summary>
    public ImageBrush? LogoBrush => LogoImage == null ? null : Freeze(new ImageBrush(LogoImage) { Stretch = Stretch.Uniform });

    private bool _hasLogo;
    public bool HasLogo { get => _hasLogo; set { _hasLogo = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowBadge)); OnPropertyChanged(nameof(ShowSilhouette)); OnPropertyChanged(nameof(ShowColorLogo)); } }

    private bool _logoHasAlpha = true;
    /// <summary>El logo tiene transparencia: se puede pintar en blanco con máscara.</summary>
    public bool LogoHasAlpha { get => _logoHasAlpha; set { _logoHasAlpha = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowSilhouette)); OnPropertyChanged(nameof(ShowColorLogo)); } }

    public bool ShowBadge => !HasLogo;
    public bool ShowSilhouette => HasLogo && LogoHasAlpha;
    public bool ShowColorLogo => HasLogo && !LogoHasAlpha;

    public static SystemItem FromName(string name, int count = 0)
    {
        var info = SystemInfo.For(name);
        return new SystemItem
        {
            Name = name, FullName = info.FullName, Maker = info.Maker, Year = info.Year,
            Icon = info.Short, Accent = info.Color1, Accent2 = info.Color2, Count = count
        };
    }

    public static bool HasAlpha(ImageSource? img) =>
        img is BitmapSource b && (b.Format == PixelFormats.Bgra32 || b.Format == PixelFormats.Pbgra32 || b.Format == PixelFormats.Prgba64 ||
                                  b.Format == PixelFormats.Rgba64 || b.Format == PixelFormats.Indexed8 || b.Format == PixelFormats.Indexed4 ||
                                  b.Format == PixelFormats.Indexed2 || b.Format == PixelFormats.Indexed1 || b.Format == PixelFormats.Prgba128Float ||
                                  b.Format == PixelFormats.Rgba128Float);

    private static ImageBrush Freeze(ImageBrush b) { b.Freeze(); return b; }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public override string ToString() => Name;
}
