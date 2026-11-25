using RecompVault.Core;

int failed = 0, passed = 0;
void Check(string name, bool ok)
{
    if (ok) passed++; else { failed++; Console.WriteLine("FALLO: " + name); }
}

// ---------------------------------------------------------------- versiones
Check("v1.2 == 1.2.0", VersionUtil.Equivalent("v1.2", "1.2.0"));
Check("v1.2.0 != v1.2.1", !VersionUtil.Equivalent("v1.2.0", "v1.2.1"));
Check("labels distintos", !VersionUtil.Equivalent("v1.0-beta", "v1.0"));
Check("nightly literal", VersionUtil.Equivalent("nightly", "nightly"));
Check("update 1.0 -> 1.1", VersionUtil.IsUpdate("v1.0.0", "v1.1.0"));
Check("prefijo de plataforma: más nueva instalada", !VersionUtil.IsUpdate("windows-v3.2.1", "windows-v2.6.0"));
Check("prefijo de plataforma: actualización real", VersionUtil.IsUpdate("windows-v1.9.8.5", "windows-v1.9.8.6"));
Check("prefijo de plataforma equivalente", VersionUtil.Equivalent("windows-v1.9.8.6", "v1.9.8.6"));
Check("release de otra plataforma sin número no es actualización", !VersionUtil.IsUpdate("windows-v1.9.8.6", "linux-seamless-beta.1"));
Check("prefijo no se come etiquetas", !VersionUtil.Equivalent("windows-v3.1.0-validation-test1", "windows-v3.1.0"));
Check("no update igual", !VersionUtil.IsUpdate("1.1", "v1.1.0"));
Check("no update si instalada es más nueva", !VersionUtil.IsUpdate("v2.0.0", "v1.9.0"));
Check("no update si instalada desconocida", !VersionUtil.IsUpdate("0.0.0", "v1.0"));
Check("no update si 'unknown'", !VersionUtil.IsUpdate("unknown", "v1.0"));
Check("no update si 'instalada'", !VersionUtil.IsUpdate("instalada", "v1.0"));
Check("update tags nightly distintos", VersionUtil.IsUpdate("nightly-2026-01-01", "nightly-2026-02-01"));
Check("Clean primera línea", VersionUtil.Clean("v1.2\r\nbasura") == "v1.2");
Check("Clean BOM", VersionUtil.Clean("﻿v3") == "v3");
Check("fechas largas no petan", VersionUtil.IsNewer("20261117", "20261001"));

// ---------------------------------------------------------------- assets
var assets = new[]
{
    new ReleaseAsset("Game-linux-x64.tar.gz", "u1"),
    new ReleaseAsset("Game-macos-arm64.zip", "u2"),
    new ReleaseAsset("Game-windows-x64.zip", "u3"),
    new ReleaseAsset("Game-windows-arm64.zip", "u4"),
    new ReleaseAsset("Game-windows-x64.zip.sha256", "u5"),
    new ReleaseAsset("Source code.zip", "u6"),
};
Check("elige windows x64 zip", ReleaseAssetPicker.Pick(assets)?.Url == "u3");
Check("ninguno válido", ReleaseAssetPicker.Pick(new[] { new ReleaseAsset("Game.AppImage", "x"), new ReleaseAsset("Game-linux.tar.gz", "y") }) == null);
Check("zip sin SO aceptado", ReleaseAssetPicker.Pick(new[] { new ReleaseAsset("Zelda64Recompiled.zip", "z") })?.Url == "z");
Check("exe suelto", ReleaseAssetPicker.Pick(new[] { new ReleaseAsset("Port.exe", "e"), new ReleaseAsset("Port-linux", "l") })?.Url == "e");
Check("instalador", ReleaseAssetPicker.IsInstaller("Game-Setup.exe") && !ReleaseAssetPicker.IsInstaller("Game.exe"));
Check("crash reporter no es installable por extensión .pdb", ReleaseAssetPicker.Score("game.pdb") < 0);

