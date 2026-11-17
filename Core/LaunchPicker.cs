namespace RecompVault.Core;

/// <summary>Elige el ejecutable de lanzamiento de una carpeta de juego.</summary>
public static class LaunchPicker
{
    private static readonly HashSet<string> HeavyDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "romfs", "exefs", "assets", "cache", "extracted", "nup_dump", "mods", "save", "saves", "logs", "debug",
        "shaders", "shadercache", "redist", "_redist", "redistributables", "directx", "vcredist", "__macosx",
        "tools", "_commonredist", "commonredist", "dotnet", "python", "node_modules", ".git", "screenshots", "dumps"
    };

    /// <summary>Nombres exactos (sin extensión) que nunca son el juego.</summary>
    private static readonly HashSet<string> ExcludedStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "hactool","zig","python","pythonw","chdman","ffmpeg","ffprobe","clang","lld-link","ninja","tcc","i386-win32-tcc","armips",
        "torch","createdump","elevate","unins000","unins001","uninstall","uninst","setup","vc_redist.x64","vc_redist.x86","vcredist_x64","vcredist_x86",
        "dxsetup","dxwebsetup","exiso","xdvdfs","rexglue","nodtool","waifu2x-ncnn-vulkan","7z","7za","7zr","unrar","rar","curl","wget","git",
        "unitycrashhandler64","unitycrashhandler32","crashpad_handler","crashreporter","crash_reporter","quiverlauncher","update","updater",
        "notification_helper","cefsharp.browsersubprocess","dotnet","node","cmd","powershell","adb","fastboot","oo2core","texconv","nvtt"
    };

    /// <summary>Fragmentos que indican herramienta auxiliar.</summary>
    private static readonly string[] BadFragments =
    {
        "generator","extract","crash","report","helper","updater","patcher","redist","dxweb","server","dedicated","-ded",
        "_ded","backend","editor","config","configurator","settings","benchmark","shadercompile","converter","packer",
        "unpack","dumper","debugger","symbol","sample","example","register","diagnostic","build","compile","cmake",
        "firewall","dev.","-dev","_dev","keycap","texture","asset","iso extract","xiso","cleanup","clean"
    };

    /// <summary>Instaladores/desinstaladores: nunca se eligen.</summary>
    private static readonly string[] HardExcluded = { "setup", "uninstall", "unins0", "installer", "install.", "installgame", "vc_redist", "vcredist", "dxsetup" };

    public sealed record Candidate(string Path, int Score);

    public static string Pick(string gameDir, string folderName, int maxDepth = 3) =>
        Rank(gameDir, folderName, maxDepth).FirstOrDefault()?.Path ?? "";

    public static List<Candidate> Rank(string gameDir, string folderName, int maxDepth = 3)
    {
        var result = new List<Candidate>();
        var files = Collect(gameDir, maxDepth);
        if (files.Count == 0) return result;

        var folderTokens = QuiverMatcher.Tokenize(CleanTitle(folderName)).ToList();
        var folderNorm = PathUtil.NormName(folderName);

        foreach (var (f, depth) in files)
        {
            var stem = Path.GetFileNameWithoutExtension(f);
            var lower = stem.ToLowerInvariant();
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ExcludedStems.Contains(lower)) continue;
            if (HardExcluded.Any(h => (lower + ".").Contains(h))) continue;

            int score = 0;
            if (ext == ".exe") score += 20;
            else score -= 5; // .bat / .cmd valen como último recurso (muchos ports arrancan así)

            if (BadFragments.Any(b => lower.Contains(b))) score -= 80;
            if (lower.Contains("launcher") || lower.Contains("lanceur")) score += 45;
            if (lower.StartsWith("play ") || lower == "play" || lower.StartsWith("play_") || lower.StartsWith("jugar")) score += 25;
            if (lower.Contains("without the launcher") || lower.Contains("sin launcher") || lower.Contains("no-launcher")) score -= 35;
            if (lower.Contains("recomp") || lower.Contains("decomp")) score += 40;
            if (lower.EndsWith("-cli") || lower.EndsWith("_cli") || lower.Contains("console")) score -= 30;

            // profundidad: preferir la raíz
            score += depth == 0 ? 30 : depth == 1 ? 10 : 0;

            // coincidencia con el nombre de la carpeta
            var stemNorm = PathUtil.NormName(stem);
            if (stemNorm.Length >= 4 && folderNorm.Length >= 4)
            {
                if (folderNorm.StartsWith(stemNorm) || stemNorm.StartsWith(folderNorm)) score += 45;
                else if (folderNorm.Contains(stemNorm) || stemNorm.Contains(folderNorm)) score += 30;
            }
            var stemTokens = QuiverMatcher.Tokenize(stem).ToList();
            score += stemTokens.Count(t => folderTokens.Contains(t)) * 12;

            // las herramientas de consola (subsistema CUI) casi nunca son el juego
            if (ext == ".exe")
            {
                var sub = PeSubsystem(f);
                if (sub == 2) score += 15;      // GUI
                else if (sub == 3) score -= 25; // consola
            }

            // tamaño (los juegos suelen ser binarios grandes)
            try { score += (int)Math.Min(40, new FileInfo(f).Length / (2 * 1024 * 1024)); } catch { }

            result.Add(new Candidate(f, score));
        }
        return result.OrderByDescending(c => c.Score).ThenBy(c => c.Path.Length).ToList();
    }

    private static List<(string path, int depth)> Collect(string root, int maxDepth)
    {
        var files = new List<(string, int)>();
        var stack = new Stack<(string dir, int depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0 && files.Count < 2000)
        {
            var (d, depth) = stack.Pop();
            foreach (var f in QuiverLibrary.SafeFiles(d))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is ".exe" or ".bat" or ".cmd") files.Add((f, depth));
            }
            if (depth >= maxDepth) continue;
            foreach (var sub in QuiverLibrary.SafeDirs(d))
            {
                var bn = Path.GetFileName(sub);
                if (HeavyDirs.Contains(bn) || bn.StartsWith('.')) continue;
                stack.Push((sub, depth + 1));
            }
        }
        return files;
    }

    /// <summary>¿Hay algún ejecutable hasta <paramref name="maxDepth"/> niveles?</summary>
    public static bool HasExecutable(string dir, int maxDepth) => Rank(dir, Path.GetFileName(dir), maxDepth).Count > 0;

    /// <summary>Subsistema PE: 2 = GUI, 3 = consola, 0 = desconocido.</summary>
    public static int PeSubsystem(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var br = new BinaryReader(fs);
            if (fs.Length < 0x40 || br.ReadUInt16() != 0x5A4D) return 0; // "MZ"
            fs.Position = 0x3C;
            var peOffset = br.ReadInt32();
            if (peOffset <= 0 || peOffset > fs.Length - 0x60) return 0;
            fs.Position = peOffset;
            if (br.ReadUInt32() != 0x00004550) return 0; // "PE\0\0"
            fs.Position = peOffset + 4 + 20 + 68; // COFF header (20) + offset de Subsystem en el optional header
            return br.ReadUInt16();
        }
        catch { return 0; }
    }

    // ---------------------------------------------------------------- nombres

    private static readonly HashSet<string> NoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "windows","win","win64","win32","x64","x86","x86_64","amd64","msvc","mingw","portable","release","final","build",
        "pc","port","recomp","recompiled","decomp","decompiled","alpha","beta","nightly","master","main","latest"
    };

    /// <summary>
    /// Título legible a partir del nombre de carpeta:
    /// "BanjoRecompiled-v1.0.0-Windows" → "Banjo", "Shining_Soul2Recomp-v0.2.4-windows-x64" → "Shining Soul 2".
    /// </summary>
    public static string CleanTitle(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "";
        // '.' separa palabras salvo dentro de números de versión (1.2.0)
        var chars = folder.Replace('_', ' ').ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (chars[i] == '.' && !(i > 0 && char.IsDigit(chars[i - 1]) && i + 1 < chars.Length && char.IsDigit(chars[i + 1]))) chars[i] = ' ';
        var s = new string(chars);
        // separar CamelCase y números pegados (Zelda64Recompiled → Zelda 64 Recompiled)
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (i > 0)
            {
                var p = s[i - 1];
                bool vPrefix = (i < 2 || !char.IsLetter(s[i - 2])); // "v1", "x64": letra suelta + número
                if ((char.IsUpper(c) && char.IsLower(p)) ||
                    (char.IsUpper(c) && char.IsDigit(p) && i + 1 < s.Length && char.IsLower(s[i + 1])) ||
                    (char.IsDigit(c) && char.IsLetter(p) && !vPrefix))
                    sb.Append(' ');
            }
            sb.Append(c);
        }
        var words = sb.ToString().Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        var kept = new List<string>();
        foreach (var w in words)
        {
            if (NoiseTokens.Contains(w)) continue;
            if (IsVersionToken(w)) continue;
            kept.Add(w);
        }
        var title = string.Join(" ", kept).Trim();
        return title.Length >= 2 ? title : folder.Replace('_', ' ').Trim();
    }

    private static bool IsVersionToken(string w)
    {
        var t = w.TrimStart('v', 'V');
        if (t.Length == 0 || !char.IsDigit(t[0])) return false;
        // "1.2.0", "0.6.0", "v3": versión. Un número suelto solo si va con 'v' delante.
        if (t.Contains('.')) return t.All(c => char.IsDigit(c) || c == '.' || char.IsLetter(c));
        return w.Length != t.Length && t.All(char.IsDigit);
    }

    /// <summary>Clave para buscar en gametitles.json ignorando versión/plataforma: "Zelda64Recompiled-v1.3.0-Windows" → "zelda64recompiled".</summary>
    public static string TitleKey(string folder)
    {
        var words = folder.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !NoiseTokensForKey.Contains(w) && !IsVersionToken(w))
            .Select(w => w.ToLowerInvariant());
        return PathUtil.NormName(string.Join("", words));
    }

    private static readonly HashSet<string> NoiseTokensForKey = new(StringComparer.OrdinalIgnoreCase)
    {
        "windows","win","win64","win32","x64","x86","amd64","msvc","mingw","portable","release"
    };
}
