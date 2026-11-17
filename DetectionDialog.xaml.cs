using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using RecompVault.Core;
using RecompVault.Models;
using RecompVault.Services;

namespace RecompVault;

/// <summary>
/// Muestra qué se ha detectado para un juego (nombre, ejecutable, app de Quiver, repositorio,
/// versiones, portada, copia de deshacer), de dónde sale cada dato y permite cambiarlo.
/// Los cambios se guardan como overrides por carpeta en la configuración.
/// </summary>
public partial class DetectionDialog : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public sealed class Option
    {
        public string Display { get; init; } = "";
        public string? Value { get; init; }      // ruta del exe / folderName de Quiver
        public QuiverApp? App { get; init; }
        public override string ToString() => Display;
    }

    private const string Auto = "\u0001auto";
    private const string None = "\u0001none";

    private readonly GameEntry _g;
    private readonly AppConfig _cfg;
    private readonly QuiverLibrary? _lib;
    private readonly List<Option> _quiverOptions = new();
    private bool _loading = true;

    /// <summary>Se guardaron cambios: hay que volver a detectar este juego.</summary>
    public bool Saved { get; private set; }
    /// <summary>El usuario pidió deshacer la última actualización.</summary>
    public bool UndoRequested { get; private set; }
    /// <summary>El usuario pidió instalar una versión concreta (tras guardar los cambios del vínculo).</summary>
    public bool InstallVersionRequested { get; private set; }

    public DetectionDialog(GameEntry g, AppConfig cfg, QuiverLibrary? lib)
    {
        InitializeComponent();
        _g = g; _cfg = cfg; _lib = lib;
        SourceInitialized += (_, _) =>
        {
            try { int d = 1; var h = new WindowInteropHelper(this).Handle; if (DwmSetWindowAttribute(h, 20, ref d, 4) != 0) DwmSetWindowAttribute(h, 19, ref d, 4); } catch { }
        };
        Fill();
        QuiverFilterBox.TextChanged += (_, _) => FilterQuiver();
        _loading = false;
    }

    private void Fill()
    {
        Title = $"Detección — {_g.Name}";
        HeaderText.Text = _g.Name;
        FolderText.Text = _g.FolderPath;
        FolderText.ToolTip = _g.FolderPath;
        SystemText.Text = _g.System + (_g.IsExternal ? "   (app de Quiver fuera de la carpeta raíz)" : "");

        // ---- nombre
        NameBox.Text = _g.Name;
        NameSourceText.Text = "Origen: " + (string.IsNullOrWhiteSpace(_g.NameSource) ? "automático" : _g.NameSource) +
                              $"   ·   carpeta: {Path.GetFileName(_g.FolderPath)}" +
                              "   ·   vacío = volver al automático";

        // ---- ejecutable
        var folderName = Path.GetFileName(_g.FolderPath);
        var ranked = LaunchPicker.Rank(_g.FolderPath, folderName);
        var exeOptions = new List<Option>
        {
            new() { Display = "(automático) " + (ranked.Count > 0 ? Rel(ranked[0].Path) : "ninguno encontrado"), Value = Auto }
        };
        exeOptions.AddRange(ranked.Take(40).Select(c => new Option { Display = $"{Rel(c.Path)}    [puntuación {c.Score}]", Value = c.Path }));
        if (!string.IsNullOrWhiteSpace(_g.LaunchPath) && exeOptions.All(o => !string.Equals(o.Value, _g.LaunchPath, StringComparison.OrdinalIgnoreCase)))
            exeOptions.Insert(1, new Option { Display = Rel(_g.LaunchPath) + "    [elegido a mano]", Value = _g.LaunchPath });
        ExeBox.ItemsSource = exeOptions;
        var manualExe = ConfigService.Get(_cfg.LaunchOverrides, _g.FolderPath);
        ExeBox.SelectedItem = manualExe != null
            ? exeOptions.FirstOrDefault(o => string.Equals(o.Value, manualExe, StringComparison.OrdinalIgnoreCase)) ?? exeOptions[0]
            : exeOptions[0];
        LaunchSourceText.Text = "Origen: " + (string.IsNullOrWhiteSpace(_g.LaunchSource) ? "automático" : _g.LaunchSource) +
                                (string.IsNullOrWhiteSpace(_g.LaunchPath) ? "" : $"   ·   actual: {Rel(_g.LaunchPath)}");

        // ---- Quiver
        _quiverOptions.Add(new Option { Display = "(automático)", Value = Auto });
        _quiverOptions.Add(new Option { Display = "(sin vincular — no buscar actualizaciones)", Value = None });
        if (_lib != null)
        {
            foreach (var a in _lib.Apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var where = a.InstallPath.Length > 0 && Directory.Exists(a.InstallPath) ? $"  · instalada en {a.InstallPath}" : "";
                _quiverOptions.Add(new Option
                {
                    Display = $"{a.Name}   —   {(a.Repository.Length > 0 ? a.Repository : "(sin repositorio)")}   [{a.FolderName}]{where}",
                    Value = a.FolderName, App = a
                });
            }
        }
        QuiverBox.ItemsSource = _quiverOptions;

        LinkOverride? ov;
        lock (ConfigService.Sync) _cfg.LinkOverrides.TryGetValue(_g.FolderPath, out ov);
        if (ov?.Unlinked == true) QuiverBox.SelectedIndex = 1;
        else if (!string.IsNullOrWhiteSpace(ov?.QuiverFolderName))
            QuiverBox.SelectedItem = _quiverOptions.FirstOrDefault(o => o.App != null && string.Equals(o.Value, ov.QuiverFolderName, StringComparison.OrdinalIgnoreCase)) ?? _quiverOptions[0];
        else QuiverBox.SelectedIndex = 0;

        var detectedApp = !string.IsNullOrWhiteSpace(_g.QuiverFolderName) ? _lib?.AppByFolderName(_g.QuiverFolderName!) : null;
        LinkSourceText.Text = "Detectado: " + _g.LinkSource +
            (detectedApp != null ? $"  →  {detectedApp.Name} [{detectedApp.FolderName}]" : "") +
            (_lib == null ? "   ·   Quiver no detectado (botón Q Quiver)" : $"   ·   {_lib.Apps.Count} apps en Quiver");

        RepoBox.Text = _g.Repository ?? "";
        ProviderBox.SelectedIndex = string.Equals(_g.Provider, "gitlab", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        FilterBox.Text = _g.AssetFilter ?? "";
        ProtectedBox.Text = ConfigService.Get(_cfg.ProtectedByGame, _g.FolderPath) ?? "";
        lock (ConfigService.Sync)
            ProtectedHint.Text = "Además de los globales: " + string.Join("; ", _cfg.ProtectedPatterns) + ". Se conservan si ya existen; los nuevos sí se copian.";

        // ---- estado
        VersionText.Text =
            $"Instalada (version.txt):  {(_g.InstalledUnknown ? "desconocida" : _g.InstalledVersion)}\n" +
            $"Paquete instalado:        {_g.InstalledAssetName ?? "—"}\n" +
            $"Última publicada:         {(string.IsNullOrWhiteSpace(_g.LatestVersion) ? "—" : _g.LatestVersion)}\n" +
            $"Paquete que se bajaría:   {_g.UpdateAssetName ?? "—"}\n" +
            $"¿Actualización?:          {(_g.HasUpdate ? "sí" : _g.InstalledUnknown && !string.IsNullOrWhiteSpace(_g.LatestVersion) ? "no se sabe (instalada desconocida)" : "no")}";

        var coverOv = ConfigService.Get(_cfg.CoverOverrides, _g.FolderPath);
        CoverText.Text = !_g.HasCover ? "Sin portada (usa 🔎 o ⬇ en la tarjeta)."
            : coverOv != null && File.Exists(coverOv) ? $"Guardada: {coverOv}"
            : !string.IsNullOrWhiteSpace(_g.QuiverLocalIconPath) ? $"Icono de Quiver: {_g.QuiverLocalIconPath}"
            : "Imagen encontrada en la carpeta del juego.";

        var undo = UndoStore.Latest(_g.FolderPath);
        UndoBtn.IsEnabled = undo != null;
        UndoText.Text = undo == null
            ? (UpdateService.UpdatedByOldRecompVault(_g.FolderPath)
                ? "Sin copia: esta carpeta la actualizó la versión ANTERIOR de RecompVault, que no guardaba copia. Si el juego quedó mal, hay que reinstalarlo (desde Quiver o a mano)."
                : "No hay copia: RecompVault no ha actualizado este juego (o ya se deshizo).")
            : $"{undo.CreatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}: {(undo.FromVersion ?? "desconocida")} → {undo.ToVersion} ({undo.AssetName}). " +
              $"{undo.Replaced.Count} ficheros reemplazados, {undo.Created.Count} nuevos, copia de {undo.BackupBytes / 1048576.0:0.#} MB.";
    }

    private string Rel(string path)
    {
        try { return PathUtil.IsSameOrInside(path, _g.FolderPath) ? Path.GetRelativePath(_g.FolderPath, path) : path; }
        catch { return path; }
    }

    private void FilterQuiver()
    {
        var q = QuiverFilterBox.Text?.Trim() ?? "";
        var selected = QuiverBox.SelectedItem;
        var terms = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var list = terms.Length == 0 ? _quiverOptions
            : _quiverOptions.Where(o => o.App == null || terms.All(t => o.Display.Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();
        _loading = true;
        QuiverBox.ItemsSource = list;
        QuiverBox.SelectedItem = selected != null && list.Contains(selected) ? selected : null;
        _loading = false;
        if (terms.Length > 0 && list.Count > 2) QuiverBox.IsDropDownOpen = true;
    }

    private void QuiverBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || QuiverBox.SelectedItem is not Option o) return;
        if (o.App != null)
        {
            RepoBox.Text = o.App.Repository;
            ProviderBox.SelectedIndex = o.App.Provider == "gitlab" ? 1 : 0;
            FilterBox.Text = o.App.AssetFilter;
        }
        else if (o.Value == None)
        {
            RepoBox.Text = ""; FilterBox.Text = "";
        }
    }

    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Ejecutable de {_g.Name}",
            Filter = "Ejecutables|*.exe;*.bat;*.cmd;*.lnk|Todos|*.*",
            InitialDirectory = Directory.Exists(_g.FolderPath) ? _g.FolderPath : null
        };
        if (dlg.ShowDialog(this) != true) return;
        var items = (List<Option>)ExeBox.ItemsSource;
        var opt = items.FirstOrDefault(o => string.Equals(o.Value, dlg.FileName, StringComparison.OrdinalIgnoreCase));
        if (opt == null)
        {
            opt = new Option { Display = Rel(dlg.FileName) + "    [elegido a mano]", Value = dlg.FileName };
            items.Insert(1, opt);
            ExeBox.ItemsSource = null; ExeBox.ItemsSource = items;
        }
        ExeBox.SelectedItem = opt;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => LauncherService.OpenFolder(_g.FolderPath);

    private void OpenRepo_Click(object sender, RoutedEventArgs e)
    {
        var repo = RepoBox.Text.Trim().Trim('/');
        if (repo.Length == 0) return;
        var host = ProviderBox.SelectedIndex == 1 ? "https://gitlab.com/" : "https://github.com/";
        LauncherService.OpenUrl(host + repo + "/releases");
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        UndoRequested = true;
        DialogResult = true;
    }

    private void InstallVersion_Click(object sender, RoutedEventArgs e)
    {
        // guarda primero lo editado (p.ej. un repositorio corregido) y luego abre el selector de versiones
        InstallVersionRequested = true;
        Save_Click(sender, e);
        if (DialogResult != true) InstallVersionRequested = false;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (!DialogService.Confirm("Restablecer detección", $"¿Olvidar el nombre, el ejecutable y el vínculo elegidos a mano para\n{_g.FolderPath}?", "Restablecer", "Cancelar"))
            return;
        ConfigService.Update(_cfg, c =>
        {
            c.NameOverrides.Remove(_g.FolderPath);
            c.LaunchOverrides.Remove(_g.FolderPath);
            c.LinkOverrides.Remove(_g.FolderPath);
        });
        Saved = true;
        DialogResult = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var repo = RepoBox.Text.Trim().Trim('/');
        if (repo.StartsWith("http", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(repo, UriKind.Absolute, out var u))
        {
            repo = u.AbsolutePath.Trim('/');
            if (repo.EndsWith("/releases", StringComparison.OrdinalIgnoreCase)) repo = repo[..^"/releases".Length];
        }
        if (repo.Length > 0 && !repo.Contains('/'))
        {
            DialogService.Info("Repositorio no válido", "Escribe el repositorio como propietario/nombre (p.ej. Zelda64Recomp/Zelda64Recomp) o pega la URL de GitHub.");
            return;
        }
        var provider = ProviderBox.SelectedIndex == 1 ? "gitlab" : "github";
        var filter = FilterBox.Text.Trim();
        var name = NameBox.Text.Trim();
        var exe = (ExeBox.SelectedItem as Option)?.Value;
        var quiver = QuiverBox.SelectedItem as Option;
        var protectedText = string.Join("; ", RecompVault.Core.GlobMatcher.Parse(ProtectedBox.Text));

        ConfigService.Update(_cfg, c =>
        {
            // nombre
            if (name.Length == 0) c.NameOverrides.Remove(_g.FolderPath);
            else if (name != _g.Name || c.NameOverrides.ContainsKey(_g.FolderPath)) c.NameOverrides[_g.FolderPath] = name;

            // ficheros protegidos del juego
            if (protectedText.Length == 0) c.ProtectedByGame.Remove(_g.FolderPath);
            else c.ProtectedByGame[_g.FolderPath] = protectedText;

            // ejecutable
            if (exe == null || exe == Auto) c.LaunchOverrides.Remove(_g.FolderPath);
            else c.LaunchOverrides[_g.FolderPath] = exe;

            // vínculo
            var detectedRepo = (_g.Repository ?? "").Trim('/');
            if (quiver?.Value == None || (quiver?.App == null && repo.Length == 0 && detectedRepo.Length > 0))
            {
                c.LinkOverrides[_g.FolderPath] = new LinkOverride { Unlinked = true };
            }
            else if (quiver?.App != null)
            {
                c.LinkOverrides[_g.FolderPath] = new LinkOverride
                {
                    QuiverFolderName = quiver.App.FolderName,
                    Repository = repo.Equals(quiver.App.Repository, StringComparison.OrdinalIgnoreCase) ? null : repo,
                    Provider = provider,
                    AssetFilter = filter.Equals(quiver.App.AssetFilter, StringComparison.Ordinal) ? null : filter
                };
            }
            else
            {
                // "(automático)": solo se guarda si el usuario cambió repositorio/filtro/proveedor;
                // si no, se deja lo que hubiera (para volver a lo automático está "Restablecer").
                var changed = !repo.Equals(detectedRepo, StringComparison.OrdinalIgnoreCase) ||
                              !filter.Equals(_g.AssetFilter ?? "", StringComparison.Ordinal) ||
                              !provider.Equals(_g.Provider ?? "github", StringComparison.OrdinalIgnoreCase);
                if (changed) c.LinkOverrides[_g.FolderPath] = new LinkOverride { Repository = repo, Provider = provider, AssetFilter = filter };
                else if (c.LinkOverrides.TryGetValue(_g.FolderPath, out var prev) && (prev.Unlinked || !string.IsNullOrWhiteSpace(prev.QuiverFolderName)))
                    c.LinkOverrides.Remove(_g.FolderPath); // se pasó de "sin vincular"/app elegida a "(automático)"
            }
        });
        Saved = true;
        DialogResult = true;
    }
}