var shared = new[]
{
    new ReleaseAsset("vf2-windows-x64.zip", "a"), new ReleaseAsset("vf3-windows-x64.zip", "b"), new ReleaseAsset("vf1-windows-x64.zip", "c"),
};
Check("filtro de asset", ReleaseAssetPicker.Pick(shared, "vf3")?.Url == "b");
Check("filtro con comodín", ReleaseAssetPicker.Pick(shared, "vf2-*.zip")?.Url == "a");
Check("filtro sin coincidencia → null", ReleaseAssetPicker.Pick(shared, "naomi") == null);
var cps2 = new[] { new ReleaseAsset("CPS2Recomp-v0.5-windows.zip", "x"), new ReleaseAsset("SFA2-v0.5-windows.zip", "y") };
Check("misma forma que el instalado", ReleaseAssetPicker.Pick(cps2, null, "SFA2-v0.4-windows.zip")?.Url == "y");

// ---------------------------------------------------------------- tokens / rutas
Check("camel tokens", QuiverMatcher.Tokenize("BanjoKazooie-Recomp").SequenceEqual(new[] { "banjo", "kazooie" }));
Check("inside con separador", PathUtil.IsSameOrInside("/x/n64/zelda/sub", "/x/n64/zelda"));
Check("prefijo sin separador no cuenta", !PathUtil.IsSameOrInside("/x/n64/zelda64", "/x/n64/zelda"));
Check("hash estable", PathUtil.StableHash("/a/b") == PathUtil.StableHash("/a/b/"));

// ---------------------------------------------------------------- nombres
void Title(string folder, string expected)
{
    var got = LaunchPicker.CleanTitle(folder);
    Check($"CleanTitle '{folder}' → '{got}' (esperado '{expected}')", got == expected);
}
Title("BanjoRecompiled-v1.0.0-Windows", "Banjo");
Title("Zelda64Recompiled-v1.2.0-Windows", "Zelda 64");
Title("ShiningSoul2Recomp-v0.2.4-windows-x64", "Shining Soul 2");
Title("Golden-Balloon-1.0.3-windows-x64", "Golden Balloon");
Title("Eternal Darkness Recomp HD", "Eternal Darkness HD");
Title("TwilitRealm.dusklight", "Twilit Realm dusklight");
Title("Starfox 64", "Starfox 64");
Check("TitleKey ignora versión", LaunchPicker.TitleKey("Zelda64Recompiled-v1.3.0-Windows") == LaunchPicker.TitleKey("Zelda64Recompiled-v1.2.0-Windows"));
Check("TitleKey distingue juegos", LaunchPicker.TitleKey("Zelda64Recompiled-v1.2.0-Windows") != LaunchPicker.TitleKey("BanjoRecompiled-v1.0.0-Windows"));

