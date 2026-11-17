using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using RecompVault.Models;
using RecompVault.Services;

namespace RecompVault.ViewModels;

public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;
    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    { _execute = execute; _canExecute = canExecute; }
    public bool CanExecute(object? p) => _canExecute?.Invoke(p) ?? true;
    public void Execute(object? p) => _execute(p);
    public event EventHandler? CanExecuteChanged { add { CommandManager.RequerySuggested += value; } remove { CommandManager.RequerySuggested -= value; } }
}

public class MainViewModel : INotifyPropertyChanged
{
    public ObservableCollection<GameEntry> AllGames { get; } = new();
    public ObservableCollection<GameEntry> VisibleGames { get; } = new();
    public ObservableCollection<SystemItem> Systems { get; } = new();

    private string _search = "";
    public string Search { get => _search; set { _search = value; OnPropertyChanged(); ApplyFilter(); } }

    private SystemItem _selectedSystem = SystemItem.FromName("Todos");
    public SystemItem SelectedSystem { get => _selectedSystem; set { _selectedSystem = value ?? SystemItem.FromName("Todos"); OnPropertyChanged(); ApplyFilter(); } }

    private bool _favoritesOnly;
    public bool FavoritesOnly { get => _favoritesOnly; set { _favoritesOnly = value; OnPropertyChanged(); ApplyFilter(); } }

    private bool _showHidden;
    public bool ShowHidden { get => _showHidden; set { _showHidden = value; OnPropertyChanged(); RebuildSystems(); ApplyFilter(); } }

    private string _status = "Listo.";
    public string Status { get => _status; set { _status = value; OnPropertyChanged(); } }

    public AppConfig Config { get; }

    public bool HasNoVisibleGames => VisibleGames.Count == 0;
    public string AutoUpdateLabel => Config.AutoUpdateRecomps ? "Auto-update: ON" : "Auto-update: OFF";
    public int UpdatesAvailableCount => AllGames.Count(g => g.HasUpdate);
    public string UpdatesLabel => UpdatesAvailableCount > 0 ? $"↻ Updates ({UpdatesAvailableCount})" : "↻ Updates";

    public ICommand LaunchCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand RescanCommand { get; }
    public ICommand PickExeCommand { get; }
    public ICommand PickCoverCommand { get; }
    public ICommand DownloadCoverCommand { get; }
    public ICommand SearchCoverCommand { get; }
    public ICommand DownloadAllCoversCommand { get; }
    public ICommand SetApiKeyCommand { get; }
    public ICommand SetRawgKeyCommand { get; }
    public ICommand SetIgdbCommand { get; }

    public ICommand RenameGameCommand { get; }
    public ICommand HideGameCommand { get; }
    public ICommand UnhideGameCommand { get; }
    public ICommand DeleteGameCommand { get; }
    public ICommand SyncQuiverCommand { get; }
    public ICommand CheckUpdatesCommand { get; }
    public ICommand CheckUpdateCommand { get; }
    public ICommand UpdateGameCommand { get; }
    public ICommand ToggleAutoUpdateCommand { get; }

