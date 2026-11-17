using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using RecompVault.Core;
using RecompVault.Models;
using RecompVault.Services;
using RecompVault.ViewModels;

namespace RecompVault;

/// <summary>Explorador del catálogo de Quiver: novedades, búsqueda e instalación directa en la biblioteca.</summary>
public partial class ExploreDialog : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public sealed class Item : INotifyPropertyChanged
    {
        public CatalogApp App { get; init; } = null!;
        private ImageSource? _image;
        public ImageSource? Image { get => _image; set { _image = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image))); } }
        public string Consoles => App.Consoles.Count == 0 ? (App.ProjectType.Length > 0 ? App.ProjectType : "—") : string.Join(" · ", App.Consoles);
        public string Badge { get; init; } = "";
        public string BadgeColor { get; init; } = "#D0342C";
        public bool HasBadge => Badge.Length > 0;
        public string DateText { get; init; } = "";
        public GameEntry? Installed { get; init; }
        public bool IsInstalled => Installed != null;
        public string InstalledText => Installed == null ? "" :
            Installed.HasUpdate ? $"✔ Instalado · ⟳ {Installed.LatestVersion}" :
            "✔ Instalado" + (Installed.InstalledUnknown ? "" : " " + Installed.InstalledVersion);
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly MainViewModel _vm;
    private readonly List<Item> _all = new();
    private string? _cursor;
    private CancellationTokenSource _cts = new();
    private static readonly Dictionary<string, ImageSource> ImageCache = new();
    private static readonly SemaphoreSlim ImageGate = new(6);
    private bool _loading;

    public ExploreDialog(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        SourceInitialized += (_, _) =>
        {
            try { int d = 1; var h = new WindowInteropHelper(this).Handle; if (DwmSetWindowAttribute(h, 20, ref d, 4) != 0) DwmSetWindowAttribute(h, 19, ref d, 4); } catch { }
        };
        Closed += (_, _) => _cts.Cancel();
        SystemBox.ItemsSource = vm.Systems.Select(s => s.Name).Where(n => n != "Todos" && n != "Quiver").ToList();
        FolderBox.TextChanged += (_, _) => UpdateTarget();
        SystemBox.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => UpdateTarget()));
        SystemBox.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(new Action(UpdateTarget));
        Loaded += async (_, _) => { SortBox.SelectedIndex = 0; await Task.CompletedTask; };
    }

    // ------------------------------------------------------------------ carga

    private string Sort => (SortBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "updated";

    private async void Sort_Changed(object sender, SelectionChangedEventArgs e) => await ReloadAsync();

    private async void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        _all.Clear();
        _cursor = null;
        await LoadPageAsync(_cts.Token);
    }

    private async void More_Click(object sender, RoutedEventArgs e) => await LoadPageAsync(_cts.Token);

    private async Task LoadPageAsync(CancellationToken ct)
    {
        if (_loading) return;
        _loading = true;
        MoreBtn.IsEnabled = false;
        StatusText.Text = "Cargando catálogo...";
        try
        {
            var page = await QuiverCatalogService.GetAppsAsync(Sort, SearchBox.Text, _cursor, ct);
            if (ct.IsCancellationRequested) return;
            _cursor = page.NextCursor;
            var now = DateTime.Now;
            foreach (var app in page.Items)
            {
                if (_all.Any(x => x.App.Slug == app.Slug)) continue;
                var isNew = app.AddedAt.HasValue && (now - app.AddedAt.Value).TotalDays <= 14;
                var isUpd = app.LastReleaseAt.HasValue && (now - app.LastReleaseAt.Value).TotalDays <= 14;
                _all.Add(new Item
                {
                    App = app,
                    Badge = isNew ? "NUEVO" : isUpd ? "ACTUALIZADO" : "",
                    BadgeColor = isNew ? "#D0342C" : "#2E7D32",
                    DateText = DateLine(app, now),
                    Installed = FindInstalled(app)
                });
            }
            RefreshConsoles();
            ApplyFilter();
            MoreBtn.IsEnabled = _cursor != null;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText.Text = "No se pudo cargar el catálogo: " + ex.Message;
            Log.Warn("Catálogo de Quiver: " + ex.Message);
        }
        finally { _loading = false; }
    }

    private static string DateLine(CatalogApp a, DateTime now)
    {
        static string Ago(DateTime d, DateTime now)
        {
            var days = (int)(now - d).TotalDays;
            return days <= 0 ? "hoy" : days == 1 ? "ayer" : days < 30 ? $"hace {days} días" : d.ToString("dd/MM/yyyy");
        }
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(a.LastReleaseVersion)) parts.Add(a.LastReleaseVersion!);
        if (a.LastReleaseAt.HasValue) parts.Add(Ago(a.LastReleaseAt.Value, now));
        else if (a.AddedAt.HasValue) parts.Add("añadido " + Ago(a.AddedAt.Value, now));
        return string.Join(" · ", parts);
    }

    private GameEntry? FindInstalled(CatalogApp a)
    {
        var folder = a.FolderName;
        return _vm.AllGames.FirstOrDefault(g =>
            (!string.IsNullOrWhiteSpace(g.QuiverFolderName) && string.Equals(g.QuiverFolderName, folder, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(Path.GetFileName(g.FolderPath), folder, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshConsoles()
    {
        var current = ConsoleBox.SelectedItem as string;
        var consoles = new List<string> { "Todas las consolas" };
        consoles.AddRange(_all.SelectMany(x => x.App.Consoles).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x));
        ConsoleBox.SelectionChanged -= Filter_Changed;
        ConsoleBox.ItemsSource = consoles;
        ConsoleBox.SelectedItem = current != null && consoles.Contains(current) ? current : consoles[0];
        ConsoleBox.SelectionChanged += Filter_Changed;
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (List == null) return;
        IEnumerable<Item> q = _all;
        var console = ConsoleBox.SelectedItem as string;
        if (console != null && ConsoleBox.SelectedIndex > 0) q = q.Where(x => x.App.Consoles.Contains(console, StringComparer.OrdinalIgnoreCase));
        if (HideOwnedBox.IsChecked == true) q = q.Where(x => !x.IsInstalled);
        if (WindowsBox.IsChecked == true) q = q.Where(x => x.App.RunsOnWindows);
        var list = q.ToList();
        List.ItemsSource = list;
        var owned = _all.Count(x => x.IsInstalled);
        StatusText.Text = $"{list.Count} de {_all.Count} apps cargadas · {owned} ya en tu biblioteca" + (_cursor != null ? " · hay más (Cargar más)" : "");
        foreach (var it in list.Where(i => i.Image == null)) _ = LoadImageAsync(it);
    }

    private async Task LoadImageAsync(Item it)
    {
        var url = it.App.BestArt;
        if (string.IsNullOrWhiteSpace(url)) return;
        lock (ImageCache) if (ImageCache.TryGetValue(url, out var c)) { it.Image = c; return; }
        await ImageGate.WaitAsync();
        try
        {
            var bytes = await CoverDownloadService.Http.GetByteArrayAsync(url, _cts.Token);
            var img = ImageLoader.Load(bytes, 340);
            if (img == null) return;
            lock (ImageCache) ImageCache[url] = img;
            UiThread.Run(() => it.Image = img);
        }
        catch { }
        finally { ImageGate.Release(); }
    }

    // ------------------------------------------------------------------ detalle

    private Item? Selected => List.SelectedItem as Item;

    private async void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var it = Selected;
        DetailPanel.Visibility = it == null ? Visibility.Collapsed : Visibility.Visible;
        EmptyDetail.Visibility = it == null ? Visibility.Visible : Visibility.Collapsed;
        if (it == null) return;
        var a = it.App;
        DName.Text = a.Name;
        DSub.Text = string.Join(" · ", new[] { string.Join(", ", a.Consoles), a.ProjectType, a.Developer ?? "", a.Recommended > 0 ? $"👍 {a.Recommended}" : "" }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        DVersions.Text = $"Última:     {a.LastReleaseVersion ?? "—"}{(a.LastReleaseAt.HasValue ? "  (" + a.LastReleaseAt.Value.ToString("dd/MM/yyyy") + ")" : "")}\n" +
                         $"Verificada: {a.VerifiedVersion ?? "—"}\n" +
                         $"Windows:    {(a.RunsOnWindows ? "sí" : "no")}" + (string.IsNullOrWhiteSpace(a.AiLevel) || a.AiLevel == "none" ? "" : $"\nIA:         {a.AiLevel}");
        DInstalled.Text = it.Installed == null ? "" : $"{it.InstalledText} en {it.Installed.FolderPath}";
        DDesc.Text = a.Description;
        InstallBtn.Content = it.IsInstalled ? "⇩ Instalar otra copia..." : "⇩ Elegir versión e instalar";
        SystemBox.Text = SystemMapper.Suggest(a.Consoles, _vm.Systems.Select(s => s.Name)) ?? "PC";
        FolderBox.Text = PathUtil.SafeFileName(string.IsNullOrWhiteSpace(a.FolderName) ? a.Slug : a.FolderName, 80);
        UpdateTarget();

        HeroImage.Source = null;
        var heroUrl = a.Hero ?? a.Header ?? a.BestArt;
        if (!string.IsNullOrWhiteSpace(heroUrl))
        {
            try
            {
                var bytes = await CoverDownloadService.Http.GetByteArrayAsync(heroUrl, _cts.Token);
                if (Selected == it) HeroImage.Source = ImageLoader.Load(bytes, 800);
            }
            catch { }
        }
    }

    private string TargetPath() => Path.Combine(_vm.Config.RootPath, PathUtil.SafeFileName(SystemBox.Text?.Trim() ?? "PC", 40), PathUtil.SafeFileName(FolderBox.Text?.Trim() ?? "", 80));

    private void UpdateTarget()
    {
        if (TargetText == null) return;
        var target = TargetPath();
        TargetText.Text = Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()
            ? $"⚠ Ya existe y tiene contenido: {target} (se instalará encima, con copia para deshacer)"
            : $"Se instalará en: {target}";
    }

    private void OpenSite_Click(object sender, RoutedEventArgs e) { if (Selected is { } it) LauncherService.OpenUrl(QuiverCatalogService.AppPage(it.App.Slug)); }

    private async void OpenReleases_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } it) return;
        try
        {
            var p = await QuiverCatalogService.GetProjectAsync(it.App.Slug);
            if (p == null) { StatusText.Text = "El catálogo no indica el repositorio de esta app."; return; }
            LauncherService.OpenUrl((p.Provider == "gitlab" ? "https://gitlab.com/" : "https://github.com/") + p.Repository + "/releases");
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } it) return;
        if (string.IsNullOrWhiteSpace(SystemBox.Text) || string.IsNullOrWhiteSpace(FolderBox.Text))
        { StatusText.Text = "Indica el sistema y la carpeta."; return; }
        if (!Directory.Exists(_vm.Config.RootPath))
        { DialogService.Info("Instalar", "La carpeta raíz no existe. Configúrala en ⚙ Ajustes."); return; }

        InstallBtn.IsEnabled = false;
        StatusText.Text = "Consultando el repositorio en el catálogo...";
        try
        {
            var project = await QuiverCatalogService.GetProjectAsync(it.App.Slug, _cts.Token);
            if (project == null) { StatusText.Text = "El catálogo no indica el repositorio de esta app."; return; }
            var target = TargetPath();
            var temp = new GameEntry
            {
                Name = it.App.Name, System = SystemBox.Text.Trim(), FolderPath = target, RecompName = Path.GetFileName(target),
                Repository = project.Repository, Provider = project.Provider, AssetFilter = it.App.AssetFilter, QuiverFolderName = it.App.FolderName
            };
            var dlg = new VersionPickerDialog(temp, _vm.Config, preselectTag: it.App.VerifiedVersion ?? it.App.LastReleaseVersion, installText: "Instalar") { Owner = this };
            if (dlg.ShowDialog() != true || dlg.Choice == null) { StatusText.Text = "Instalación cancelada."; return; }
            _vm.InstallFromCatalog(temp, dlg.Choice, dlg.Pin);
            StatusText.Text = $"Instalando {it.App.Name} en {target}... (sigue el progreso en la ventana principal)";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = "Error: " + ex.Message; }
        finally { InstallBtn.IsEnabled = true; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
