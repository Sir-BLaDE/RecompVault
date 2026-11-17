namespace RecompVault.Core;

/// <summary>Traduce las consolas del catálogo de Quiver ("Nintendo 64", "PlayStation 2"...) a carpetas de sistema de RecompVault.</summary>
public static class SystemMapper
{
    private static readonly (string[] keys, string system)[] Rules =
    {
        (new[] { "switch2", "nintendoswitch2" }, "SWITCH 2"),
        (new[] { "switch", "nintendoswitch" }, "SWITCH"),
        (new[] { "wiiu" }, "WIIU"),
        (new[] { "wii" }, "WII"),
        (new[] { "gamecube", "ngc" }, "GC"),
        (new[] { "nintendo64", "n64" }, "N64"),
        (new[] { "supernintendo", "snes", "superfamicom", "sfc" }, "SNES"),
        (new[] { "gameboyadvance", "gba" }, "GBA"),
        (new[] { "gameboycolor", "gbc" }, "GBC"),
        (new[] { "gameboy", "gb" }, "GB"),
        (new[] { "nintendo3ds", "3ds" }, "3DS"),
        (new[] { "nintendods", "nds", "ds" }, "DS"),
        (new[] { "nes", "famicom", "nintendoentertainmentsystem" }, "NES"),
        (new[] { "playstationportable", "psp" }, "PSP"),
        (new[] { "playstation4", "ps4" }, "PS4"),
        (new[] { "playstation3", "ps3" }, "PS3"),
        (new[] { "playstation2", "ps2" }, "PS2"),
        (new[] { "playstation", "ps1", "psx", "psone" }, "PSX"),
        (new[] { "xbox360", "x360" }, "X360"),
        (new[] { "xbox", "xboxoriginal" }, "XBOX"),
        (new[] { "megadrive", "genesis", "segagenesis", "segamegadrive" }, "MEGADRIVE"),
        (new[] { "dreamcast" }, "DREAMCAST"),
        (new[] { "saturn", "segasaturn" }, "SATURN"),
        (new[] { "pcengine", "turbografx", "turbografx16", "pce" }, "PCE"),
        (new[] { "arcade", "cps1", "cps2", "cps3", "naomi", "mame", "neogeo", "model2", "model3" }, "Arcade"),
        (new[] { "pc", "dos", "windows", "msdos" }, "PC"),
    };

    /// <summary>Sistema sugerido para una lista de consolas; prefiere uno que ya exista en la biblioteca.</summary>
    public static string? Suggest(IEnumerable<string> consoles, IEnumerable<string>? existingSystems = null)
    {
        var existing = existingSystems?.ToList() ?? new List<string>();
        foreach (var c in consoles)
        {
            var n = PathUtil.NormName(c);
            foreach (var (keys, system) in Rules)
            {
                if (keys.Any(k => n == k || (k.Length >= 4 && n.Contains(k))))
                    return existing.FirstOrDefault(e => string.Equals(e, system, StringComparison.OrdinalIgnoreCase)) ?? system;
            }
            // coincide con el nombre de una carpeta existente
            var hit = existing.FirstOrDefault(e => PathUtil.NormName(e) == n);
            if (hit != null) return hit;
        }
        return null;
    }
}
