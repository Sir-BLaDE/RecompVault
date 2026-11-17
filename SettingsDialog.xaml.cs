using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using RecompVault.Core;
using RecompVault.Services;

namespace RecompVault;

/// <summary>Todos los ajustes en una ventana (antes eran botones sueltos).</summary>
public partial class SettingsDialog : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private readonly AppConfig _cfg;

    /// <summary>Cambió algo que obliga a re-escanear (carpeta raíz, Quiver...).</summary>
    public bool NeedsRescan { get; private set; }

    public SettingsDialog(AppConfig cfg)
    {
        InitializeComponent();
        _cfg = cfg;
        SourceInitialized += (_, _) =>
        {
            try { int d = 1; var h = new WindowInteropHelper(this).Handle; if (DwmSetWindowAttribute(h, 20, ref d, 4) != 0) DwmSetWindowAttribute(h, 19, ref d, 4); } catch { }
        };
        lock (ConfigService.Sync)
        {
            RootBox.Text = cfg.RootPath;
            QuiverEnabledBox.IsChecked = cfg.QuiverSyncEnabled;
            QuiverBox.Text = cfg.QuiverPath;
            ExternalBox.IsChecked = cfg.IncludeQuiverAppsOutsideRoot;
            FuzzyBox.IsChecked = cfg.FuzzyQuiverMatching;
            AutoCheckBox.IsChecked = cfg.AutoCheckUpdates;
            AutoUpdateBox.IsChecked = cfg.AutoUpdateRecomps;
            PreBox.IsChecked = cfg.IncludePrereleases;
            ChangelogBox.IsChecked = cfg.ConfirmUpdatesWithChangelog;
            IntervalBox.Text = cfg.UpdateCheckIntervalHours.ToString();
            UndoKeepBox.Text = cfg.UndoBackupsToKeep.ToString();
            ProtectedBox.Text = string.Join("; ", cfg.ProtectedPatterns);
            GitHubBox.Text = cfg.GitHubToken ?? "";
            SgdbBox.Text = cfg.SteamGridDbApiKey ?? "";
            RawgBox.Text = cfg.RawgApiKey ?? "";
            IgdbIdBox.Text = cfg.IgdbClientId ?? "";
            IgdbSecretBox.Text = cfg.IgdbClientSecret ?? "";
        }
        UpdateQuiverHint();
        QuiverBox.TextChanged += (_, _) => UpdateQuiverHint();
    }

    private void UpdateQuiverHint()
    {
        var resolved = QuiverLibrary.ResolveRoot(QuiverBox.Text);
        QuiverHint.Text = string.IsNullOrWhiteSpace(QuiverBox.Text) ? "Vacío = detectar automáticamente."
            : resolved != null ? $"✔ Quiver encontrado: {resolved}" : "✖ No hay apps.json en esa carpeta.";
    }

    private void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Carpeta raíz de recomps/ports" };
        if (Directory.Exists(RootBox.Text)) dlg.InitialDirectory = RootBox.Text;
        if (dlg.ShowDialog(this) == true) RootBox.Text = dlg.FolderName;
    }

    private void BrowseQuiver_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Carpeta de Quiver Launcher (la que contiene apps.json)" };
        if (dlg.ShowDialog(this) == true) QuiverBox.Text = QuiverLibrary.ResolveRoot(dlg.FolderName) ?? dlg.FolderName;
    }

    private void DetectQuiver_Click(object sender, RoutedEventArgs e)
    {
        var found = QuiverLibrary.Detect(null, RootBox.Text);
        if (found != null) QuiverBox.Text = found;
        else QuiverHint.Text = "No se ha encontrado Quiver junto a la carpeta raíz ni en %LOCALAPPDATA%.";
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ConfigService.Dir);
        LauncherService.OpenFolder(ConfigService.Dir);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(IntervalBox.Text.Trim(), out var interval) || interval < 0 || interval > 720)
        { DialogService.Info("Ajustes", "El intervalo debe ser un número de horas entre 0 y 720."); return; }
        if (!int.TryParse(UndoKeepBox.Text.Trim(), out var keep) || keep < 1 || keep > 10)
        { DialogService.Info("Ajustes", "Las copias de deshacer deben estar entre 1 y 10."); return; }
        var root = RootBox.Text.Trim().Trim('"');
        if (!Directory.Exists(root) &&
            !DialogService.Confirm("Carpeta raíz", $"La carpeta no existe:\n{root}\n\n¿Guardar igualmente?", "Guardar", "Cancelar"))
            return;

        static string? N(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var quiver = QuiverBox.Text.Trim().Trim('"');
        ConfigService.Update(_cfg, c =>
        {
            NeedsRescan = !string.Equals(c.RootPath, root, StringComparison.OrdinalIgnoreCase) ||
                          !string.Equals(c.QuiverPath, quiver, StringComparison.OrdinalIgnoreCase) ||
                          c.QuiverSyncEnabled != (QuiverEnabledBox.IsChecked == true) ||
                          c.IncludeQuiverAppsOutsideRoot != (ExternalBox.IsChecked == true) ||
                          c.FuzzyQuiverMatching != (FuzzyBox.IsChecked == true);
            c.RootPath = root;
            c.QuiverPath = QuiverLibrary.ResolveRoot(quiver) ?? quiver;
            c.QuiverSyncEnabled = QuiverEnabledBox.IsChecked == true;
            c.IncludeQuiverAppsOutsideRoot = ExternalBox.IsChecked == true;
            c.FuzzyQuiverMatching = FuzzyBox.IsChecked == true;
            c.AutoCheckUpdates = AutoCheckBox.IsChecked == true;
            c.AutoUpdateRecomps = AutoUpdateBox.IsChecked == true;
            c.IncludePrereleases = PreBox.IsChecked == true;
            c.ConfirmUpdatesWithChangelog = ChangelogBox.IsChecked == true;
            c.UpdateCheckIntervalHours = interval;
            c.UndoBackupsToKeep = keep;
            c.ProtectedPatterns = GlobMatcher.Parse(ProtectedBox.Text);
            c.GitHubToken = N(GitHubBox.Text);
            c.SteamGridDbApiKey = N(SgdbBox.Text);
            c.RawgApiKey = N(RawgBox.Text);
            c.IgdbClientId = N(IgdbIdBox.Text);
            c.IgdbClientSecret = N(IgdbSecretBox.Text);
        });
        if (NeedsRescan) QuiverLibrary.InvalidateCache();
        DialogResult = true;
    }
}
