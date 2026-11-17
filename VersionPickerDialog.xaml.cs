using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using RecompVault.Core;
using RecompVault.Models;
using RecompVault.Services;

namespace RecompVault;

/// <summary>Lista los releases del repositorio vinculado y deja elegir versión y paquete para instalar.</summary>
public partial class VersionPickerDialog : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public sealed class ReleaseRow
    {
        public UpdateService.ReleaseInfo Release { get; init; } = null!;
        public string Header { get; init; } = "";
        public string Detail { get; init; } = "";
    }

    public sealed class AssetRow
    {
        public ReleaseAsset Asset { get; init; } = null!;
        public string Display { get; init; } = "";
    }

    private readonly GameEntry _g;
    private readonly AppConfig _cfg;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Elección del usuario (null si canceló).</summary>
    public UpdateService.InstallChoice? Choice { get; private set; }
    public bool Pin => PinBox.IsChecked == true;

    private readonly string? _preselect;

    /// <param name="preselectTag">Versión a seleccionar al abrir (por defecto la última conocida).</param>
    /// <param name="installText">Texto del botón de instalar.</param>
    public VersionPickerDialog(GameEntry g, AppConfig cfg, string? preselectTag = null, string? installText = null)
    {
        InitializeComponent();
        _g = g; _cfg = cfg; _preselect = preselectTag;
        if (!string.IsNullOrWhiteSpace(installText)) InstallBtn.Content = installText;
        SourceInitialized += (_, _) =>
        {
            try { int d = 1; var h = new WindowInteropHelper(this).Handle; if (DwmSetWindowAttribute(h, 20, ref d, 4) != 0) DwmSetWindowAttribute(h, 19, ref d, 4); } catch { }
        };
        Title = $"Instalar una versión concreta — {g.Name}";
        HeaderText.Text = g.Name;
        SubText.Text = $"{(g.Provider == "gitlab" ? "GitLab" : "GitHub")}: {g.Repository}   ·   instalada: {(g.InstalledUnknown ? "desconocida" : g.InstalledVersion)}" +
                       (string.IsNullOrWhiteSpace(g.AssetFilter) ? "" : $"   ·   filtro de paquete: {g.AssetFilter}") +
                       "\nSi el repositorio no es el de este juego, cámbialo antes en ℹ (Detección). La instalación se puede deshacer con ↶.";
        PinBox.IsChecked = g.IsPinned;
        InstallBtn.IsEnabled = false;
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => _cts.Cancel();
    }

    private async Task LoadAsync()
    {
        StatusText.Text = "Cargando releases...";
        try
        {
            var (releases, error) = await UpdateService.ListReleasesAsync(_g.Provider, _g.Repository!, _cfg, _cts.Token);
            if (error != null) { StatusText.Text = error; return; }
            if (releases.Count == 0) { StatusText.Text = "Este repositorio no tiene releases publicados."; return; }

            var rows = releases.Select(r =>
            {
                var best = ReleaseAssetPicker.Pick(r.Assets, _g.AssetFilter, _g.InstalledAssetName);
                var tags = new List<string>();
                if (VersionUtil.Equivalent(r.Tag, _g.InstalledVersion)) tags.Add("INSTALADA");
                if (r.Prerelease) tags.Add("prerelease");
                if (best == null) tags.Add("sin paquete de Windows");
                return new ReleaseRow
                {
                    Release = r,
                    Header = r.Tag + (tags.Count > 0 ? "   [" + string.Join(", ", tags) + "]" : ""),
                    Detail = $"{(r.Published?.ToLocalTime().ToString("dd/MM/yyyy") ?? "sin fecha")} · {r.Title} · {r.Assets.Count} ficheros"
                };
            }).ToList();
            ReleaseList.ItemsSource = rows;
            StatusText.Text = $"{rows.Count} releases. Elige uno y el paquete a instalar.";
            // preselección: el siguiente más nuevo que la instalada, o el primero
            ReleaseList.SelectedItem = rows.FirstOrDefault(x => VersionUtil.Equivalent(x.Release.Tag, _preselect ?? _g.LatestVersion)) ?? rows[0];
            ReleaseList.ScrollIntoView(ReleaseList.SelectedItem);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = "Error: " + ex.Message; }
    }

    private void ReleaseList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReleaseList.SelectedItem is not ReleaseRow row) { InstallBtn.IsEnabled = false; return; }
        var r = row.Release;
        // misma versión que la instalada: el mismo paquete exacto si existe (reparar)
        var best = (VersionUtil.Equivalent(r.Tag, _g.InstalledVersion) && !string.IsNullOrWhiteSpace(_g.InstalledAssetName)
                       ? r.Assets.FirstOrDefault(a => string.Equals(a.Name, _g.InstalledAssetName, StringComparison.OrdinalIgnoreCase)) : null)
                   ?? ReleaseAssetPicker.Pick(r.Assets, _g.AssetFilter, _g.InstalledAssetName);
        var assets = r.Assets
            .Select(a => (a, score: ReleaseAssetPicker.Score(a.Name)))
            .OrderByDescending(x => ReferenceEquals(x.a, best)).ThenByDescending(x => x.score)
            .Select(x => new AssetRow
            {
                Asset = x.a,
                Display = $"{x.a.Name}{(x.a.Size > 0 ? $"   ({x.a.Size / 1048576.0:0.#} MB)" : "")}" +
                          (ReferenceEquals(x.a, best) ? "   ← recomendado" : x.score < 0 ? "   (no parece de Windows)" : "")
            }).ToList();
        AssetBox.ItemsSource = assets;
        AssetBox.SelectedIndex = assets.Count > 0 ? 0 : -1;
        AssetHint.Text = best != null
            ? "Recomendado según el filtro de paquete de Quiver, el paquete instalado y la plataforma (Windows x64)."
            : assets.Count == 0 ? "Este release no tiene ficheros adjuntos." : "No se ha encontrado un paquete claro de Windows; elige uno a mano con cuidado.";
        var notes = (r.Notes ?? "").Trim();
        NotesText.Text = notes.Length == 0 ? "(sin notas)" : notes.Length > 20000 ? notes[..20000] + "\n…" : notes;
        InstallBtn.IsEnabled = assets.Count > 0;
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        if (ReleaseList.SelectedItem is not ReleaseRow row || AssetBox.SelectedItem is not AssetRow asset) return;
        if (ReleaseAssetPicker.Score(asset.Asset.Name) < 0 &&
            !DialogService.Confirm("Paquete dudoso", $"\"{asset.Asset.Name}\" no parece un paquete de Windows instalable. ¿Instalarlo igualmente?", "Instalar", "Cancelar"))
            return;
        Choice = new UpdateService.InstallChoice(row.Release.Tag, asset.Asset);
        DialogResult = true;
    }
}
