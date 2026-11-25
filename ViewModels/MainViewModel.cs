using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using RecompVault.Core;
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
    public void Execute(object? p)
    {
        // Un error en un comando nunca debe cerrar la aplicación.
        try { _execute(p); }
        catch (Exception ex)
        {
            Log.Error("Error en comando", ex);
            DialogService.Info("Error", "Ha ocurrido un error inesperado:\n\n" + ex.Message + "\n\nDetalles en: " + Log.FilePath);
        }
    }
    public event EventHandler? CanExecuteChanged { add { CommandManager.RequerySuggested += value; } remove { CommandManager.RequerySuggested -= value; } }
}

/// <summary>ObservableCollection que puede sustituir todo su contenido con una sola notificación (Reset).</summary>
public class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        var list = items as IList<T> ?? items.ToList();
        if (list.Count == Items.Count && list.SequenceEqual(Items)) return;
        Items.Clear();
        foreach (var i in list) Items.Add(i);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

/// <summary>Una fila de la cuadrícula virtualizada (solo se dibujan las filas visibles).</summary>
public sealed class GameRow
{
    public List<GameEntry> Items { get; init; } = new();
}

public class MainViewModel : INotifyPropertyChanged
{
    /// <summary>Filas de la cuadrícula (VisibleGames troceado según el ancho de la ventana).</summary>
    public RangeObservableCollection<GameRow> Rows { get; } = new();

    private int _columns = 5;
    /// <summary>Tarjetas por fila; lo fija la vista al cambiar de tamaño.</summary>
    public int Columns
    {
        get => _columns;
        set { var v = Math.Max(1, value); if (v == _columns) return; _columns = v; OnPropertyChanged(); RebuildRows(); }
    }