    public MainViewModel()
    {
        Config = ConfigService.Load();
        LaunchCommand = new RelayCommand(o => { if (o is GameEntry g) { LauncherService.Launch(g); ConfigService.Save(Config); Status = $"Lanzado: {g.Name}"; } });
        ToggleFavoriteCommand = new RelayCommand(o => { if (o is GameEntry g) { g.Favorite = !g.Favorite; if (g.Favorite) { if (!Config.Favorites.Contains(g.FolderPath)) Config.Favorites.Add(g.FolderPath); } else Config.Favorites.Remove(g.FolderPath); ConfigService.Save(Config); ApplyFilter(); } });
        OpenFolderCommand = new RelayCommand(o => { if (o is GameEntry g) LauncherService.OpenFolder(g.FolderPath); });
        PickExeCommand = new RelayCommand(o => { if (o is GameEntry g) PickExe(g); });
        PickCoverCommand = new RelayCommand(o => { if (o is GameEntry g) PickCover(g); });
        DownloadCoverCommand = new RelayCommand(o => { if (o is GameEntry g) DownloadCover(g); });
        SearchCoverCommand = new RelayCommand(o => { if (o is GameEntry g) SearchCover(g); });
        DownloadAllCoversCommand = new RelayCommand(_ => DownloadAllCovers());
        SetApiKeyCommand = new RelayCommand(_ => SetApiKey());
        SetRawgKeyCommand = new RelayCommand(_ => SetRawgKey());
        SetIgdbCommand = new RelayCommand(_ => SetIgdb());

        RenameGameCommand = new RelayCommand(o => { if (o is GameEntry g) RenameGame(g); });
        HideGameCommand = new RelayCommand(o => { if (o is GameEntry g) HideGame(g); });
        UnhideGameCommand = new RelayCommand(o => { if (o is GameEntry g) UnhideGame(g); });
        DeleteGameCommand = new RelayCommand(o => { if (o is GameEntry g) DeleteGame(g); });
        SyncQuiverCommand = new RelayCommand(_ => SyncQuiver());
        CheckUpdatesCommand = new RelayCommand(_ => CheckAllUpdates());
        CheckUpdateCommand = new RelayCommand(o => { if (o is GameEntry g) CheckSingleUpdate(g); });
        UpdateGameCommand = new RelayCommand(o => { if (o is GameEntry g) UpdateSingleGame(g); });
        ToggleAutoUpdateCommand = new RelayCommand(_ => { Config.AutoUpdateRecomps = !Config.AutoUpdateRecomps; ConfigService.Save(Config); Status = Config.AutoUpdateRecomps ? "Auto-actualización de recomps: ACTIVADA — se aplicará al detectar actualizaciones." : "Auto-actualización de recomps: DESACTIVADA"; OnPropertyChanged(nameof(AutoUpdateLabel)); });
        RescanCommand = new RelayCommand(_ => Rescan());
        Rescan();
    }