// ---------------------------------------------------------------- elección de ejecutable
static byte[] FakePe(ushort subsystem, int size)
{
    var b = new byte[Math.Max(size, 0x200)];
    b[0] = (byte)'M'; b[1] = (byte)'Z';
    BitConverter.GetBytes(0x80).CopyTo(b, 0x3C);
    b[0x80] = (byte)'P'; b[0x81] = (byte)'E';
    BitConverter.GetBytes(subsystem).CopyTo(b, 0x80 + 4 + 20 + 68);
    return b;
}
var exeDir = Path.Combine(Path.GetTempPath(), "rv_exe_" + Guid.NewGuid().ToString("N"));
try
{
    var g = Path.Combine(exeDir, "Goemon64Recompiled-v0.0.5-Windows-x64");
    Directory.CreateDirectory(Path.Combine(g, "tools"));
    File.WriteAllBytes(Path.Combine(g, "Goemon64Recompiled.exe"), FakePe(2, 4_000_000));
    File.WriteAllBytes(Path.Combine(g, "CrashReporter.exe"), FakePe(2, 9_000_000));
    File.WriteAllBytes(Path.Combine(g, "SupportPort.exe"), FakePe(2, 9_000_000));
    File.WriteAllBytes(Path.Combine(g, "romtool.exe"), FakePe(3, 1_000_000));
    File.WriteAllBytes(Path.Combine(g, "tools", "big.exe"), FakePe(2, 90_000_000));
    File.WriteAllBytes(Path.Combine(g, "vc_redist.x64.exe"), FakePe(2, 20_000_000));
    Check("PE GUI", LaunchPicker.PeSubsystem(Path.Combine(g, "Goemon64Recompiled.exe")) == 2);
    Check("PE consola", LaunchPicker.PeSubsystem(Path.Combine(g, "romtool.exe")) == 3);
    var picked = Path.GetFileName(LaunchPicker.Pick(g, Path.GetFileName(g)));
    Check($"elige el juego y no el crash reporter (eligió {picked})", picked == "Goemon64Recompiled.exe");

    var o = Path.Combine(exeDir, "OOT");
    Directory.CreateDirectory(o);
    File.WriteAllBytes(Path.Combine(o, "LongshotLauncher-v1.0.0-rc.10-windows-x64-setup.exe"), FakePe(2, 3_000_000));
    Check("solo instaladores → sin ejecutable", LaunchPicker.Pick(o, "OOT") == "");
    var pr = Path.Combine(exeDir, "openprey-0.0.1-windows");
    Directory.CreateDirectory(pr);
    File.WriteAllBytes(Path.Combine(pr, "OpenPrey-client_x64.exe"), FakePe(2, 5_000_000));
    File.WriteAllBytes(Path.Combine(pr, "OpenPrey-ded_x64.exe"), FakePe(3, 5_000_000));
    Check("cliente y no servidor dedicado", Path.GetFileName(LaunchPicker.Pick(pr, "openprey-0.0.1-windows")) == "OpenPrey-client_x64.exe");
    var ab = Path.Combine(exeDir, "actraiser-recomp-windows-x86_64");
    Directory.CreateDirectory(ab);
    File.WriteAllBytes(Path.Combine(ab, "ActRaiserRecomp.exe"), FakePe(2, 3_000_000));
    File.WriteAllText(Path.Combine(ab, "run-build.bat"), "x");
    File.WriteAllText(Path.Combine(ab, "run-game.bat"), "x");
    Check("exe antes que bat de build", Path.GetFileName(LaunchPicker.Pick(ab, "actraiser-recomp-windows-x86_64")) == "ActRaiserRecomp.exe");
    var cp = Path.Combine(exeDir, "CPS2Recomp");
    Directory.CreateDirectory(cp);
    foreach (var gname in new[] { "avsp", "cybots", "dimahoo", "dino" }) File.WriteAllBytes(Path.Combine(cp, gname + ".exe"), FakePe(2, 6_000_000));
    File.WriteAllBytes(Path.Combine(cp, "cps2recomp-launcher.exe"), FakePe(2, 2_000_000));
    Check("launcher de colección CPS2", Path.GetFileName(LaunchPicker.Pick(cp, "CPS2Recomp")) == "cps2recomp-launcher.exe");

    var h = Path.Combine(exeDir, "SM64PC");
    Directory.CreateDirectory(h);
    File.WriteAllBytes(Path.Combine(h, "sm64.us.f3dex2e.exe"), FakePe(2, 12_000_000));
    File.WriteAllBytes(Path.Combine(h, "texture-converter.exe"), FakePe(3, 2_000_000));
    Check("sm64 exe", Path.GetFileName(LaunchPicker.Pick(h, "SM64PC")) == "sm64.us.f3dex2e.exe");
}
finally { try { Directory.Delete(exeDir, true); } catch { } }