    private GameEntry? _selectedGame;
    public GameEntry? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (ReferenceEquals(_selectedGame, value)) return;
            if (_selectedGame != null) _selectedGame.IsSelected = false;
            _selectedGame = value;
            if (value != null) value.IsSelected = true;
            OnPropertyChanged();
            var idx = value == null ? -1 : VisibleGames.IndexOf(value);
            if (idx >= 0) ScrollToRowRequested?.Invoke(idx / Columns);
        }
    }

    /// <summary>La vista desplaza la cuadrícula hasta esta fila.</summary>
    public event Action<int>? ScrollToRowRequested;

    private bool _isCouchMode;
    /// <summary>Modo sofá: pantalla completa, interfaz simplificada y navegación con mando.</summary>
    public bool IsCouchMode { get => _isCouchMode; set { _isCouchMode = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsDeskMode)); } }
    public bool IsDeskMode => !IsCouchMode;

    private void RebuildRows()
    {
        var rows = new List<GameRow>();
        for (int i = 0; i < VisibleGames.Count; i += Columns)
            rows.Add(new GameRow { Items = VisibleGames.Skip(i).Take(Columns).ToList() });
        Rows.ReplaceAll(rows);
        if (SelectedGame != null && !VisibleGames.Contains(SelectedGame)) SelectedGame = null;
    }

    /// <summary>Mueve la selección (teclado/mando). dx = columnas, dy = filas.</summary>
    public void MoveSelection(int dx, int dy)
    {
        if (VisibleGames.Count == 0) return;
        var idx = SelectedGame == null ? -1 : VisibleGames.IndexOf(SelectedGame);
        if (idx < 0) { SelectedGame = VisibleGames[0]; return; }
        var next = idx + dx + dy * Columns;
        if (dy != 0 && (next < 0 || next >= VisibleGames.Count))
            next = dy > 0 ? Math.Min(VisibleGames.Count - 1, idx + (VisibleGames.Count - 1 - idx < Columns ? VisibleGames.Count - 1 - idx : Columns)) : Math.Max(0, idx % Columns);
        SelectedGame = VisibleGames[Math.Clamp(next, 0, VisibleGames.Count - 1)];
    }

    /// <summary>Sistema anterior/siguiente (LB/RB o RePág/AvPág).</summary>
    public void CycleSystem(int delta)
    {
        if (Systems.Count == 0) return;
        var i = Systems.IndexOf(SelectedSystem);
        SelectedSystem = Systems[(i + delta + Systems.Count) % Systems.Count];
        if (VisibleGames.Count > 0) SelectedGame = VisibleGames[0];
    }

    public void LaunchSelected() { if (SelectedGame is { } g && g.CanLaunch) LaunchCommand.Execute(g); }
    public void ToggleFavoriteSelected() { if (SelectedGame is { } g) ToggleFavoriteCommand.Execute(g); }
    public void DetailsSelected() { if (SelectedGame is { } g) ShowDetectionCommand.Execute(g); }

    public SystemItem? HeroSystem => SelectedSystem;
    public RangeObservableCollection<GameEntry> AllGames { get; } = new();
    public RangeObservableCollection<GameEntry> VisibleGames { get; } = new();
    public RangeObservableCollection<SystemItem> Systems { get; } = new();

    public IReadOnlyList<string> SortModes { get; } = new[] { "Sistema", "Nombre", "Jugados recientemente", "Con actualización" };

    private readonly DispatcherTimer _searchTimer;
    private CancellationTokenSource? _scanCts;
    private int _scanGeneration;
    private bool _checkingUpdates;
    private bool _autoUpdating;
    private bool _rebuildingSystems;

    private string _search = "";
    public string Search
    {
        get => _search;
        set { if (_search == value) return; _search = value; OnPropertyChanged(); _searchTimer.Stop(); _searchTimer.Start(); }
    }

    private SystemItem _selectedSystem = SystemItem.FromName("Todos");
    public SystemItem SelectedSystem
    {
        get => _selectedSystem;
        set
        {
            if (_rebuildingSystems) return; // el ListBox pone null al reconstruir la lista
            var v = value ?? Systems.FirstOrDefault() ?? SystemItem.FromName("Todos");
            if (ReferenceEquals(_selectedSystem, v)) return;
            _selectedSystem = v; OnPropertyChanged(); ApplyFilter();
        }
    }

    /// <summary>Tiles de sistema compactos (caben más a la vista).</summary>
    public bool CompactSystems
    {
        get => Config.CompactSystems;
        set { if (Config.CompactSystems == value) return; ConfigService.Update(Config, c => c.CompactSystems = value); OnPropertyChanged(); }
    }

    private bool _favoritesOnly;
    public bool FavoritesOnly { get => _favoritesOnly; set { _favoritesOnly = value; OnPropertyChanged(); ApplyFilter(); } }

    private bool _showHidden;
    public bool ShowHidden { get => _showHidden; set { _showHidden = value; OnPropertyChanged(); RebuildSystems(); ApplyFilter(); } }

    private string _sortMode = "Sistema";
    public string SortMode { get => _sortMode; set { _sortMode = value ?? "Sistema"; OnPropertyChanged(); ApplyFilter(); } }

    private string _status = "Listo.";
    public string Status { get => _status; set { _status = value; OnPropertyChanged(); } }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasNoVisibleGames)); } }

    public AppConfig Config { get; }

    public bool HasNoVisibleGames => VisibleGames.Count == 0 && !IsBusy;
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
    public ICommand SetGitHubTokenCommand { get; }
    public ICommand RenameGameCommand { get; }
    public ICommand HideGameCommand { get; }
    public ICommand UnhideGameCommand { get; }
    public ICommand DeleteGameCommand { get; }
    public ICommand SyncQuiverCommand { get; }
    public ICommand CheckUpdatesCommand { get; }
    public ICommand CheckUpdateCommand { get; }
    public ICommand UpdateGameCommand { get; }
    public ICommand ToggleAutoUpdateCommand { get; }
    public ICommand OpenRepositoryCommand { get; }
    public ICommand OpenLogCommand { get; }
    public ICommand ShowDetectionCommand { get; }
    public ICommand UndoUpdateCommand { get; }
    public ICommand InstallVersionCommand { get; }
    public ICommand RepairCommand { get; }
    public ICommand CancelUpdateCommand { get; }
    public ICommand ExploreCommand { get; }
    public ICommand VerifyFingerprintCommand { get; }
    public ICommand DesktopShortcutCommand { get; }
    public ICommand AddToSteamCommand { get; }
    public ICommand OpenSelfUpdateCommand { get; }

    private string? _selfUpdateUrl;
    private string _selfUpdateLabel = "";
    /// <summary>Texto del aviso de versión nueva de RecompVault (vacío = no hay).</summary>
    public string SelfUpdateLabel { get => _selfUpdateLabel; set { _selfUpdateLabel = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasSelfUpdate)); } }
    public bool HasSelfUpdate => !string.IsNullOrEmpty(SelfUpdateLabel);
    public ICommand UndoManagerCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand ReviewDetectionCommand { get; }

    public MainViewModel()
    {
        Config = ConfigService.Load();
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); ApplyFilter(); };
        VisibleGames.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoVisibleGames));

        LaunchCommand = new RelayCommand(o => { if (o is GameEntry g) Launch(g); });
        ToggleFavoriteCommand = new RelayCommand(o => { if (o is GameEntry g) ToggleFavorite(g); });
        OpenFolderCommand = new RelayCommand(o => { if (o is GameEntry g) { var e = LauncherService.OpenFolder(g.FolderPath); if (e != null) Status = e; } });
        PickExeCommand = new RelayCommand(o => { if (o is GameEntry g) PickExe(g); });
        PickCoverCommand = new RelayCommand(o => { if (o is GameEntry g) PickCover(g); });
        DownloadCoverCommand = new RelayCommand(o => { if (o is GameEntry g) DownloadCover(g); });
        SearchCoverCommand = new RelayCommand(o => { if (o is GameEntry g) SearchCover(g); });
        DownloadAllCoversCommand = new RelayCommand(_ => DownloadAllCovers());
        SetApiKeyCommand = new RelayCommand(_ => PromptKey("SteamGridDB API key", "Pega tu API key de SteamGridDB (gratis en steamgriddb.com/profile/preferences/api). Vacío para borrar.", c => c.SteamGridDbApiKey, (c, v) => c.SteamGridDbApiKey = v));
        SetRawgKeyCommand = new RelayCommand(_ => PromptKey("RAWG API key", "Pega tu API key de RAWG (gratis en rawg.io/apidocs). Vacío para borrar.", c => c.RawgApiKey, (c, v) => c.RawgApiKey = v));
        SetIgdbCommand = new RelayCommand(_ => SetIgdb());
        SetGitHubTokenCommand = new RelayCommand(_ => PromptKey("Token de GitHub",
            "Opcional. Con un token (github.com/settings/tokens, sin permisos, tipo 'fine-grained' de solo lectura pública) el límite de la API pasa de 60 a 5000 consultas/hora. Vacío para borrar.",
            c => c.GitHubToken, (c, v) => c.GitHubToken = v));
        RenameGameCommand = new RelayCommand(o => { if (o is GameEntry g) RenameGame(g); });
        HideGameCommand = new RelayCommand(o => { if (o is GameEntry g) HideGame(g); });
        UnhideGameCommand = new RelayCommand(o => { if (o is GameEntry g) UnhideGame(g); });
        DeleteGameCommand = new RelayCommand(o => { if (o is GameEntry g) DeleteGame(g); });
        SyncQuiverCommand = new RelayCommand(_ => SyncQuiver());
        CheckUpdatesCommand = new RelayCommand(_ => CheckAllUpdates(silent: false));
        CheckUpdateCommand = new RelayCommand(o => { if (o is GameEntry g) CheckSingleUpdate(g); });
        UpdateGameCommand = new RelayCommand(o => { if (o is GameEntry g) UpdateSingleGame(g); });
        ToggleAutoUpdateCommand = new RelayCommand(_ => ToggleAutoUpdate());
        OpenRepositoryCommand = new RelayCommand(o => { if (o is GameEntry g) OpenRepository(g); });
        OpenLogCommand = new RelayCommand(_ => { if (File.Exists(Log.FilePath)) LauncherService.OpenUrl(Log.FilePath); else Status = "Aún no hay registro."; });
        ShowDetectionCommand = new RelayCommand(o => { if (o is GameEntry g) ShowDetection(g); });
        UndoUpdateCommand = new RelayCommand(o => { if (o is GameEntry g) UndoUpdate(g); }, o => o is GameEntry g && g.HasUndo && !g.IsUpdating);
        InstallVersionCommand = new RelayCommand(o => { if (o is GameEntry g) InstallVersion(g); }, o => o is GameEntry g && !g.IsUpdating);
        RepairCommand = new RelayCommand(o => { if (o is GameEntry g) Repair(g); }, o => o is GameEntry g && !g.IsUpdating);
        ReviewDetectionCommand = new RelayCommand(_ => ReviewDetection());
        CancelUpdateCommand = new RelayCommand(o => { if (o is GameEntry g) CancelUpdate(g); });
        ExploreCommand = new RelayCommand(_ =>
        {
            var open = System.Windows.Application.Current.Windows.OfType<ExploreDialog>().FirstOrDefault();
            if (open != null) { open.Activate(); return; }
            new ExploreDialog(this) { Owner = System.Windows.Application.Current.MainWindow }.Show();
        });
        DesktopShortcutCommand = new RelayCommand(o =>
        {
            if (o is not GameEntry g) return;
            var err = IntegrationService.CreateDesktopShortcut(g, Config);
            Status = err == null ? $"Acceso directo creado en el escritorio: {g.Name}" : $"No se pudo crear el acceso directo: {err}";
        });
        AddToSteamCommand = new RelayCommand(o => { if (o is GameEntry g) AddToSteam(g); });
        VerifyFingerprintCommand = new RelayCommand(o => { if (o is GameEntry g) VerifyMany(new[] { g }, interactive: true); });
        OpenSelfUpdateCommand = new RelayCommand(_ =>
        {
            if (_selfUpdateRelease != null) SelfUpdateService.Prompt(_selfUpdateRelease, Config, manual: true);
            else if (_selfUpdateUrl != null) LauncherService.OpenUrl(_selfUpdateUrl);
        });
        UndoManagerCommand = new RelayCommand(_ => { var d = new UndoManagerDialog(this) { Owner = ActiveOwner() }; d.ShowDialog(); });
        SettingsCommand = new RelayCommand(_ => OpenSettings());
        RescanCommand = new RelayCommand(_ => Rescan(full: Keyboard.Modifiers.HasFlag(ModifierKeys.Control)));
    }

    private void Repair(GameEntry g)
    {
        if (string.IsNullOrWhiteSpace(g.Repository)) { InstallVersion(g); return; }
        if (g.InstalledUnknown)
        {
            DialogService.Info("Reparar", $"No se sabe qué versión de \"{g.Name}\" está instalada, así que no se puede reinstalar \"la misma\".\n\nElige la versión a mano en la lista que se abre a continuación.");
            InstallVersion(g);
            return;
        }
        var dlg = new VersionPickerDialog(g, Config, preselectTag: g.InstalledVersion, installText: "Reinstalar")
        { Owner = ActiveOwner(), Title = $"Reparar (reinstalar {g.InstalledVersion}) — {g.Name}" };
        if (dlg.ShowDialog() != true || dlg.Choice == null) return;
        StartUpdate(g, dlg.Choice, pin: dlg.Pin ? true : (g.IsPinned ? null : false));
    }

    /// <summary>Ventana activa (para que los diálogos abiertos desde otro diálogo queden encima).</summary>
    private static System.Windows.Window? ActiveOwner()
    {
        var app = System.Windows.Application.Current;
        return app?.Windows.OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive && w.IsVisible) ?? app?.MainWindow;
    }

    private bool _verifying;

    /// <summary>Verifica por huella uno o varios juegos (en segundo plano, con progreso en la barra de estado).</summary>
    public void VerifyMany(IReadOnlyList<GameEntry> games, bool interactive = false)
    {
        if (_verifying) { Status = "Ya hay una verificación en curso."; return; }
        if (games.Count == 0) return;
        _verifying = true;
        var lib = QuiverService.Load(Config);
        Task.Run(async () =>
        {
            int ok = 0, no = 0, i = 0;
            FingerprintResult? last = null;
            foreach (var g in games)
            {
                i++;
                var n = i;
                var candidates = FingerprintService.CandidatesFor(g, lib, includeSuggestions: string.IsNullOrWhiteSpace(g.Repository));
                FingerprintResult r;
                try
                {
                    r = await FingerprintService.VerifyAsync(g, candidates, Config,
                        new Progress<string>(s => UiThread.Run(() => Status = games.Count > 1 ? $"Huella ({n}/{games.Count}) {g.Name}: {s}" : s)));
                }
                catch (Exception ex) { r = new FingerprintResult(false, "", null, null, null, ex.Message); }
                last = r;
                if (r.Matched)
                {
                    ok++;
                    FingerprintService.Apply(g, r, Config);
                    UiThread.RunSync(() => g.Fingerprint = "✔ " + r.Version);
                    await RefreshDetection(g, lib);
                }
                else
                {
                    no++;
                    UiThread.RunSync(() => g.Fingerprint = candidates.Count == 0 ? "" : "✖ no coincide");
                    Log.Info($"Huella sin coincidencia {g.Name}: {r.Message}");
                }
            }
            UiThread.Run(() =>
            {
                _verifying = false;
                RaiseUpdateCounts();
                if (games.Count == 1 && last != null)
                {
                    var g = games[0];
                    Status = last.Matched ? $"✔ {g.Name}: {last.Message}" : $"✖ {g.Name}: {last.Message}";
                    if (interactive)
                        DialogService.Info(last.Matched ? "Verificado por huella" : "Sin coincidencia",
                            last.Matched
                                ? $"{g.Name}\n\n{last.Message}\n\nEl vínculo queda verificado (seguro para auto-actualizar) y la versión instalada es {last.Version}."
                                : $"{g.Name}\n\n{last.Message}\n\nSi el vínculo actual es erróneo, cámbialo en ℹ (Detección) o márcalo \"sin vincular\".");
                }
                else Status = $"Verificación por huella: {ok} confirmados, {no} sin coincidencia (detalles en el registro).";
            });
        });
    }

    private void AddToSteam(GameEntry g)
    {
        if (IntegrationService.SteamPath() == null) { DialogService.Info("Steam", "No se ha encontrado Steam en este equipo."); return; }
        var users = IntegrationService.SteamUsers();
        if (users.Count == 0) { DialogService.Info("Steam", "No hay ningún usuario de Steam con carpeta de datos. Inicia sesión en Steam una vez."); return; }
        if (IntegrationService.SteamRunning() &&
            !DialogService.Confirm("Steam está abierto", "Steam sobrescribe sus accesos al cerrarse. Cierra Steam por completo antes de continuar.\n\n¿Ya lo has cerrado?", "Continuar", "Cancelar"))
            return;
        var user = users[0];
        if (users.Count > 1)
        {
            var list = string.Join("\n", users.Select((u, i) => $"{i + 1}. {u.Name} ({u.Id})"));
            var pick = DialogService.Prompt("Usuario de Steam", $"¿En qué usuario lo añado?\n{list}", "1", "Añadir");
            if (pick == null) return;
            if (!int.TryParse(pick.Trim(), out var n) || n < 1 || n > users.Count) { Status = "Usuario de Steam no válido."; return; }
            user = users[n - 1];
        }
        var err = IntegrationService.AddToSteam(g, Config, user);
        Status = err == null ? $"Añadido a Steam ({user.Name}): {g.Name}. Abre Steam para verlo en la biblioteca." : $"No se pudo añadir a Steam: {err}";
        if (err != null) DialogService.Info("Steam", err);
    }

    /// <summary>Instala una app del catálogo de Quiver en una carpeta nueva y la vincula.</summary>
    public void InstallFromCatalog(GameEntry temp, UpdateService.InstallChoice choice, bool pin)
    {
        temp.IsUpdating = true;
        var cts = BeginUpdateCts(temp);
        Status = $"Instalando {temp.Name} {choice.Version}...";
        Task.Run(async () =>
        {
            var res = await UpdateService.UpdateAsync(temp, Config, new Progress<string>(s => UiThread.Run(() => Status = s)), cts.Token, choice);
            UiThread.Run(() =>
            {
                EndUpdateCts(temp);
                temp.IsUpdating = false;
                if (!res.Ok)
                {
                    Status = res.Message;
                    if (!cts.IsCancellationRequested) DialogService.Info("Instalación no completada", $"{temp.Name}\n\n{res.Message}");
                    return;
                }
                ConfigService.Update(Config, c =>
                {
                    c.NameOverrides[temp.FolderPath] = temp.Name;
                    c.LinkOverrides[temp.FolderPath] = new LinkOverride
                    {
                        Repository = temp.Repository, Provider = temp.Provider, AssetFilter = temp.AssetFilter ?? ""
                    };
                    if (pin) c.PinnedVersions[temp.FolderPath] = choice.Version;
                });
                Status = $"Instalado: {temp.Name} {choice.Version} en {temp.FolderPath}";
                Rescan();
            });
        });
    }

    private void OpenSettings()
    {
        var dlg = new SettingsDialog(Config) { Owner = ActiveOwner() };
        if (dlg.ShowDialog() != true) return;
        OnPropertyChanged(nameof(AutoUpdateLabel));
        if (dlg.NeedsRescan || dlg.EmporiumChanged) Rescan();
        else Status = "Ajustes guardados.";
    }

    private void ReviewDetection()
    {
        var dlg = new ReviewDialog(this) { Owner = ActiveOwner() };
        dlg.ShowDialog();
    }

    /// <summary>Abre la ventana de detección de un juego (también desde la revisión en bloque).</summary>
    public void OpenDetection(GameEntry g) => ShowDetection(g);

    /// <summary>Convierte un vínculo detectado (p.ej. aproximado) en uno manual: pasa a ser "seguro".</summary>
    public void ConfirmLink(GameEntry g)
    {
        if (string.IsNullOrWhiteSpace(g.Repository)) return;
        ConfigService.Update(Config, c => c.LinkOverrides[g.FolderPath] = new LinkOverride
        {
            QuiverFolderName = string.IsNullOrWhiteSpace(g.QuiverFolderName) ? null : g.QuiverFolderName,
            Repository = g.Repository, Provider = g.Provider, AssetFilter = g.AssetFilter ?? ""
        });
        g.LinkSource = "Manual: vínculo confirmado";
    }

    public void UnlinkManually(GameEntry g)
    {
        ConfigService.Update(Config, c => c.LinkOverrides[g.FolderPath] = new LinkOverride { Unlinked = true });
        QuiverService.Unlink(g);
        g.LinkSource = "Manual: sin vincular";
        RaiseUpdateCounts();
    }

    private void InstallVersion(GameEntry g)
    {
        if (string.IsNullOrWhiteSpace(g.Repository))
        {
            DialogService.Info("Sin repositorio", $"\"{g.Name}\" no está vinculado a ningún repositorio.\n\nVincúlalo primero con ℹ (Detección): elige la app de Quiver o escribe propietario/repositorio.");
            return;
        }
        var dlg = new VersionPickerDialog(g, Config) { Owner = ActiveOwner() };
        if (dlg.ShowDialog() != true || dlg.Choice == null) return;
        StartUpdate(g, dlg.Choice, pin: dlg.Pin);
    }

    // ------------------------------------------------------------------ detección manual / deshacer

    private async void ShowDetection(GameEntry g)
    {
        var lib = QuiverService.Load(Config);
        var dlg = new DetectionDialog(g, Config, lib) { Owner = ActiveOwner() };
        if (dlg.ShowDialog() != true) return;
        if (dlg.UndoRequested) { UndoUpdate(g); return; }
        if (dlg.VerifyRequested)
        {
            if (dlg.Saved) await RefreshDetection(g, lib);
            VerifyMany(new[] { g }, interactive: true);
            return;
        }
        if (dlg.InstallVersionRequested)
        {
            if (dlg.Saved) await RefreshDetection(g, lib); // el vínculo nuevo tiene que estar aplicado antes de listar releases
            InstallVersion(g);
            return;
        }
        if (dlg.Saved) await RefreshDetection(g, lib);
    }

    /// <summary>Vuelve a detectar un solo juego (nombre, exe, vínculo, versiones) tras cambios manuales.</summary>
    private Task RefreshDetection(GameEntry g, QuiverLibrary? lib)
    {
        Status = $"Aplicando cambios en {g.Name}...";
        var others = AllGames.Where(x => !ReferenceEquals(x, g) && !string.IsNullOrWhiteSpace(x.QuiverFolderName))
                             .Select(x => x.QuiverFolderName!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Task.Run(async () =>
        {
            var folderName = Path.GetFileName(g.FolderPath);
            var (launch, launchSource) = ScannerService.ResolveLaunchWithSource(g.FolderPath, folderName, Config);
            var auto = ScannerService.AutoName(folderName);
            var probe = new GameEntry { FolderPath = g.FolderPath, Name = auto.name, NameSource = auto.source, NameFromTitleMap = auto.fromMap, System = g.System };
            if (lib != null)
            {
                var taken = lib.Apps.Where(a => others.Contains(a.FolderName)).ToHashSet();
                var exact = QuiverMatcher.MatchExact(lib, g.FolderPath);
                var m = exact ?? (Config.FuzzyQuiverMatching ? QuiverMatcher.Suggest(lib, g.FolderPath, auto.name, taken) : null);
                if (m != null) QuiverService.Enrich(probe, m, Config, allowRename: exact != null || !auto.fromMap);
            }
            QuiverService.ApplyLinkOverride(probe, lib, Config);
            var custom = ConfigService.Get(Config.NameOverrides, g.FolderPath);

            UiThread.RunSync(() =>
            {
                QuiverService.Unlink(g);
                g.Repository = probe.Repository; g.Provider = probe.Provider; g.AssetFilter = probe.AssetFilter;
                g.Project = probe.Project; g.QuiverFolderName = probe.QuiverFolderName;
                g.QuiverAppIconUrl = probe.QuiverAppIconUrl; g.QuiverLocalIconPath = probe.QuiverLocalIconPath;
                g.LinkSource = string.IsNullOrWhiteSpace(probe.Repository) && probe.LinkSource == "Sin vincular" ? "Sin vincular" : probe.LinkSource;
                if (!string.IsNullOrWhiteSpace(custom)) { g.Name = custom; g.NameSource = "Manual"; }
                else { g.Name = probe.Name; g.NameSource = probe.NameSource; g.NameFromTitleMap = probe.NameFromTitleMap; }
                g.LaunchPath = launch; g.LaunchSource = launchSource;
            });
            if (!string.IsNullOrWhiteSpace(g.Repository))
                await UpdateService.CheckUpdateAsync(g, lib, Config, force: true);
            else
                UiThread.RunSync(() => g.InstalledVersion = UpdateService.ReadInstalledVersion(g.UpdateDir) ?? "");
            ArtworkService.Apply(g, Config);
            UiThread.Run(() =>
            {
                RaiseUpdateCounts();
                ApplyFilter();
                g.RaiseDetectionChanged();
                Status = $"Detección actualizada: {g.Name} · {g.LinkSource}" + (string.IsNullOrWhiteSpace(g.Repository) ? "" : $" ({g.Repository})");
            });
        });
    }

    public void UndoUpdate(GameEntry g)
    {
        var m = UndoStore.Latest(g.UpdateDir);
        if (m == null) { Status = $"{g.Name}: no hay ninguna actualización que deshacer."; g.HasUndo = false; return; }
        if (!DialogService.Confirm("Deshacer actualización",
                $"¿Deshacer la actualización de \"{g.Name}\" del {m.CreatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}?\n\n" +
                $"{(m.FromVersion ?? "versión desconocida")}  ←  {m.ToVersion}  ({m.AssetName})\n\n" +
                $"Se restaurarán {m.Replaced.Count} ficheros y se borrarán {m.Created.Count} que añadió la actualización.\n" +
                "Las partidas y ajustes que no tocó la actualización no cambian.",
                "Deshacer", "Cancelar"))
            return;
        g.IsUpdating = true;
        Status = $"Deshaciendo la actualización de {g.Name}...";
        Task.Run(() =>
        {
            var res = UpdateService.UndoLastUpdate(g, Config);
            UiThread.Run(() =>
            {
                g.IsUpdating = false;
                RaiseUpdateCounts();
                Status = res.Message;
                if (!res.Ok) DialogService.Info("No se pudo deshacer", $"{g.Name}\n\n{res.Message}");
                else if (Config.AutoUpdateRecomps && g.HasUpdate)
                    DialogService.Info("Auto-update activado",
                        "Este juego vuelve a tener una actualización pendiente y el auto-update la reinstalaría.\n" +
                        "Si el vínculo era erróneo, ábrelo con ℹ (Detección) y cámbialo o márcalo \"sin vincular\".");
            });
        });
    }

    // ------------------------------------------------------------------ escaneo

    /// <summary>
    /// Arranque: muestra al instante la biblioteca guardada en caché, carga sus portadas en segundo plano
    /// y después escanea solo para corregir lo que haya cambiado (juegos nuevos, borrados o actualizados).
    /// </summary>
    public void Startup()
    {
        var cached = LibraryCacheService.Load(Config.RootPath, Config);
        if (cached.Count == 0) { Rescan(afterStartup: true); return; }

        AllGames.ReplaceAll(cached.OrderBy(g => g.System, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase));
        RebuildSystems();
        ApplyFilter();
        RaiseUpdateCounts();
        Status = $"{AllGames.Count} juegos (última sesión) · comprobando cambios...";
        Rescan(afterStartup: true, artworkFirst: cached);
    }

    /// <param name="full">Escaneo desde cero: no reutiliza nada del escaneo anterior (Ctrl+F5).</param>
    public void Rescan(bool afterStartup = false, bool full = false, IReadOnlyList<GameEntry>? artworkFirst = null)
    {
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        var gen = Interlocked.Increment(ref _scanGeneration);
        IsBusy = true;
        if (artworkFirst == null) Status = full ? "Escaneando biblioteca desde cero..." : "Comprobando cambios en la biblioteca...";
        var root = Config.RootPath;
        // instantánea de lo que hay ahora para reutilizar carpetas sin cambios
        var previous = full ? null : AllGames.GroupBy(g => PathUtil.Norm(g.FolderPath)).ToDictionary(x => x.Key, x => x.First());

        Task.Run(async () =>
        {
            QuiverLibrary? lib = null;
            List<GameEntry> scanned;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (artworkFirst != null) await LibraryCacheService.LoadArtworkAsync(artworkFirst, Config, cts.Token);
                if (cts.IsCancellationRequested) return;
                if (full) QuiverMatcher.ClearCache();
                lib = QuiverService.Load(Config);
                scanned = ScannerService.Scan(root, Config, lib, previous);
                foreach (var g in scanned) QuiverService.ApplyKnownLatest(g, lib);
                Log.Info($"Escaneo {(full ? "completo" : "incremental")}: {scanned.Count} juegos en {sw.ElapsedMilliseconds} ms");
            }
            catch (Exception ex)
            {
                Log.Error("Error escaneando", ex);
                UiThread.Run(() => { IsBusy = false; Status = "Error al escanear: " + ex.Message; });
                return;
            }
            if (cts.IsCancellationRequested || gen != _scanGeneration) return;

            UiThread.Run(() =>
            {
                if (gen != _scanGeneration) return;
                var (added, removed) = MergeScan(scanned);
                IsBusy = false;
                RebuildSystems();
                ApplyFilter();
                RaiseUpdateCounts();

                var withCover = AllGames.Count(g => g.HasCover);
                var external = AllGames.Count(g => g.IsExternal);
                var changes = added + removed > 0 ? $" · {added} nuevos, {removed} quitados" : "";
                Status = $"{AllGames.Count} juegos{changes}" + (external > 0 ? $" ({external} de Quiver fuera de la raíz)" : "") +
                         $" · portadas {withCover}/{AllGames.Count} · " +
                         (lib == null ? "Quiver no detectado" : $"Quiver: {lib.Apps.Count} apps");
                if (!Directory.Exists(root)) Status = $"La carpeta raíz no existe: {root}. Pulsa ⚙ para elegirla.";

                if (!Config.AutoCoverAttempted && AllGames.Count > 0)
                {
                    ConfigService.Update(Config, c => c.AutoCoverAttempted = true);
                    DownloadAllCovers(onlyQuiverIfNoKeys: true);
                }
                else if (Config.AutoCheckUpdates && afterStartup)
                {
                    CheckAllUpdates(silent: true);
                }
                if (afterStartup) { StartAppUpdateChecks(); RefreshEmporiumInBackground(); }
                SaveLibraryCache();
            });
        }, cts.Token);
    }

    /// <summary>
    /// Integra un escaneo nuevo sin rehacer la lista: los juegos que siguen se actualizan en su sitio
    /// (misma tarjeta, misma selección), se añaden los nuevos y se quitan los que ya no están.
    /// </summary>
    private (int added, int removed) MergeScan(List<GameEntry> scanned)
    {
        var current = AllGames.GroupBy(g => PathUtil.Norm(g.FolderPath)).ToDictionary(x => x.Key, x => x.First());
        var result = new List<GameEntry>(scanned.Count);
        int added = 0;
        foreach (var s in scanned)
        {
            var key = PathUtil.Norm(s.FolderPath);
            if (current.Remove(key, out var existing)) { existing.CopyFrom(s); result.Add(existing); }
            else { result.Add(s); added++; }
        }
        // no quitar un juego que se está actualizando o ejecutando aunque la carpeta haya desaparecido un momento
        var removed = 0;
        foreach (var gone in current.Values)
        {
            if (gone.IsUpdating || gone.IsRunning) result.Add(gone);
            else removed++;
        }
        var ordered = result.OrderBy(g => g.System, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (added > 0 || removed > 0 || !ordered.SequenceEqual(AllGames)) AllGames.ReplaceAll(ordered);
        return (added, removed);
    }

    /// <summary>Actualiza el catálogo de Gaming Emporium (una vez al día) y, si cambió, vuelve a emparejar.</summary>
    private void RefreshEmporiumInBackground()
    {
        if (!Config.UseEmporium || !EmporiumService.IsStale(Config)) return;
        Task.Run(async () =>
        {
            try
            {
                var (changed, error) = await EmporiumService.RefreshAsync(Config, force: false);
                if (error != null) { UiThread.Run(() => Status = "Gaming Emporium no disponible: " + error); return; }
                if (changed) UiThread.Run(() =>
                {
                    Status = $"Catálogo de Gaming Emporium actualizado ({EmporiumService.Entries(Config).Count} proyectos). Revisando vínculos...";
                    Rescan();
                });
            }
            catch (Exception ex) { Log.Warn("Gaming Emporium: " + ex.Message); }
        });
    }

    private System.Threading.Timer? _cacheSaveTimer;

    /// <summary>Guarda la caché (con un pequeño retardo para agrupar cambios seguidos).</summary>
    public void SaveLibraryCache()
    {
        var snapshot = AllGames.ToList();
        var root = Config.RootPath;
        _cacheSaveTimer?.Dispose();
        _cacheSaveTimer = new System.Threading.Timer(_ => LibraryCacheService.Save(snapshot, root), null, 1500, Timeout.Infinite);
    }

    // ------------------------------------------------------------------ actualización de RecompVault

    private System.Windows.Threading.DispatcherTimer? _appUpdateTimer;
    private string? _appUpdatePromptedTag;

    /// <summary>Comprueba al arrancar y cada 6 horas mientras RecompVault esté abierto.</summary>
    private void StartAppUpdateChecks()
    {
        if (_appUpdateTimer != null) return;
        _appUpdateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _appUpdateTimer.Tick += (_, _) => CheckAppUpdate(manual: false);
        _appUpdateTimer.Start();
        // un poco después de arrancar, para no tapar el primer escaneo
        var once = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        once.Tick += (_, _) => { once.Stop(); CheckAppUpdate(manual: false); };
        once.Start();
    }

    public async void CheckAppUpdate(bool manual)
    {
        if (!manual && !Config.CheckAppUpdates) return;
        var r = await SelfUpdateService.LatestAsync(Config.GitHubToken);
        if (r == null)
        {
            if (manual) DialogService.Info("Actualización de RecompVault", $"No se pudo consultar GitHub. Puedes mirar a mano en:\n{SelfUpdateService.ReleasesUrl}");
            return;
        }
        if (!r.IsNewer)
        {
            SelfUpdateLabel = "";
            if (manual) DialogService.Info("Actualización de RecompVault", $"Tienes la última versión ({SelfUpdateService.CurrentVersion}).");
            return;
        }
        _selfUpdateUrl = r.Url;
        _selfUpdateRelease = r;
        SelfUpdateLabel = $"⬆ RecompVault {r.Tag}";
        Status = $"Hay una versión nueva de RecompVault: {r.Tag} (tienes la {SelfUpdateService.CurrentVersion}).";
        // diálogo: siempre si es manual; si es automático, una vez por sesión y no si se omitió esa versión
        var skipped = !manual && string.Equals(Config.SkippedAppVersion, r.Tag, StringComparison.OrdinalIgnoreCase);
        if (manual || (!skipped && _appUpdatePromptedTag != r.Tag && !IsCouchMode))
        {
            _appUpdatePromptedTag = r.Tag;
            SelfUpdateService.Prompt(r, Config, manual);
        }
    }

    private SelfUpdateService.AppRelease? _selfUpdateRelease;

    // ------------------------------------------------------------------ acciones por juego

    private void Launch(GameEntry g)
    {
        if (g.IsRunning) { Status = $"{g.Name} ya está abierto."; return; }
        var err = LauncherService.Launch(g, Config, out var proc);
        if (err != null) { Status = $"No se pudo lanzar {g.Name}: {err}"; DialogService.Info("No se pudo lanzar", $"{g.Name}\n\n{err}"); return; }
        ConfigService.Update(Config, c => c.LastPlayed[g.FolderPath] = g.LastPlayed);
        Status = $"Lanzado: {g.Name}";
        if (SortMode == "Jugados recientemente") ApplyFilter();

        // tiempo de juego
        g.IsRunning = true;
        Task.Run(async () =>
        {
            var secs = await LauncherService.TrackSessionAsync(g, proc);
            UiThread.Run(() =>
            {
                g.IsRunning = false;
                if (secs <= 0) return;
                g.PlaySeconds += secs;
                ConfigService.Update(Config, c => c.PlaySeconds[g.FolderPath] = g.PlaySeconds);
                Status = $"{g.Name}: sesión de {TimeSpan.FromSeconds(secs):h\\:mm\\:ss} · total {g.PlayInfo}";
            });
        });
    }

    private void ToggleFavorite(GameEntry g)
    {
        g.Favorite = !g.Favorite;
        ConfigService.Update(Config, c =>
        {
            c.Favorites.RemoveAll(f => string.Equals(f, g.FolderPath, StringComparison.OrdinalIgnoreCase));
            if (g.Favorite) c.Favorites.Add(g.FolderPath);
        });
        if (FavoritesOnly) ApplyFilter();
    }

    private void PickExe(GameEntry g)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Ejecutable de {g.Name}",
            Filter = "Ejecutables|*.exe;*.bat;*.cmd;*.lnk|Todos|*.*",
            InitialDirectory = Directory.Exists(g.FolderPath) ? g.FolderPath : null
        };
        if (dlg.ShowDialog() != true) return;
        g.LaunchPath = dlg.FileName;
        ConfigService.Update(Config, c => c.LaunchOverrides[g.FolderPath] = dlg.FileName);
        Task.Run(() => ArtworkService.Apply(g, Config));
        Status = $"Ejecutable actualizado: {g.Name}";
    }

    private void PickCover(GameEntry g)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Carátula de {g.Name}",
            Filter = "Imágenes|*.png;*.jpg;*.jpeg;*.bmp;*.ico;*.webp;*.gif|Todos|*.*",
            InitialDirectory = Directory.Exists(g.FolderPath) ? g.FolderPath : null
        };
        if (dlg.ShowDialog() != true) return;
        var file = dlg.FileName;
        Task.Run(async () =>
        {
            var ok = await CoverDownloadService.ApplyFromFileAsync(g, file, Config);
            UiThread.Run(() => Status = ok ? $"Carátula actualizada: {g.Name}" : "Esa imagen no se puede leer.");
        });
    }

    private void SearchCover(GameEntry g)
    {
        var dlg = new CoverPickerDialog(g, Config) { Owner = ActiveOwner() };
        Status = dlg.ShowDialog() == true && dlg.Applied ? $"Portada aplicada: {g.Name}" : $"Sin cambios de portada: {g.Name}";
    }

    private void DownloadCover(GameEntry g)
    {
        Status = $"Buscando portada: {g.Name}...";
        Task.Run(async () =>
        {
            var ok = await CoverDownloadService.DownloadAsync(g, Config, new Progress<string>(s => UiThread.Run(() => Status = s)));
            UiThread.Run(() => Status = ok ? $"Portada encontrada: {g.Name}" : $"No se encontró portada para {g.Name}. Prueba 🔎 para buscar a mano.");
        });
    }

    private void DownloadAllCovers(bool onlyQuiverIfNoKeys = false)
    {
        var targets = AllGames.Where(g => !g.HasCover).ToList();
        if (targets.Count == 0) { Status = "Todos los juegos tienen portada."; return; }
        var anyKey = !string.IsNullOrWhiteSpace(Config.SteamGridDbApiKey) || !string.IsNullOrWhiteSpace(Config.RawgApiKey) ||
                     !string.IsNullOrWhiteSpace(Config.IgdbClientId);
        Status = $"Buscando {targets.Count} portadas...";
        Task.Run(async () =>
        {
            int n;
            var progress = new Progress<string>(s => UiThread.Run(() => Status = s));
            if (onlyQuiverIfNoKeys && !anyKey)
            {
                n = 0;
                foreach (var g in targets) if (await CoverDownloadService.TryQuiverAsync(g, Config)) n++;
            }
            else n = await CoverDownloadService.DownloadAllAsync(targets, Config, progress);
            UiThread.Run(() =>
            {
                Status = $"Portadas: {n} nuevas · total {AllGames.Count(x => x.HasCover)}/{AllGames.Count}." +
                         (anyKey ? "" : " Añade una API key de SteamGridDB (🔑SGDB) para encontrar más.");
                if (Config.AutoCheckUpdates) CheckAllUpdates(silent: true);
            });
        });
    }

    private void PromptKey(string title, string message, Func<AppConfig, string?> get, Action<AppConfig, string?> set)
    {
        var key = DialogService.Prompt(title, message, get(Config) ?? "");
        if (key == null) return;
        var v = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        ConfigService.Update(Config, c => set(c, v));
        Status = v == null ? $"{title}: borrada." : $"{title}: guardada.";
    }

    private void SetIgdb()
    {
        var id = DialogService.Prompt("IGDB (Twitch) Client ID", "Client ID de dev.twitch.tv/console/apps. Vacío para borrar.", Config.IgdbClientId ?? "");
        if (id == null) return;
        var secret = string.IsNullOrWhiteSpace(id) ? "" : DialogService.Prompt("IGDB (Twitch) Client Secret", "Client Secret de la misma aplicación de Twitch.", Config.IgdbClientSecret ?? "");
        if (secret == null) return;
        ConfigService.Update(Config, c =>
        {
            c.IgdbClientId = string.IsNullOrWhiteSpace(id) ? null : id.Trim();
            c.IgdbClientSecret = string.IsNullOrWhiteSpace(secret) ? null : secret.Trim();
        });
        Status = Config.IgdbClientId != null ? "Credenciales de IGDB guardadas." : "Credenciales de IGDB borradas.";
    }

    private void RenameGame(GameEntry g)
    {
        var name = DialogService.Prompt("Cambiar nombre", "Nuevo nombre (vacío para volver al automático):", g.Name, "Guardar");
        if (name == null) return;
        name = name.Trim();
        if (name.Length == 0)
        {
            ConfigService.Update(Config, c => c.NameOverrides.Remove(g.FolderPath));
            Status = "Nombre restaurado; re-escaneando...";
            Rescan();
            return;
        }
        ConfigService.Update(Config, c => c.NameOverrides[g.FolderPath] = name);
        g.Name = name;
        ApplyFilter();
        Status = $"Nombre actualizado: {name}";
    }

    private void HideGame(GameEntry g)
    {
        g.IsHidden = true;
        g.Favorite = false;
        ConfigService.Update(Config, c =>
        {
            if (!c.Hidden.Contains(g.FolderPath, StringComparer.OrdinalIgnoreCase)) c.Hidden.Add(g.FolderPath);
            c.Favorites.RemoveAll(f => string.Equals(f, g.FolderPath, StringComparison.OrdinalIgnoreCase));
        });
        RebuildSystems();
        ApplyFilter();
        Status = $"Ocultado: {g.Name}. Marca \"Mostrar ocultos\" para verlo.";
    }

    private void UnhideGame(GameEntry g)
    {
        g.IsHidden = false;
        ConfigService.Update(Config, c => c.Hidden.RemoveAll(h => string.Equals(h, g.FolderPath, StringComparison.OrdinalIgnoreCase)));
        RebuildSystems();
        ApplyFilter();
        Status = $"Restaurado: {g.Name}";
    }

    private void DeleteGame(GameEntry g)
    {
        // Protección: nunca la raíz, un sistema entero ni una ruta de primer nivel.
        var full = PathUtil.Norm(g.FolderPath);
        var root = PathUtil.Norm(Config.RootPath);
        var depthOk = full.Count(c => c == '\\') >= 2;
        var isSystemDir = PathUtil.IsSameOrInside(g.FolderPath, Config.RootPath) &&
                          PathUtil.Norm(Path.GetDirectoryName(g.FolderPath) ?? "") == root;
        if (!depthOk || full == root || isSystemDir || !Directory.Exists(g.FolderPath))
        {
            DialogService.Info("No se puede eliminar", $"Por seguridad no se elimina esta carpeta:\n{g.FolderPath}");
            return;
        }
        if (!DialogService.Confirm("Eliminar juego",
                $"¿Enviar \"{g.Name}\" y toda su carpeta a la Papelera de reciclaje?\n\n{g.FolderPath}" +
                (g.IsExternal ? "\n\nEs una app de Quiver: quítala también de Quiver para que no la reinstale." : ""),
                "Enviar a la papelera", "Cancelar"))
            return;

        Status = $"Eliminando {g.Name}...";
        Task.Run(() =>
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(g.FolderPath,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                ConfigService.Update(Config, c =>
                {
                    c.Hidden.RemoveAll(h => string.Equals(h, g.FolderPath, StringComparison.OrdinalIgnoreCase));
                    c.Favorites.RemoveAll(f => string.Equals(f, g.FolderPath, StringComparison.OrdinalIgnoreCase));
                    c.LaunchOverrides.Remove(g.FolderPath);
                    c.CoverOverrides.Remove(g.FolderPath);
                    c.NameOverrides.Remove(g.FolderPath);
                    c.LastPlayed.Remove(g.FolderPath);
                });
                UiThread.Run(() =>
                {
                    AllGames.Remove(g);
                    RebuildSystems();
                    ApplyFilter();
                    RaiseUpdateCounts();
                    Status = $"Enviado a la papelera: {g.Name}";
                });
            }
            catch (OperationCanceledException) { UiThread.Run(() => Status = "Eliminación cancelada."); }
            catch (Exception ex)
            {
                Log.Error($"No se pudo eliminar {g.FolderPath}", ex);
                UiThread.Run(() => Status = $"No se pudo eliminar: {ex.Message}");
            }
        });
    }

    private void OpenRepository(GameEntry g)
    {
        if (string.IsNullOrWhiteSpace(g.Repository)) { Status = $"{g.Name} no está vinculado a ningún repositorio."; return; }
        var host = g.Provider == "gitlab" ? "https://gitlab.com/" : "https://github.com/";
        LauncherService.OpenUrl(host + g.Repository!.Trim('/') + "/releases");
    }

    // ------------------------------------------------------------------ Quiver

    private void SyncQuiver()
    {
        var path = QuiverService.ResolvePath(Config);
        if (path == null)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Selecciona la carpeta de Quiver Launcher (la que contiene apps.json)" };
            if (dlg.ShowDialog() != true) { Status = "Sincronización con Quiver cancelada."; return; }
            var resolved = QuiverLibrary.ResolveRoot(dlg.FolderName);
            if (resolved == null)
            {
                DialogService.Info("Quiver no encontrado", $"No hay apps.json en:\n{dlg.FolderName}\n\nElige la carpeta QuiverLauncher-win-Portable (o la de %LOCALAPPDATA%\\QuiverLauncher).");
                return;
            }
            ConfigService.Update(Config, c => { c.QuiverPath = resolved; c.QuiverSyncEnabled = true; });
        }
        QuiverLibrary.InvalidateCache();
        Status = "Sincronizando con Quiver...";
        Rescan();
        // tras el escaneo, completar portadas que solo existan en Quiver
        var gen = _scanGeneration;
        Task.Run(async () =>
        {
            while (IsBusy && gen == _scanGeneration) await Task.Delay(200);
            if (gen != _scanGeneration) return;
            var targets = AllGames.Where(g => !g.HasCover).ToList();
            int n = 0;
            foreach (var g in targets) if (await CoverDownloadService.TryQuiverAsync(g, Config)) n++;
            var recognized = AllGames.Count(g => !string.IsNullOrWhiteSpace(g.Repository));
            UiThread.Run(() => Status = $"Quiver: {recognized} juegos vinculados a un repositorio, {n} portadas importadas.");
        });
    }

    // ------------------------------------------------------------------ actualizaciones

    private void ToggleAutoUpdate()
    {
        ConfigService.Update(Config, c => c.AutoUpdateRecomps = !c.AutoUpdateRecomps);
        OnPropertyChanged(nameof(AutoUpdateLabel));
        Status = Config.AutoUpdateRecomps
            ? "Auto-actualización ACTIVADA: se instalarán solas las actualizaciones con versión instalada conocida."
            : "Auto-actualización DESACTIVADA.";
    }

    private void CheckAllUpdates(bool silent)
    {
        if (_checkingUpdates) { if (!silent) Status = "Ya se están comprobando actualizaciones..."; return; }
        var targets = AllGames.Where(g => !string.IsNullOrWhiteSpace(g.Repository)).ToList();
        if (targets.Count == 0)
        {
            if (!silent) Status = "Ningún juego está vinculado a un repositorio (sincroniza con Quiver).";
            return;
        }
        _checkingUpdates = true;
        if (!silent) Status = $"Comprobando actualizaciones ({targets.Count})...";
        Task.Run(async () =>
        {
            Dictionary<CheckResult, int> counts;
            try
            {
                var lib = QuiverService.Load(Config);
                var progress = silent ? null : new Progress<(int done, int total)>(p => UiThread.Run(() => Status = $"Comprobando actualizaciones... {p.done}/{p.total}"));
                counts = await UpdateService.CheckManyAsync(targets, lib, Config, force: !silent, progress);
            }
            catch (Exception ex)
            {
                Log.Error("Error comprobando actualizaciones", ex);
                UiThread.Run(() => { _checkingUpdates = false; Status = "Error comprobando actualizaciones: " + ex.Message; });
                return;
            }
            UiThread.Run(() =>
            {
                _checkingUpdates = false;
                RaiseUpdateCounts();
                if (SortMode == "Con actualización") ApplyFilter();
                var updates = AllGames.Where(x => x.HasUpdate).ToList();
                var installable = updates.Count(x => !string.IsNullOrWhiteSpace(x.UpdateAssetUrl));
                var limited = counts.GetValueOrDefault(CheckResult.RateLimited);
                var notFound = counts.GetValueOrDefault(CheckResult.NotFound);
                var unknown = counts.GetValueOrDefault(CheckResult.LatestOnly);

                var msg = updates.Count > 0 ? $"Actualizaciones: {updates.Count} (instalables: {installable})." : "Todo al día.";
                if (unknown > 0) msg += $" {unknown} sin versión instalada conocida.";
                if (notFound > 0) msg += $" {notFound} repos no encontrados.";
                if (limited > 0) msg += $" Límite de GitHub alcanzado ({limited} sin comprobar, se reinicia a las {UpdateService.RateLimitResetLocal:HH:mm}); añade un token con 🔑GitHub.";
                if (!silent || updates.Count > 0 || limited > 0) Status = msg;

                if (!silent && updates.Count > 0)
                {
                    var sample = string.Join("\n", updates.Take(12).Select(x => $"• {x.Name}: {x.InstalledVersion} → {x.LatestVersion}"));
                    if (updates.Count > 12) sample += $"\n… y {updates.Count - 12} más";
                    DialogService.Info("Actualizaciones encontradas", $"{sample}\n\nUsa ⟳ en cada tarjeta para actualizar." +
                        (Config.AutoUpdateRecomps ? "" : "\nO activa Auto-update para hacerlo automáticamente."));
                }
                if (Config.AutoUpdateRecomps && installable > 0) AutoUpdateAll();
            });
        });
    }

    private void CheckSingleUpdate(GameEntry g)
    {
        if (string.IsNullOrWhiteSpace(g.Repository)) { Status = $"{g.Name} no está vinculado a ningún repositorio de Quiver."; return; }
        Status = $"Comprobando {g.Name}...";
        Task.Run(async () =>
        {
            var r = await UpdateService.CheckUpdateAsync(g, QuiverService.Load(Config), Config, force: true);
            UiThread.Run(() =>
            {
                RaiseUpdateCounts();
                Status = r switch
                {
                    CheckResult.UpdateAvailable => $"Actualización disponible: {g.Name} {g.InstalledVersion} → {g.LatestVersion}",
                    CheckResult.LatestOnly => $"{g.Name}: última versión {g.LatestVersion}; la instalada es desconocida (pulsa ⟳ para instalar la última).",
                    CheckResult.UpToDate => $"{g.Name} está al día ({g.InstalledVersion}).",
                    CheckResult.NotFound => $"{g.Name}: el repositorio {g.Repository} no existe o no tiene releases.",
                    CheckResult.RateLimited => $"Límite de la API de GitHub alcanzado hasta las {UpdateService.RateLimitResetLocal:HH:mm}. Añade un token con 🔑GitHub.",
                    _ => $"No se pudo comprobar {g.Name} (ver registro)."
                };
            });
        });
    }

    private void UpdateSingleGame(GameEntry g)
    {
        if (g.IsUpdating) { Status = $"Ya se está actualizando {g.Name}."; return; }
        if (string.IsNullOrWhiteSpace(g.Repository)) { Status = $"{g.Name} no está vinculado a ningún repositorio (sincroniza con Quiver o usa ℹ)."; return; }
        if (g.IsPinned)
        {
            if (!DialogService.Confirm("Versión fijada", $"\"{g.Name}\" está fijado en {g.PinnedVersion}.\n\n¿Quitar la fijación y actualizar a la última?", "Quitar y actualizar", "Cancelar"))
                return;
            ConfigService.Update(Config, c => c.PinnedVersions.Remove(g.FolderPath));
            g.PinnedVersion = "";
        }
        Status = $"Preparando actualización de {g.Name}...";
        Task.Run(async () =>
        {
            var r = await UpdateService.CheckUpdateAsync(g, QuiverService.Load(Config), Config, force: true);
            UiThread.Run(() =>
            {
                RaiseUpdateCounts();
                if (Config.ConfirmUpdatesWithChangelog && (r == CheckResult.UpdateAvailable || r == CheckResult.LatestOnly))
                {
                    // notas del release y paquete antes de instalar (se puede elegir otro)
                    var dlg = new VersionPickerDialog(g, Config, preselectTag: g.LatestVersion, installText: "Actualizar")
                    { Owner = ActiveOwner(), Title = $"Actualizar {g.Name}: {(g.InstalledUnknown ? "?" : g.InstalledVersion)} → {g.LatestVersion}" };
                    if (dlg.ShowDialog() == true && dlg.Choice != null) StartUpdate(g, dlg.Choice, pin: dlg.Pin);
                    else Status = "Actualización cancelada.";
                }
                else if (r == CheckResult.UpdateAvailable)
                {
                    StartUpdate(g);
                }
                else if (r == CheckResult.LatestOnly)
                {
                    if (DialogService.Confirm("Versión instalada desconocida",
                            $"No se sabe qué versión de \"{g.Name}\" tienes instalada.\n\n¿Instalar la última ({g.LatestVersion}) encima?\nSe sobrescribirán los ficheros del juego; las partidas y ajustes que no vengan en el paquete se conservan.",
                            "Instalar", "Cancelar"))
                        StartUpdate(g);
                    else Status = "Actualización cancelada.";
                }
                else if (r == CheckResult.UpToDate)
                {
                    Status = $"{g.Name} ya está en la última versión ({g.InstalledVersion}).";
                }
                else
                {
                    Status = r == CheckResult.RateLimited
                        ? $"Límite de la API de GitHub alcanzado hasta las {UpdateService.RateLimitResetLocal:HH:mm}."
                        : $"No se pudo consultar el repositorio de {g.Name}.";
                }
            });
        });
    }

    /// <param name="choice">Versión concreta elegida; null = la última.</param>
    /// <param name="pin">true = fijar la versión instalada; false = quitar la fijación; null = no tocar.</param>
    private void StartUpdate(GameEntry g, UpdateService.InstallChoice? choice = null, bool? pin = false)
    {
        g.IsUpdating = true;
        var cts = BeginUpdateCts(g);
        Status = $"Instalando {g.Name} {choice?.Version ?? g.LatestVersion}...";
        Task.Run(async () =>
        {
            var res = await UpdateService.UpdateAsync(g, Config, new Progress<string>(s => UiThread.Run(() => Status = s)), cts.Token, choice);
            UiThread.Run(() =>
            {
                EndUpdateCts(g);
                g.IsUpdating = false;
                if (res.Ok && pin.HasValue)
                {
                    var tag = choice?.Version ?? g.InstalledVersion;
                    ConfigService.Update(Config, c => { if (pin.Value) c.PinnedVersions[g.FolderPath] = tag; else c.PinnedVersions.Remove(g.FolderPath); });
                    g.PinnedVersion = pin.Value ? tag : "";
                }
                RaiseUpdateCounts();
                Status = res.Message;
                if (!res.Ok && !res.Manual && !cts.IsCancellationRequested)
                    DialogService.Info("Actualización no completada", $"{g.Name}\n\n{res.Message}\n\nRepositorio: {g.Repository}\nPaquete: {g.UpdateAssetName ?? "(ninguno)"}\nRegistro: {Log.FilePath}");
            });
        });
    }

    private readonly Dictionary<GameEntry, CancellationTokenSource> _updateCts = new();

    private CancellationTokenSource BeginUpdateCts(GameEntry g)
    {
        var cts = new CancellationTokenSource();
        lock (_updateCts) _updateCts[g] = cts;
        UiThread.Run(() => { g.UpdateProgress = -1; g.UpdateStage = "Preparando"; });
        return cts;
    }

    private void EndUpdateCts(GameEntry g)
    {
        lock (_updateCts) { if (_updateCts.Remove(g, out var c)) c.Dispose(); }
        g.UpdateProgress = -1; g.UpdateStage = "";
    }

    private void CancelUpdate(GameEntry g)
    {
        lock (_updateCts) { if (_updateCts.TryGetValue(g, out var c)) c.Cancel(); }
        g.UpdateStage = "Cancelando...";
        Status = $"Cancelando la actualización de {g.Name} (se restaura lo anterior)...";
    }

    private void AutoUpdateAll()
    {
        if (_autoUpdating) return;
        var candidates = AllGames.Where(g => g.HasUpdate && !string.IsNullOrWhiteSpace(g.UpdateAssetUrl) && !g.IsUpdating).ToList();
        // Solo vínculos seguros: una coincidencia aproximada puede ser otro juego.
        var fuzzy = candidates.Where(g => g.IsFuzzyLink).ToList();
        var list = candidates.Except(fuzzy).ToList();
        if (fuzzy.Count > 0)
            Log.Info($"Auto-update omitido en {fuzzy.Count} juegos con vínculo aproximado: {string.Join(", ", fuzzy.Select(x => x.Name))}");
        if (list.Count == 0)
        {
            if (fuzzy.Count > 0) Status = $"{fuzzy.Count} actualizaciones no se instalan solas porque su vínculo es aproximado. Revísalas en 🔍 Revisar detección.";
            return;
        }
        _autoUpdating = true;
        Task.Run(async () =>
        {
            int ok = 0, i = 0;
            var failed = new List<string>();
            foreach (var g in list)
            {
                i++;
                var n = i;
                UiThread.Run(() => { g.IsUpdating = true; Status = $"Auto-actualizando ({n}/{list.Count}) {g.Name}..."; });
                var cts = BeginUpdateCts(g);
                var res = await UpdateService.UpdateAsync(g, Config, new Progress<string>(s => UiThread.Run(() => Status = $"({n}/{list.Count}) {s}")), cts.Token);
                if (res.Ok) ok++; else if (!cts.IsCancellationRequested) failed.Add($"• {g.Name}: {res.Message}");
                UiThread.Run(() => { EndUpdateCts(g); g.IsUpdating = false; });
            }
            UiThread.Run(() =>
            {
                _autoUpdating = false;
                RaiseUpdateCounts();
                Status = $"Auto-actualización: {ok}/{list.Count} actualizados." +
                         (fuzzy.Count > 0 ? $" {fuzzy.Count} con vínculo aproximado sin tocar (revísalos en 🔍 Revisar detección)." : "");
                if (failed.Count > 0) DialogService.Info("Auto-actualización", $"Actualizados {ok} de {list.Count}.\n\nFallidos:\n{string.Join("\n", failed.Take(10))}");
            });
        });
    }

    private void RaiseUpdateCounts()
    {
        OnPropertyChanged(nameof(UpdatesAvailableCount));
        OnPropertyChanged(nameof(UpdatesLabel));
    }

    // ------------------------------------------------------------------ filtro

    private void RebuildSystems()
    {
        var current = _selectedSystem?.Name ?? "Todos";
        var pool = AllGames.Where(g => ShowHidden || !g.IsHidden).ToList();
        var all = SystemItem.FromName("Todos", pool.Count);
        all.UpdateCount = pool.Count(g => g.HasUpdate);
        var items = new List<SystemItem> { all };
        foreach (var grp in pool.GroupBy(g => g.System, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var item = SystemItem.FromName(grp.Key, grp.Count());
            item.UpdateCount = grp.Count(g => g.HasUpdate);
            SystemLogoService.Apply(item);
            items.Add(item);
        }
        _rebuildingSystems = true;
        try { Systems.ReplaceAll(items); }
        finally { _rebuildingSystems = false; }
        _selectedSystem = Systems.FirstOrDefault(s => s.Name == current) ?? Systems[0];
        OnPropertyChanged(nameof(SelectedSystem));
    }

    public void ApplyFilter()
    {
        IEnumerable<GameEntry> q = AllGames;
        if (!ShowHidden) q = q.Where(g => !g.IsHidden);
        if (FavoritesOnly) q = q.Where(g => g.Favorite);
        var sys = SelectedSystem?.Name;
        if (!string.IsNullOrEmpty(sys) && sys != "Todos") q = q.Where(g => string.Equals(g.System, sys, StringComparison.OrdinalIgnoreCase));
        var search = Search?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            var terms = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            q = q.Where(g => terms.All(t =>
                g.Name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                g.System.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                (g.RecompName ?? "").Contains(t, StringComparison.OrdinalIgnoreCase) ||
                (g.Repository ?? "").Contains(t, StringComparison.OrdinalIgnoreCase) ||
                g.FolderPath.Contains(t, StringComparison.OrdinalIgnoreCase)));
        }
        q = SortMode switch
        {
            "Nombre" => q.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            "Jugados recientemente" => q.OrderByDescending(g => g.LastPlayed).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            "Con actualización" => q.OrderByDescending(g => g.HasUpdate).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => q.OrderBy(g => g.System, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        VisibleGames.ReplaceAll(q.ToList());
        RebuildRows();
        OnPropertyChanged(nameof(HasNoVisibleGames));
        OnPropertyChanged(nameof(HeroSystem));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