    private void PickExe(GameEntry g)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Selecciona el ejecutable de lanzamiento",
            Filter = "Ejecutables|*.exe;*.bat;*.cmd|Todos|*.*",
            InitialDirectory = Directory.Exists(g.FolderPath) ? g.FolderPath : null
        };
        if (dlg.ShowDialog() == true)
        {
            g.LaunchPath = dlg.FileName;
            Config.LaunchOverrides[g.FolderPath] = dlg.FileName;
            ConfigService.Save(Config);
            ArtworkService.Apply(g, Config);
            Status = $"Ejecutable actualizado: {g.Name}";
        }
    }

    private void PickCover(GameEntry g)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Selecciona la carátula/logo (imagen)",
            Filter = "Imágenes|*.png;*.jpg;*.jpeg;*.bmp;*.ico;*.webp|Todos|*.*",
            InitialDirectory = Directory.Exists(g.FolderPath) ? g.FolderPath : null
        };
        if (dlg.ShowDialog() == true)
        {
            g.CoverImage = null;
            Config.CoverOverrides[g.FolderPath] = dlg.FileName;
            ConfigService.Save(Config);
            ArtworkService.Apply(g, Config);
            Status = $"Carátula actualizada: {g.Name}";
        }
    }

    private void SearchCover(GameEntry g)
    {
        try
        {
            var dlg = new CoverPickerDialog(g, Config) { Owner = System.Windows.Application.Current?.MainWindow };
            var res = dlg.ShowDialog();
            if (res == true && dlg.Applied)
                Status = $"Portada aplicada: {g.Name}";
            else
                Status = $"Sin cambios de portada para: {g.Name}";
        }
        catch (Exception ex)
        {
            Status = "No se pudo abrir el selector de portadas: " + ex.Message;
        }
    }



    private void SetApiKey()
    {
        var key = DialogService.Prompt("SteamGridDB API key", "Pega tu API key de SteamGridDB (gratis en steamgriddb.com/api/v2). Dejar vacío para borrar.", Config.SteamGridDbApiKey ?? "");
        if (key == null) return;
        Config.SteamGridDbApiKey = key.Trim();
        ConfigService.Save(Config);
        Status = string.IsNullOrWhiteSpace(Config.SteamGridDbApiKey) ? "API key borrada." : "SteamGridDB API key guardada.";
    }

    private void SetRawgKey()
    {
        var key = DialogService.Prompt("RAWG API key", "Pega tu API key de RAWG (gratis en rawg.io/apidocs). Dejar vacío para borrar.", Config.RawgApiKey ?? "");
        if (key == null) return;
        Config.RawgApiKey = key.Trim();
        ConfigService.Save(Config);
        Status = string.IsNullOrWhiteSpace(Config.RawgApiKey) ? "RAWG key borrada." : "RAWG API key guardada.";
    }

    private void SetIgdb()
    {
        var id = DialogService.Prompt("IGDB (Twitch) Client ID", "Pega tu Client ID de IGDB/Twitch (dev.twitch.tv). Dejar vacío para borrar.", Config.IgdbClientId ?? "");
        if (id == null) return;
        var secret = DialogService.Prompt("IGDB (Twitch) Client Secret", "Pega tu Client Secret de IGDB/Twitch.", Config.IgdbClientSecret ?? "");
        if (secret == null) return;
        Config.IgdbClientId = id.Trim();
        Config.IgdbClientSecret = secret.Trim();
        ConfigService.Save(Config);
        Status = (!string.IsNullOrWhiteSpace(Config.IgdbClientId) && !string.IsNullOrWhiteSpace(Config.IgdbClientSecret)) ? "IGDB credenciales guardadas." : "IGDB credenciales borradas.";
    }

    private void DownloadCover(GameEntry g)
    {
        Status = $"Buscando portada: {g.Name}...";
        Task.Run(async () =>
        {
            var ok = await CoverDownloadService.DownloadAsync(g, Config.SteamGridDbApiKey ?? "", Config, new Progress<string>(s => DispatcherInvoke(() => Status = s)));
            DispatcherInvoke(() => Status = ok ? $"Portada encontrada: {g.Name}" : $"No se encontró portada para: {g.Name}");
        });
    }

    private void DownloadAllCovers()
    {
        var targets = AllGames.Where(g => g.CoverImage == null).ToList();
        Status = $"Buscando portadas ({targets.Count})... (SGDB → RAWG/Libretro/IGDB → Quiver)";
        Task.Run(async () =>
        {
            var n = await CoverDownloadService.DownloadAllAsync(targets, Config.SteamGridDbApiKey ?? "", Config, new Progress<string>(s => DispatcherInvoke(() => Status = s)));
            var withCover = AllGames.Count(g => g.CoverImage != null);
            DispatcherInvoke(() => Status = $"Descarga terminada: {n} nuevas. Total con portada: {withCover}/{AllGames.Count}.");
        });
    }

    private void RenameGame(GameEntry g)
    {
        var name = DialogService.Prompt("Cambiar nombre", "Nuevo nombre para el juego (deja vacío para restaurar el nombre automático):", g.Name, "Guardar");
        if (name == null) return;
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            Config.NameOverrides.Remove(g.FolderPath);
            Status = $"Nombre restaurado: {Path.GetFileName(g.FolderPath)}";
        }
        else
        {
            Config.NameOverrides[g.FolderPath] = name;
            Status = $"Nombre actualizado: {name}";
        }
        ConfigService.Save(Config);
        Rescan();
    }

    private void HideGame(GameEntry g)
    {
        if (!Config.Hidden.Contains(g.FolderPath)) Config.Hidden.Add(g.FolderPath);
        g.IsHidden = true;
        if (Config.Favorites.Contains(g.FolderPath)) { Config.Favorites.Remove(g.FolderPath); g.Favorite = false; }
        ConfigService.Save(Config);
        RebuildSystems();
        ApplyFilter();
        Status = $"Juego ocultado: {g.Name}. Activa \"Mostrar ocultos\" para gestionarlo.";
    }

    private void UnhideGame(GameEntry g)
    {
        Config.Hidden.Remove(g.FolderPath);
        g.IsHidden = false;
        ConfigService.Save(Config);
        ApplyFilter();
        RebuildSystems();
        Status = $"Juego restaurado: {g.Name}";
    }

    private void DeleteGame(GameEntry g)
    {
        if (!DialogService.Confirm("Eliminar juego", $"¿Eliminar permanentemente \"{g.Name}\" y toda su carpeta?\n\n{g.FolderPath}\n\nEsta acción no se puede deshacer.", "Eliminar", "Cancelar"))
            return;
        try
        {
            if (Directory.Exists(g.FolderPath))
                Directory.Delete(g.FolderPath, true);
            Config.Hidden.Remove(g.FolderPath);
            Config.Favorites.Remove(g.FolderPath);
            Config.LaunchOverrides.Remove(g.FolderPath);
            Config.CoverOverrides.Remove(g.FolderPath);
            Config.NameOverrides.Remove(g.FolderPath);
            ConfigService.Save(Config);
            AllGames.Remove(g);
            RebuildSystems();
            ApplyFilter();
            Status = $"Juego eliminado: {g.Name}";
        }
        catch (Exception ex)
        {
            Status = $"No se pudo eliminar: {ex.Message}";
        }
    }

    public void Rescan()
    {
        Status = "Escaneando...";
        Task.Run(() =>
        {
            var scanned = ScannerService.Scan(Config.RootPath, Config);
            DispatcherInvoke(() =>
            {
                AllGames.Clear();
                foreach (var g in scanned) AllGames.Add(g);
                RebuildSystems();
                ApplyFilter();
                var qPath = QuiverIntegrationService.ResolveQuiverPath(Config);
                Status = $"Cargados {AllGames.Count} juegos." + (string.IsNullOrEmpty(qPath) ? " (Quiver no detectado)" : " (Quiver sincronizado)");
                OnPropertyChanged(nameof(UpdatesAvailableCount)); OnPropertyChanged(nameof(UpdatesLabel));
                ConfigService.Save(Config);
                if (!Config.AutoCoverAttempted)
                {
                    Config.AutoCoverAttempted = true;
                    ConfigService.Save(Config);
                    // primera vez: intentar completar portadas (SGDB primero si hay key; si no, probar alternativas/Quiver)
                    var missing = AllGames.Where(g => g.CoverImage == null).ToList();
                    if (missing.Count > 0 && !string.IsNullOrWhiteSpace(Config.SteamGridDbApiKey))
                        DownloadAllCovers();
                    else if (missing.Count > 0)
                        _ = DownloadMissingCoversAsync(missing);
                    Status = $"Cargados {AllGames.Count} juegos. Portadas: {AllGames.Count(g => g.CoverImage != null)}/{AllGames.Count}.";
                }
                if (Config.AutoCheckUpdates)
                    CheckAllUpdates(silent: true);
            });
        });
    }

    private async Task DownloadMissingCoversAsync(List<GameEntry> missing)
    {
        int n = 0;
        foreach (var g in missing)
        {
            if (g.CoverImage != null) continue;
            var ok = await CoverDownloadService.DownloadAsync(g, Config, new Progress<string>(s => DispatcherInvoke(() => Status = s)));
            if (ok) n++;
        }
        if (n > 0) DispatcherInvoke(() => Status = $"Portadas aplicadas: {n}. Total: {AllGames.Count(x => x.CoverImage != null)}/{AllGames.Count}.");
    }

    private void SyncQuiver()
    {
        var qPath = QuiverIntegrationService.ResolveQuiverPath(Config);
        if (string.IsNullOrEmpty(qPath))
        {
            // ask user to pick
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Selecciona la carpeta de QuiverLauncher (portable)" };
            if (dlg.ShowDialog() == true && File.Exists(Path.Combine(dlg.FolderName, "apps.json")))
            {
                Config.QuiverPath = dlg.FolderName;
                qPath = dlg.FolderName;
                ConfigService.Save(Config);
            }
            else { Status = "No se encontró Quiver (apps.json)."; return; }
        }
        Status = "Sincronizando con Quiver...";
        Task.Run(() =>
        {
            try
            {
                var apps = QuiverIntegrationService.LoadApps(qPath);
                var versions = QuiverIntegrationService.LoadVersionCache(qPath);
                var catalog = QuiverIntegrationService.LoadCatalogIndex(qPath);
                int matched = 0, covers = 0;
                DispatcherInvoke(() =>
                {
                    foreach (var g in AllGames)
                    {
                        var info = QuiverIntegrationService.Match(g, apps);
                        if (info != null)
                        {
                            matched++;
                            if (!Config.NameOverrides.ContainsKey(g.FolderPath) && !string.IsNullOrWhiteSpace(info.Name)) g.Name = info.Name;
                            QuiverIntegrationService.Enrich(g, info, qPath);
                        }
                        else
                        {
                            var ce = QuiverIntegrationService.MatchCatalog(g, catalog);
                            if (ce != null)
                            {
                                matched++;
                                QuiverIntegrationService.EnrichFromCatalog(g, ce);
                                if (!string.IsNullOrWhiteSpace(ce.FolderName))
                                {
                                    var icon = QuiverIntegrationService.FindLocalIcon(qPath, ce.FolderName, g.FolderPath);
                                    if (!string.IsNullOrWhiteSpace(icon)) g.QuiverLocalIconPath = icon;
                                }
                            }
                        }
                        QuiverIntegrationService.ApplyUpdateInfo(g, versions);
                    }
                    ApplyFilter();
                });
                foreach (var g in AllGames.ToList())
                {
                    if (g.CoverImage != null) continue;
                    if (string.IsNullOrWhiteSpace(g.QuiverAppIconUrl) && string.IsNullOrWhiteSpace(g.QuiverLocalIconPath)) continue;
                    var ok = CoverDownloadService.DownloadAsync(g, Config, null).GetAwaiter().GetResult();
                    if (ok) covers++;
                }
                DispatcherInvoke(() => {
                    OnPropertyChanged(nameof(UpdatesAvailableCount)); OnPropertyChanged(nameof(UpdatesLabel));
                    ApplyFilter();
                    Status = $"Quiver: {matched} juegos reconocidos (apps+catálogo), {covers} portadas importadas. Actualizaciones detectadas: {AllGames.Count(x => x.HasUpdate)}.";
                });
            }
            catch (Exception ex) { DispatcherInvoke(() => Status = "Error sincronizando Quiver: " + ex.Message); }
        });
    }

    private void CheckAllUpdates(bool silent = false)
    {
        var qPath = QuiverIntegrationService.ResolveQuiverPath(Config) ?? "";
        var targets = AllGames.Where(g => !string.IsNullOrWhiteSpace(g.Repository) || !string.IsNullOrWhiteSpace(qPath)).ToList();
        if (!silent) Status = $"Comprobando actualizaciones ({targets.Count})...";
        Task.Run(async () =>
        {
            int checkedN = 0;
            foreach (var g in targets)
            {
                await UpdateService.CheckUpdateAsync(g, qPath);
                checkedN++;
                if (!silent && checkedN % 25 == 0) DispatcherInvoke(() => Status = $"Comprobando actualizaciones... {checkedN}/{targets.Count}");
            }
            DispatcherInvoke(() =>
            {
                OnPropertyChanged(nameof(UpdatesAvailableCount));
                OnPropertyChanged(nameof(UpdatesLabel));
                ApplyFilter();
                var updates = AllGames.Count(x => x.HasUpdate);
                var actionable = AllGames.Count(x => x.HasUpdate && !string.IsNullOrWhiteSpace(x.UpdateAssetUrl));
                if (!silent || updates > 0)
                    Status = updates > 0
                        ? $"Actualizaciones disponibles: {updates} (instalables ahora: {actionable}). Auto-update: {(Config.AutoUpdateRecomps ? "ON" : "OFF")}."
                        : "No hay actualizaciones disponibles.";
                if (!silent && updates > 0)
                {
                    var sample = string.Join(", ", AllGames.Where(x => x.HasUpdate).Take(5).Select(x => $"{x.Name} ({x.LatestVersion})"));
                    if (AllGames.Count(x => x.HasUpdate) > 5) sample += ", ...";
                    DialogService.Info("Actualizaciones encontradas", $"Se han detectado {updates} recomps con actualización.\nInstalables automáticamente ahora: {actionable}.\n\n{sample}");
                }
                if (Config.AutoUpdateRecomps && actionable > 0) AutoUpdateAll();
            });
        });
    }

    private void CheckSingleUpdate(GameEntry g)
    {
        var qPath = QuiverIntegrationService.ResolveQuiverPath(Config) ?? "";
        Status = $"Comprobando actualización: {g.Name}...";
        Task.Run(async () =>
        {
            await UpdateService.CheckUpdateAsync(g, qPath);
            DispatcherInvoke(() => Status = g.HasUpdate ? $"Actualización disponible: {g.Name} {g.InstalledVersion} → {g.LatestVersion}" : $"Sin actualización: {g.Name}");
        });
    }

    private void UpdateSingleGame(GameEntry g)
    {
        if (g.IsUpdating) { Status = $"Ya se está actualizando: {g.Name}"; return; }
        // Always check/refresh so we have latest asset and clear feedback
        if (string.IsNullOrWhiteSpace(g.UpdateAssetUrl) || string.IsNullOrWhiteSpace(g.Repository) == false)
        {
            // proceed to check then update attempt
        }
        var qPath = QuiverIntegrationService.ResolveQuiverPath(Config) ?? "";
        Status = $"Preparando actualización: {g.Name}...";
        Task.Run(async () =>
        {
            await UpdateService.CheckUpdateAsync(g, qPath);
            // try to resolve asset if update known
            if (g.HasUpdate && string.IsNullOrWhiteSpace(g.UpdateAssetUrl) && !string.IsNullOrWhiteSpace(g.Repository))
            {
                // UpdateAsync will also try refresh, but set status
            }
            if (!g.HasUpdate)
            {
                DispatcherInvoke(() => { OnPropertyChanged(nameof(UpdatesAvailableCount)); OnPropertyChanged(nameof(UpdatesLabel)); Status = $"No hay actualización disponible para {g.Name}." + (string.IsNullOrWhiteSpace(g.Repository) ? " (no reconocido con repositorio de Quiver/catálogo)" : ""); });
                return;
            }
            if (string.IsNullOrWhiteSpace(g.UpdateAssetUrl) && string.IsNullOrWhiteSpace(g.Repository))
            {
                DispatcherInvoke(() => { Status = $"Actualización detectada para {g.Name} pero no hay repositorio/asset conocido para descargar."; });
                return;
            }

            DispatcherInvoke(() => { g.IsUpdating = true; Status = $"Actualizando {g.Name} a {g.LatestVersion}..."; });
            var ok = await UpdateService.UpdateAsync(g, new Progress<string>(s => DispatcherInvoke(() => Status = s)));
            DispatcherInvoke(() =>
            {
                g.IsUpdating = false;
                OnPropertyChanged(nameof(UpdatesAvailableCount)); OnPropertyChanged(nameof(UpdatesLabel));
                ApplyFilter();
                if (ok) Status = $"Actualizado: {g.Name} → {g.LatestVersion}";
                else
                {
                    Status = $"No se pudo actualizar {g.Name}. " + (string.IsNullOrWhiteSpace(g.UpdateAssetUrl) ? "Sin asset de Windows descargable." : "Revisa el estado/log.");
                    DialogService.Info("Actualización no completada", $"No se pudo actualizar \"{g.Name}\".\n\nRepositorio: {(string.IsNullOrWhiteSpace(g.Repository) ? "(no reconocido)" : g.Repository)}\nVersión detectada: {g.LatestVersion}\nAsset: {(string.IsNullOrWhiteSpace(g.UpdateAssetName) ? "(no encontrado)" : g.UpdateAssetName)}\n\nMotivos habituales: sin paquete de Windows en el release, límite de la API de GitHub (403), repositorio no disponible (404), o el paquete requiere instalación manual.\n\nLog: %TEMP%\\recompvault_updates.log");
                }
            });
        });
    }

    private void AutoUpdateAll()
    {
        var list = AllGames.Where(g => g.HasUpdate && !string.IsNullOrWhiteSpace(g.UpdateAssetUrl) && !g.IsUpdating).ToList();
        if (list.Count == 0) { Status = "Hay actualizaciones detectadas, pero sin asset de Windows instalable automáticamente para esas entradas."; return; }
        Status = $"Auto-actualizando {list.Count} recomps...";
        Task.Run(async () =>
        {
            int okc = 0; int i = 0;
            foreach (var g in list)
            {
                i++;
                DispatcherInvoke(() => { g.IsUpdating = true; Status = $"Actualizando ({i}/{list.Count}): {g.Name} → {g.LatestVersion}..."; });
                var ok = await UpdateService.UpdateAsync(g, new Progress<string>(s => DispatcherInvoke(() => Status = $"({i}/{list.Count}) {s}")));
                if (ok) okc++;
                DispatcherInvoke(() => g.IsUpdating = false);
            }
            DispatcherInvoke(() =>
            {
                OnPropertyChanged(nameof(UpdatesAvailableCount)); OnPropertyChanged(nameof(UpdatesLabel));
                ApplyFilter();
                Status = $"Auto-actualización completada: {okc}/{list.Count} recomps actualizadas.";
                DialogService.Info("Auto-actualización", $"Se han actualizado {okc} de {list.Count} recomps.");
            });
        });
    }

    private static void DispatcherInvoke(Action a)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d != null && !d.CheckAccess()) d.Invoke(a);
        else a();
    }

    private void RebuildSystems()
    {
        var current = _selectedSystem?.Name ?? "Todos";
        Systems.Clear();
        var visiblePool = AllGames.Where(g => ShowHidden || !g.IsHidden).ToList();
        Systems.Add(SystemItem.FromName("Todos", visiblePool.Count));
        foreach (var grp in visiblePool.GroupBy(g => g.System).OrderBy(g => g.Key))
        {
            var item = SystemItem.FromName(grp.Key, grp.Count());
            SystemLogoService.Apply(item);
            Systems.Add(item);
        }
        var sel = Systems.FirstOrDefault(s => s.Name == current) ?? Systems.FirstOrDefault();
        if (sel != null) SelectedSystem = sel;
    }

    public void ApplyFilter()
    {
        VisibleGames.Clear();
        foreach (var g in AllGames)
        {
            if (!ShowHidden && g.IsHidden) continue;
            if (FavoritesOnly && !g.Favorite) continue;
            if (SelectedSystem != null && SelectedSystem.Name != "Todos" && g.System != SelectedSystem.Name) continue;
            if (!string.IsNullOrWhiteSpace(Search))
            {
                var q = Search.ToLowerInvariant();
                if (!(g.Name.ToLowerInvariant().Contains(q) || g.System.ToLowerInvariant().Contains(q) || g.FolderPath.ToLowerInvariant().Contains(q) || (g.RecompName ?? "").ToLowerInvariant().Contains(q)))
                    continue;
            }
            VisibleGames.Add(g);
        }
        OnPropertyChanged(nameof(HasNoVisibleGames));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}