// ---------------------------------------------------------------- índice de zip (verificación por huella)
var zipDir = Path.Combine(Path.GetTempPath(), "rv_zip_" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(zipDir);
    var exeLocal = Path.Combine(zipDir, "Game.exe");
    var rnd = new Random(7); var exeBytes = new byte[300_000]; rnd.NextBytes(exeBytes);
    File.WriteAllBytes(exeLocal, exeBytes);
    var zipPath = Path.Combine(zipDir, "pkg.zip");
    using (var za = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
    {
        System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(za, exeLocal, "Game-v1.2/Game.exe");
        var e2 = za.CreateEntry("Game-v1.2/data/readme.txt");
        using (var w = new StreamWriter(e2.Open())) w.Write("hola");
        for (int k = 0; k < 400; k++) { var ek = za.CreateEntry($"Game-v1.2/assets/f{k}.bin"); using var sw = ek.Open(); sw.WriteByte((byte)k); }
    }
    var all = File.ReadAllBytes(zipPath);
    var tail = all[^Math.Min(all.Length, 8192)..];
    var loc = ZipIndex.FindCentralDirectory(tail, all.Length);
    Check("EOCD encontrado", loc != null);
    var cdBytes = all[(int)loc!.Offset..(int)(loc.Offset + loc.Size)];
    var entries = ZipIndex.ParseCentralDirectory(cdBytes);
    Check($"entradas leídas ({entries.Count})", entries.Count == 402);
    var crcLocal = ZipIndex.FileCrc32(exeLocal);
    Check("exe local coincide con el zip", ZipIndex.FindMatch(entries, "Game.exe", exeBytes.Length, crcLocal) != null);
    exeBytes[10] ^= 0xFF; File.WriteAllBytes(exeLocal, exeBytes);
    Check("exe modificado no coincide", ZipIndex.FindMatch(entries, "Game.exe", exeBytes.Length, ZipIndex.FileCrc32(exeLocal)) == null);
    Check("no-zip devuelve null", ZipIndex.FindCentralDirectory(new byte[100], 100) == null);
}
finally { try { Directory.Delete(zipDir, true); } catch { } }

// ---------------------------------------------------------------- Steam y sistemas
Check("crc32 conocido", SteamShortcuts.Crc32(System.Text.Encoding.ASCII.GetBytes("123456789")) == 0xCBF43926);
var (vdf1, id1, upd1) = SteamShortcuts.AddOrUpdate(null, "Zelda 64", @"D:\J\z.exe", @"D:\J", @"D:\J\z.exe", "", new[] { "RecompVault", "N64" });
Check("appid con bit alto", (id1 & 0x80000000) != 0 && !upd1);
var parsed = BinaryVdf.Read(vdf1);
var sc = parsed.Get("shortcuts") as VdfNode;
Check("vdf ida y vuelta", sc != null && (sc.Get("0") as VdfNode)?.Get("AppName") as string == "Zelda 64");
var (vdf2, id2, upd2) = SteamShortcuts.AddOrUpdate(vdf1, "Banjo", @"D:\J\b.exe", @"D:\J", "", "-x", Array.Empty<string>());
var (vdf3, _, upd3) = SteamShortcuts.AddOrUpdate(vdf2, "Zelda 64", @"D:\J\z.exe", @"D:\J", "", "", Array.Empty<string>());
var sc3 = BinaryVdf.Read(vdf3).Get("shortcuts") as VdfNode;
Check("segundo acceso en índice 1", sc3?.Order.Count == 2 && (sc3.Get("1") as VdfNode)?.Get("LaunchOptions") as string == "-x");
Check("mismo exe actualiza, no duplica", upd3 && !upd2);
Check("bytes idénticos tras releer y reescribir", BinaryVdf.Write(BinaryVdf.Read(vdf3)).SequenceEqual(vdf3));
Check("mapper N64", SystemMapper.Suggest(new[] { "Nintendo 64" }) == "N64");
Check("mapper PS2 vs PS", SystemMapper.Suggest(new[] { "PlayStation 2" }) == "PS2" && SystemMapper.Suggest(new[] { "PlayStation" }) == "PSX");
Check("mapper Switch 2", SystemMapper.Suggest(new[] { "Nintendo Switch 2" }) == "SWITCH 2");
Check("mapper Xbox 360", SystemMapper.Suggest(new[] { "Xbox 360" }) == "X360" && SystemMapper.Suggest(new[] { "Xbox" }) == "XBOX");
Check("mapper respeta carpeta existente", SystemMapper.Suggest(new[] { "Sega Genesis" }, new[] { "MegaDrive" }) == "MegaDrive");
Check("mapper desconocido", SystemMapper.Suggest(new[] { "Virtual Boy" }) == null);
Check("título núcleo quita plataforma y versión", QuiverMatcher.CoreTitle("SuperMetroidSNESRecomp-windows-x64-v0.3.0") == "supermetroid");
Check("título núcleo conserva números", QuiverMatcher.CoreTitle("Ridge_Racer_4_Recomp_Windows") != QuiverMatcher.CoreTitle("Ridge Racer"));
Check("título núcleo: colección no es el juego", QuiverMatcher.CoreTitle("Ridge Racer Collection") != QuiverMatcher.CoreTitle("Ridge Racer"));
Check("título núcleo iguala nombres equivalentes", QuiverMatcher.CoreTitle("Super Mario World") == QuiverMatcher.CoreTitle("SuperMarioWorldRecomp-windows-x64-v0.9.5"));

// ---- The Gaming Emporium
{
    var card = @"<div class=""game-card game-card--decomp-list"" data-favourite-id=""mm"" data-title=""The Legend of Zelda: Majora&#39;s Mask Recompilation"" data-genres=""RPG|Action"" data-category=""decompilations-recompilations"" data-platform=""Nintendo 64"" data-release-platforms=""linux|macos|windows|"">
  <button></button><a class=""game-card__link"" href=""https://github.com/Zelda64Recomp/Zelda64Recomp"" target=""_blank"">x</a></div>
<div class=""game-card"" data-title=""Some Launcher"" data-genres=""Utility"" data-category=""decompilations-recompilations"" data-platform="""" data-release-platforms=""windows|""><a class=""game-card__link"" href=""https://github.com/foo/launcher/releases/tag/v1"">x</a></div>
<div class=""game-card"" data-title=""Itch Game"" data-category=""fan-games""><a class=""game-card__link"" href=""https://foo.itch.io/game"">x</a></div>
<div class=""game-card"" data-title=""Takedawn"" data-genres=""Racing"" data-category=""decompilations-recompilations"" data-platform=""Xbox"" data-release-platforms=""windows|""><a class=""game-card__link"" href=""https://github.com/xenophobentx/takedawn.git"">x</a></div>";
    var em = EmporiumCatalog.ParseCategoryPage(card);
    Check("emporium: solo tarjetas con repositorio", em.Count == 3);
    Check("emporium: título decodificado", em[0].Title == "The Legend of Zelda: Majora's Mask Recompilation");
    Check("emporium: repo de enlace a release", em[1].Repository == "foo/launcher" && em[1].IsUtility);
    Check("emporium: .git fuera", em[2].Repository == "xenophobentx/takedawn");
    Check("emporium: parse gitlab", EmporiumCatalog.ParseRepo("https://gitlab.com/onerag90/fe3h-recomp/-/releases")?.repo == "onerag90/fe3h-recomp");
    Check("emporium: no es repo", EmporiumCatalog.ParseRepo("https://github.com/orgs/foo/repositories") == null);

    var tmpE = Path.Combine(Path.GetTempPath(), "rv-emp-" + Guid.NewGuid().ToString("N"));
    var dTake = Directory.CreateDirectory(Path.Combine(tmpE, "XBOX", "B3")).FullName;
    File.WriteAllBytes(Path.Combine(dTake, "Takedawn.exe"), new byte[] { 0x4D, 0x5A });
    var mTake = EmporiumCatalog.Match(em, dTake, "XBOX");
    Check("emporium: exe con el nombre del repo", mTake?.Entry.Repository == "xenophobentx/takedawn" && !mTake.Approximate);
    var dMm = Directory.CreateDirectory(Path.Combine(tmpE, "N64", "The Legend of Zelda Majoras Mask")).FullName;
    var mMm = EmporiumCatalog.Match(em, dMm, "N64");
    Check("emporium: por título es aproximada", mMm?.Entry.Repository == "Zelda64Recomp/Zelda64Recomp" && mMm.Approximate == true);
    Check("emporium: plataforma distinta descarta", EmporiumCatalog.Match(em, dMm, "PSX") == null);
    var dRd = Directory.CreateDirectory(Path.Combine(tmpE, "N64", "Juego")).FullName;
    File.WriteAllText(Path.Combine(dRd, "README.txt"), "Download from https://github.com/Zelda64Recomp/Zelda64Recomp/releases");
    var mRd = EmporiumCatalog.Match(em, dRd, "N64");
    Check("emporium: enlace en README", mRd?.Kind.StartsWith("enlace") == true && !mRd.Approximate);
    try { Directory.Delete(tmpE, true); } catch { }
}

// ---------------------------------------------------------------- ficheros protegidos
Check("glob *.ini en subcarpeta", GlobMatcher.IsMatch(@"cfg\game.ini", "*.ini"));
Check("glob carpeta/**", GlobMatcher.IsMatch("saves/slot1/a.bin", "saves/**") && GlobMatcher.IsMatch(@"Saves\x.dat", "saves/**"));
Check("glob no coincide", !GlobMatcher.IsMatch("data/saves.bin", "saves/**") && !GlobMatcher.IsMatch("game.exe", "*.ini"));
Check("glob ruta exacta", GlobMatcher.IsMatch("config/user.json", "config/user.json") && !GlobMatcher.IsMatch("other/config/user.json", "config/user.json"));
Check("glob **/x", GlobMatcher.IsMatch("a/b/keys.txt", "**/keys.txt") && GlobMatcher.IsMatch("keys.txt", "**/keys.txt"));
Check("parse patrones", GlobMatcher.Parse("*.ini; saves/**\n*.sav").Count == 3);

// ---------------------------------------------------------------- deshacer actualización
var undoDir = Path.Combine(Path.GetTempPath(), "rv_undo_" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(Path.Combine(undoDir, "data"));
    File.WriteAllText(Path.Combine(undoDir, "game.exe"), "v1");
    File.WriteAllText(Path.Combine(undoDir, "data", "a.bin"), "A1");
    File.WriteAllText(Path.Combine(undoDir, "save.dat"), "partida");
    File.WriteAllText(Path.Combine(undoDir, "version.txt"), "v1.0");

    var s = UndoSession.Begin(undoDir, "Juego", "v1.0", "v2.0", "Juego-v2.zip");
    foreach (var (rel, content) in new[] { ("game.exe", "v2"), (Path.Combine("data", "a.bin"), "A2"), (Path.Combine("nuevo", "sub", "b.bin"), "B"), ("version.txt", "v2.0") })
    {
        s.BeforeWrite(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(undoDir, rel))!);
        File.WriteAllText(Path.Combine(undoDir, rel), content);
    }
    s.Commit();
    Check("tras actualizar: ficheros nuevos", File.ReadAllText(Path.Combine(undoDir, "game.exe")) == "v2" && File.Exists(Path.Combine(undoDir, "nuevo", "sub", "b.bin")));
    Check("manifiesto disponible", UndoStore.Latest(undoDir)?.FromVersion == "v1.0");
    Check("el escáner no ve la copia como ejecutable", LaunchPicker.Rank(undoDir, "Juego").All(c => !c.Path.Contains(UndoSession.RootDirName)));

    var err = UndoStore.RestoreLatest(undoDir);
    Check($"deshacer sin errores ({err})", err == null);
    Check("exe original restaurado", File.ReadAllText(Path.Combine(undoDir, "game.exe")) == "v1");
    Check("dato original restaurado", File.ReadAllText(Path.Combine(undoDir, "data", "a.bin")) == "A1");
    Check("version.txt restaurado", File.ReadAllText(Path.Combine(undoDir, "version.txt")) == "v1.0");
    Check("fichero creado eliminado y carpetas vacías fuera", !Directory.Exists(Path.Combine(undoDir, "nuevo")));
    Check("partida intacta", File.ReadAllText(Path.Combine(undoDir, "save.dat")) == "partida");
    Check("copia de deshacer eliminada", !Directory.Exists(Path.Combine(undoDir, UndoSession.RootDirName)));

    // rollback de una actualización que falla a medias
    var s2 = UndoSession.Begin(undoDir, "Juego", "v1.0", "v3.0", null);
    s2.BeforeWrite("game.exe"); File.WriteAllText(Path.Combine(undoDir, "game.exe"), "v3");
    s2.BeforeWrite("extra.dll"); File.WriteAllText(Path.Combine(undoDir, "extra.dll"), "x");
    Check("rollback sin errores", s2.Rollback() == null);
    Check("rollback deja el original", File.ReadAllText(Path.Combine(undoDir, "game.exe")) == "v1" && !File.Exists(Path.Combine(undoDir, "extra.dll")));

    // poda: solo se guarda la última
    for (int k = 0; k < 3; k++)
    {
        var sk = UndoSession.Begin(undoDir, "Juego", $"v{k}", $"v{k + 1}", null);
        sk.BeforeWrite("game.exe"); File.WriteAllText(Path.Combine(undoDir, "game.exe"), $"v{k + 1}");
        sk.Commit(keep: 1);
    }
    var lst = UndoStore.List(undoDir);
    Check($"solo una copia guardada ({lst.Count}: {string.Join(",", lst.Select(x => Path.GetFileName(x.dir) + "=" + x.manifest.ToVersion))})", lst.Count == 1 && lst[0].manifest.ToVersion == "v3");
}
finally { try { Directory.Delete(undoDir, true); } catch { } }

// ---------------------------------------------------------------- biblioteca Quiver simulada
var tmp = Path.Combine(Path.GetTempPath(), "rv_test_" + Guid.NewGuid().ToString("N"));
try
{
    var q = Path.Combine(tmp, "QuiverLauncher-win-Portable");
    var gamesRoot = Path.Combine(tmp, "Juegos");
    Directory.CreateDirectory(Path.Combine(q, "current", "Cache", "CustomIcons"));
    Directory.CreateDirectory(Path.Combine(q, "Apps", "Zelda64-Recomp"));
    File.WriteAllText(Path.Combine(q, "Apps", "Zelda64-Recomp", "version.txt"), "v1.2.0");
    Directory.CreateDirectory(Path.Combine(gamesRoot, "N64", "BanjoRecompiled-v1.0.0-Windows"));
    File.WriteAllText(Path.Combine(gamesRoot, "N64", "BanjoRecompiled-v1.0.0-Windows", "BanjoRecompiled.exe"), "MZ");
    File.WriteAllText(Path.Combine(q, "current", "Cache", "CustomIcons", "Zelda64-Recomp_custom.png"), "png");

    // PascalCase + BOM + installPath relativo + app manual sin repo
    File.WriteAllText(Path.Combine(q, "apps.json"), "﻿" + """
    {
      "Apps": [
        { "Name": "Zelda 64: Recompiled", "Repository": "Zelda64Recomp/Zelda64Recomp", "FolderName": "Zelda64-Recomp" },
        { "name": "Banjo Recompiled", "repository": "BanjoRecomp/BanjoRecomp", "folderName": "BanjoKazooie-Recomp" },
        { "name": "Manual", "folderName": "Manual", "tags": ["manual"] },
        { "name": "GitLab thing", "repository": "grp/proj", "repositorySource": "gitlab", "folderName": "GL", "installPath": "Apps/GL" }
      ]
    }
    """);
    File.WriteAllText(Path.Combine(q, "current", "Cache", "version_cache.json"), """
    {
      "github:Zelda64Recomp/Zelda64Recomp": { "Version": "v1.3.0", "CachedRelease": { "tag_name": "v1.3.0", "assets": [
          { "name": "Zelda64Recompiled-v1.3.0-Windows.zip", "browser_download_url": "https://x/z.zip", "digest": "sha256:abc" } ] } },
      "BanjoRecomp/BanjoRecomp": { "Version": "v1.0.1" }
    }
    """);

    Check("ResolveRoot desde current", QuiverLibrary.ResolveRoot(Path.Combine(q, "current")) == q);
    Check("Detect desde raíz de juegos hermana", QuiverLibrary.Detect(null, gamesRoot) == q);
    Check("Detect dentro de la raíz", QuiverLibrary.Detect(null, tmp) == q);

    var lib = QuiverLibrary.Load(q)!;
    Check("4 apps", lib.Apps.Count == 4);
    var z = lib.AppByFolderName("zelda64-recomp");
    Check("PascalCase leído", z != null && z.Repository == "Zelda64Recomp/Zelda64Recomp");
    Check("installPath por Apps/folderName", z != null && z.InstallPath.EndsWith("Zelda64-Recomp"));
    Check("custom icon en current/Cache", z != null && z.CustomIconPath.EndsWith("Zelda64-Recomp_custom.png"));
    Check("caché de versiones con prefijo", lib.Release("github", "Zelda64Recomp/Zelda64Recomp")?.Version == "v1.3.0");
    Check("caché de versiones sin prefijo", lib.LatestKnown("github", "BanjoRecomp/BanjoRecomp") == "v1.0.1");
    Check("asset de la caché", ReleaseAssetPicker.Pick(lib.Release("github", "Zelda64Recomp/Zelda64Recomp")!.Assets)?.Digest == "sha256:abc");
    Check("gitlab provider", lib.AppByFolderName("GL")?.Provider == "gitlab");
    Check("caché reutilizada", ReferenceEquals(lib, QuiverLibrary.Load(q)));

    var m = QuiverMatcher.Match(lib, z!.InstallPath, "x");
    Check("match por ruta", m?.App == z);
    var banjoDir = Path.Combine(gamesRoot, "N64", "BanjoRecompiled-v1.0.0-Windows");
    var mb = QuiverMatcher.Match(lib, banjoDir, "Banjo-Kazooie");
    Check("match difuso Banjo", mb?.App?.Repository == "BanjoRecomp/BanjoRecomp");
    // Disposición real: installPath absoluto a carpetas de la raíz; sm64ds no debe emparejarse con un port de SM64 de N64
    var q2 = Path.Combine(tmp, "Q2");
    var r2 = Path.Combine(tmp, "Root2");
    var dsDir = Path.Combine(r2, "DS", "sm64ds");
    var n64Dir = Path.Combine(r2, "N64", "Ghostship-Dutchman-Charlie-Win64");
    var dpOuter = Path.Combine(r2, "X360", "Deadly Premonition (Europe)");
    var dpInner = Path.Combine(dpOuter, "Deadly Premonition (Europe)");
    foreach (var d in new[] { q2, dsDir, n64Dir, dpInner }) Directory.CreateDirectory(d);
    File.WriteAllText(Path.Combine(dsDir, "sm64ds.exe"), "MZ");
    File.WriteAllText(Path.Combine(n64Dir, "Ghostship.exe"), "MZ");
    File.WriteAllText(Path.Combine(dpInner, "deadlyprem.exe"), "MZ");
    var json = new System.Text.Json.Nodes.JsonObject
    {
        ["apps"] = new System.Text.Json.Nodes.JsonArray(
            new System.Text.Json.Nodes.JsonObject { ["name"] = "Super Mario 64 (Ghostship)", ["repository"] = "HarbourMasters/Ghostship", ["folderName"] = "SuperMario64-Ghostship", ["installPath"] = n64Dir },
            new System.Text.Json.Nodes.JsonObject { ["name"] = "Super Mario 64 Recomp", ["repository"] = "sm64recomp/sm64recomp", ["folderName"] = "SuperMario64-Recomp" },
            new System.Text.Json.Nodes.JsonObject { ["name"] = "Deadly Premonition", ["repository"] = "x/deadlyprem", ["folderName"] = "DeadlyPremonition", ["installPath"] = dpInner })
    };
    File.WriteAllText(Path.Combine(q2, "apps.json"), json.ToJsonString());
    var lib2 = QuiverLibrary.Load(q2)!;
    Check("Ghostship por installPath absoluto", QuiverMatcher.MatchExact(lib2, n64Dir)?.App?.Repository == "HarbourMasters/Ghostship");
    var taken2 = new HashSet<QuiverApp> { lib2.AppByPath(n64Dir)! };
    var ds = QuiverMatcher.Match(lib2, dsDir, "Super Mario 64 DS", taken2);
    Check($"sm64ds sin emparejar con SM64 de N64 (emparejó {ds?.App?.Name})", ds == null);
    Check("instalación anidada = mismo juego", QuiverMatcher.MatchExact(lib2, dpOuter)?.App?.Repository == "x/deadlyprem");

    var none = Path.Combine(gamesRoot, "N64", "Otro");
    Directory.CreateDirectory(none);
    Check("sin falso positivo", QuiverMatcher.Match(lib, none, "Otro juego") == null);
}
finally
{
    try { Directory.Delete(tmp, true); } catch { }
}

Console.WriteLine($"{passed} OK, {failed} fallos");
return failed == 0 ? 0 : 1;
