using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using RecompVault.Models;
using RecompVault.Services;
using RecompVault.ViewModels;

namespace RecompVault;

/// <summary>Tabla con la detección de todos los juegos para revisarla y corregirla en bloque.</summary>
public partial class ReviewDialog : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private readonly MainViewModel _vm;

    public ReviewDialog(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        SourceInitialized += (_, _) =>
        {
            try { int d = 1; var h = new WindowInteropHelper(this).Handle; if (DwmSetWindowAttribute(h, 20, ref d, 4) != 0) DwmSetWindowAttribute(h, 19, ref d, 4); } catch { }
        };
        // por defecto, lo que más interesa revisar
        var fuzzyCount = vm.AllGames.Count(g => g.IsFuzzyLink);
        FilterBox.SelectedIndex = fuzzyCount > 0 ? 1 : 0;
        Refresh();
    }

    private void Refresh()
    {
        if (Grid == null || FilterBox == null) return;
        var tag = (FilterBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        var q = SearchBox?.Text?.Trim() ?? "";
        IEnumerable<GameEntry> list = _vm.AllGames;
        list = tag switch
        {
            "fuzzy" => list.Where(g => g.IsFuzzyLink),
            "unlinked" => list.Where(g => string.IsNullOrWhiteSpace(g.Repository)),
            "unknown" => list.Where(g => g.InstalledUnknown),
            "update" => list.Where(g => g.HasUpdate),
            "oldrv" => list.Where(g => g.UpdatedByOldVersion),
            "noexe" => list.Where(g => string.IsNullOrWhiteSpace(g.LaunchPath)),
            "pinned" => list.Where(g => g.IsPinned),
            "unverified" => list.Where(g => !string.IsNullOrWhiteSpace(g.Repository) && !g.LinkSource.StartsWith("Verificado")),
            "fpbad" => list.Where(g => g.Fingerprint.StartsWith("✖")),
            _ => list
        };
        if (q.Length > 0)
            list = list.Where(g => g.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || g.FolderPath.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                   (g.Repository ?? "").Contains(q, StringComparison.OrdinalIgnoreCase));
        var items = list.OrderBy(g => g.System, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        Grid.ItemsSource = items;
        CountText.Text = $"{items.Count} de {_vm.AllGames.Count} juegos · " +
                         $"≈ aproximados: {_vm.AllGames.Count(g => g.IsFuzzyLink)} · sin vincular: {_vm.AllGames.Count(g => string.IsNullOrWhiteSpace(g.Repository))} · " +
                         $"versión ?: {_vm.AllGames.Count(g => g.InstalledUnknown)} · RV antiguo: {_vm.AllGames.Count(g => g.UpdatedByOldVersion)}";
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => Refresh();

    private List<GameEntry> Selected() => Grid.SelectedItems.OfType<GameEntry>().ToList();

    private void Detail_Click(object sender, RoutedEventArgs e)
    {
        var g = Selected().FirstOrDefault();
        if (g == null) { CountText.Text = "Selecciona un juego."; return; }
        _vm.OpenDetection(g);
    }

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Grid.SelectedItem is GameEntry g) _vm.OpenDetection(g);
    }

    private void Verify_Click(object sender, RoutedEventArgs e)
    {
        var sel = Selected();
        if (sel.Count == 0) sel = (Grid.ItemsSource as IEnumerable<GameEntry>)?.ToList() ?? new();
        if (sel.Count == 0) return;
        if (sel.Count > 1 && !DialogService.Confirm("Verificar por huella",
                $"¿Verificar {sel.Count} juegos contra sus releases? Puede tardar unos minutos; el progreso sale en la barra de estado de la ventana principal.",
                "Verificar", "Cancelar"))
            return;
        _vm.VerifyMany(sel);
        CountText.Text = $"Verificando {sel.Count} juegos... (progreso en la ventana principal; esta tabla se actualiza al volver a ella)";
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var sel = Selected().Where(g => !string.IsNullOrWhiteSpace(g.Repository)).ToList();
        if (sel.Count == 0) { CountText.Text = "Selecciona juegos vinculados a un repositorio."; return; }
        if (!DialogService.Confirm("Confirmar vínculos",
                $"¿Dar por buenos los vínculos de {sel.Count} juegos?\n\n" + string.Join("\n", sel.Take(12).Select(g => $"• {g.Name} → {g.Repository}")) +
                (sel.Count > 12 ? $"\n… y {sel.Count - 12} más" : "") + "\n\nPasarán a ser vínculos manuales y podrán auto-actualizarse.",
                "Confirmar", "Cancelar"))
            return;
        foreach (var g in sel) { _vm.ConfirmLink(g); g.RaiseDetectionChanged(); }
        Refresh();
    }

    private void Unlink_Click(object sender, RoutedEventArgs e)
    {
        var sel = Selected();
        if (sel.Count == 0) { CountText.Text = "Selecciona algún juego."; return; }
        if (!DialogService.Confirm("Desvincular", $"¿Dejar sin vincular {sel.Count} juegos? No se buscarán actualizaciones para ellos (se puede revertir en ℹ → Restablecer).",
                "Desvincular", "Cancelar"))
            return;
        foreach (var g in sel) { _vm.UnlinkManually(g); g.RaiseDetectionChanged(); }
        Refresh();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "recompvault_deteccion.csv", Filter = "CSV|*.csv" };
        if (dlg.ShowDialog(this) != true) return;
        var sb = new StringBuilder();
        sb.AppendLine("Nombre;Sistema;Avisos;Vinculo;Repositorio;Proveedor;FiltroPaquete;Instalada;Ultima;PaqueteInstalado;Ejecutable;Carpeta");
        static string Q(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        foreach (var g in _vm.AllGames.OrderBy(x => x.System).ThenBy(x => x.Name))
            sb.AppendLine(string.Join(";", new[] { g.Name, g.System, g.ReviewFlags, g.LinkSource, g.Repository, g.Provider, g.AssetFilter, g.InstalledVersion,
                g.LatestVersion, g.InstalledAssetName, g.LaunchPath, g.FolderPath }.Select(Q)));
        try
        {
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            CountText.Text = "Exportado: " + dlg.FileName;
        }
        catch (Exception ex) { CountText.Text = "No se pudo exportar: " + ex.Message; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        Refresh(); // al volver de la ventana de detección
    }
}
