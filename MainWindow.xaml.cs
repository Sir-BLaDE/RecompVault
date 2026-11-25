using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using RecompVault.Models;
using RecompVault.Services;
using RecompVault.ViewModels;

namespace RecompVault;

public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    /// <summary>Ancho ocupado por una tarjeta (212 + 2×8 de margen).</summary>
    private const double CardOuterWidth = 228;

    private readonly MainViewModel _vm;
    private readonly GamepadService _pad;
    private WindowState _stateBeforeCouch = WindowState.Normal;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"RecompVault {SelfUpdateService.CurrentVersion}";
        _vm = new MainViewModel();
        DataContext = _vm;

        Width = Math.Clamp(_vm.Config.WindowWidth, MinWidth, SystemParameters.VirtualScreenWidth);
        Height = Math.Clamp(_vm.Config.WindowHeight, MinHeight, SystemParameters.VirtualScreenHeight);
        if (_vm.Config.WindowMaximized) WindowState = WindowState.Maximized;

        _vm.ScrollToRowRequested += row =>
        {
            if (row >= 0 && row < _vm.Rows.Count) GamesList.ScrollIntoView(_vm.Rows[row]);
        };
        _vm.PropertyChanged += Vm_PropertyChanged;

        _pad = new GamepadService(() => IsActive && OwnedWindows.Count == 0);
        _pad.Pressed += OnPad;

        SourceInitialized += (_, _) => TryEnableDarkTitleBar();
        Loaded += (_, _) => OnFirstLoad();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnFirstLoad()
    {
        if (!Directory.Exists(_vm.Config.RootPath))
        {
            DialogService.Info("Carpeta de juegos", $"No existe la carpeta raíz configurada:\n{_vm.Config.RootPath}\n\nElige la carpeta que contiene las subcarpetas por sistema (N64, PSX, X360...).");
            PickRoot();
        }
        _vm.Startup();
        _pad.Start();
    }

    private void TryEnableDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int useDark = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref useDark, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref useDark, sizeof(int));
        }
        catch { }
    }

    // ------------------------------------------------------------------ cuadrícula

    private void GamesList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // ancho útil: menos padding (32) y barra de desplazamiento (~18)
        var usable = Math.Max(0, GamesList.ActualWidth - 32 - 18);
        _vm.Columns = Math.Max(1, (int)(usable / CardOuterWidth));
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GameEntry g }) return;
        // los botones de la tarjeta gestionan su propio clic
        if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) != null) return;
        _vm.SelectedGame = g;
        if (e.ClickCount == 2 && g.CanLaunch) _vm.LaunchCommand.Execute(g);
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject d) return;
        var card = FindParent<Border>(d, b => b.ContextMenu != null);
        if (card?.ContextMenu == null) return;
        card.ContextMenu.PlacementTarget = card;
        card.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        card.ContextMenu.IsOpen = true;
    }

    // ------------------------------------------------------------------ carrusel de sistemas

    private ScrollViewer? _sysSv;
    private ScrollViewer? SysSv => _sysSv ??= FindChild<ScrollViewer>(SystemsList);
    private double _sysTarget = double.NaN;
    private int _sysFrames;
    private bool _syncingBar;

    private void SystemsList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var sv = SysSv;
        if (sv == null) return;
        var from = double.IsNaN(_sysTarget) ? sv.HorizontalOffset : _sysTarget;
        SmoothScrollTo(from - e.Delta * 1.5);
        e.Handled = true;
    }

    private void SystemsList_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateSystemsNav();

    /// <summary>Flechas y barra según haya sistemas fuera de la vista.</summary>
    private void UpdateSystemsNav()
    {
        var sv = SysSv;
        if (sv == null) return;
        var overflow = sv.ScrollableWidth > 1;
        // Hidden (no Collapsed) en las flechas: si cambiasen el ancho, el desbordamiento podría oscilar
        SysPrev.Visibility = SysNext.Visibility = overflow ? Visibility.Visible : Visibility.Hidden;
        SystemsBar.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        SysPrev.IsEnabled = sv.HorizontalOffset > 1;
        SysNext.IsEnabled = sv.HorizontalOffset < sv.ScrollableWidth - 1;
        _syncingBar = true;
        SystemsBar.Minimum = 0;
        SystemsBar.Maximum = sv.ScrollableWidth;
        SystemsBar.ViewportSize = sv.ViewportWidth;
        SystemsBar.SmallChange = 80;
        SystemsBar.LargeChange = Math.Max(150, sv.ViewportWidth * 0.8);
        SystemsBar.Value = sv.HorizontalOffset;
        _syncingBar = false;
    }

    private void SystemsBar_Scroll(object sender, ScrollEventArgs e)
    {
        if (_syncingBar) return;
        SmoothScrollTo(e.NewValue, immediate: e.ScrollEventType == ScrollEventType.ThumbTrack);
    }

    private double SysPage => Math.Max(150, (SysSv?.ViewportWidth ?? 600) * 0.8);
    private double SysFrom => double.IsNaN(_sysTarget) ? SysSv?.HorizontalOffset ?? 0 : _sysTarget;
    private void SysPrev_Click(object sender, RoutedEventArgs e) => SmoothScrollTo(SysFrom - SysPage);
    private void SysNext_Click(object sender, RoutedEventArgs e) => SmoothScrollTo(SysFrom + SysPage);
    private void SysPrev_RightClick(object sender, MouseButtonEventArgs e) { SmoothScrollTo(0); e.Handled = true; }
    private void SysNext_RightClick(object sender, MouseButtonEventArgs e) { SmoothScrollTo(double.MaxValue); e.Handled = true; }

    private void SmoothScrollTo(double x, bool immediate = false)
    {
        var sv = SysSv;
        if (sv == null) return;
        x = Math.Clamp(x, 0, Math.Max(0, sv.ScrollableWidth));
        if (immediate || !SystemParameters.ClientAreaAnimation)
        {
            StopSysAnimation();
            sv.ScrollToHorizontalOffset(x);
            return;
        }
        if (double.IsNaN(_sysTarget)) CompositionTarget.Rendering += SysAnimate;
        _sysTarget = x;
        _sysFrames = 0;
    }

    private void StopSysAnimation()
    {
        if (!double.IsNaN(_sysTarget)) CompositionTarget.Rendering -= SysAnimate;
        _sysTarget = double.NaN;
    }

    private void SysAnimate(object? sender, EventArgs e)
    {
        var sv = SysSv;
        if (sv == null || double.IsNaN(_sysTarget)) { StopSysAnimation(); return; }
        var cur = sv.HorizontalOffset;
        var d = _sysTarget - cur;
        if (Math.Abs(d) < 1 || ++_sysFrames > 90) { sv.ScrollToHorizontalOffset(_sysTarget); StopSysAnimation(); return; }
        sv.ScrollToHorizontalOffset(cur + d * 0.25);
    }

    /// <summary>Centra el sistema seleccionado en el carrusel (al cambiar con LB/RB, RePág/AvPág o desde la cuadrícula).</summary>
    private void BringSelectedSystemIntoView()
    {
        var sv = SysSv;
        if (sv == null || _vm.SelectedSystem == null) return;
        if (SystemsList.ItemContainerGenerator.ContainerFromItem(_vm.SelectedSystem) is not FrameworkElement c || !c.IsVisible) return;
        var x = c.TransformToAncestor(sv).Transform(new Point(0, 0)).X;
        var inView = x >= 0 && x + c.ActualWidth <= sv.ViewportWidth;
        if (inView && double.IsNaN(_sysTarget)) return;
        SmoothScrollTo(sv.HorizontalOffset + x + c.ActualWidth / 2 - sv.ViewportWidth / 2);
    }

    // ---- cuadrícula de todos los sistemas

    private const int PopupColumns = 3;

    private void AllSystems_Opened(object sender, RoutedEventArgs e)
    {
        AllSystemsToggle.IsHitTestVisible = false; // si no, el clic que cierra el popup lo volvería a abrir
        SystemFilterBox.Text = "";
        FillAllSystems("");
        Dispatcher.BeginInvoke(() => { SystemFilterBox.Focus(); Keyboard.Focus(SystemFilterBox); }, DispatcherPriority.Input);
    }

    private void AllSystems_Closed(object? sender, EventArgs e)
    {
        AllSystemsToggle.IsChecked = false;
        Dispatcher.BeginInvoke(() => AllSystemsToggle.IsHitTestVisible = true, DispatcherPriority.Background);
    }

    private void ToggleAllSystems() => AllSystemsToggle.IsChecked = !(AllSystemsToggle.IsChecked ?? false);

    private void FillAllSystems(string q)
    {
        q = q.Trim();
        bool Has(string? s) => !string.IsNullOrEmpty(s) && s.Contains(q, StringComparison.OrdinalIgnoreCase);
        var list = _vm.Systems.Where(s => q.Length == 0 || Has(s.FullName) || Has(s.Name) || Has(s.Maker) || Has(s.Icon)).ToList();
        AllSystemsList.ItemsSource = list;
        var sel = list.Contains(_vm.SelectedSystem) ? _vm.SelectedSystem : list.FirstOrDefault();
        AllSystemsList.SelectedItem = sel;
        if (sel != null) AllSystemsList.ScrollIntoView(sel);
    }

    private void SystemFilter_TextChanged(object sender, TextChangedEventArgs e) => FillAllSystems(SystemFilterBox.Text);

    private void SystemFilter_PreviewKeyDown(object sender, KeyEventArgs e) => e.Handled = PopupKey(e.Key);
    private void AllSystemsList_PreviewKeyDown(object sender, KeyEventArgs e) => e.Handled = PopupKey(e.Key);

    private bool PopupKey(Key k)
    {
        switch (k)
        {
            case Key.Enter: ChooseSystem(AllSystemsList.SelectedItem as SystemItem); return true;
            case Key.Escape: AllSystemsToggle.IsChecked = false; return true;
            case Key.Down: MovePopupSelection(PopupColumns); return true;
            case Key.Up: MovePopupSelection(-PopupColumns); return true;
            case Key.Right when Keyboard.FocusedElement != SystemFilterBox || SystemFilterBox.CaretIndex == SystemFilterBox.Text.Length:
                MovePopupSelection(1); return true;
            case Key.Left when Keyboard.FocusedElement != SystemFilterBox || SystemFilterBox.CaretIndex == 0:
                MovePopupSelection(-1); return true;
        }
        return false;
    }

    private void MovePopupSelection(int delta)
    {
        var n = AllSystemsList.Items.Count;
        if (n == 0) return;
        var i = AllSystemsList.SelectedIndex < 0 ? 0 : Math.Clamp(AllSystemsList.SelectedIndex + delta, 0, n - 1);
        AllSystemsList.SelectedIndex = i;
        AllSystemsList.ScrollIntoView(AllSystemsList.SelectedItem);
    }

    private void AllSystemsList_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParent<ListBoxItem>(d) is { DataContext: SystemItem s })
            ChooseSystem(s);
    }

    private void ChooseSystem(SystemItem? s)
    {
        if (s == null) return;
        _vm.SelectedSystem = s;
        AllSystemsToggle.IsChecked = false;
        Dispatcher.BeginInvoke(BringSelectedSystemIntoView, DispatcherPriority.Loaded);
    }

    // ------------------------------------------------------------------ teclado y mando

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (AllSystemsPopup.IsOpen) return; // el popup gestiona sus propias teclas
        if (e.Key == Key.F11) { ToggleCouch(); e.Handled = true; return; }
        if (e.Key == Key.F5) { _vm.Rescan(full: Keyboard.Modifiers.HasFlag(ModifierKeys.Control)); e.Handled = true; return; }
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return; }

        var inText = Keyboard.FocusedElement is TextBox or ComboBox or ComboBoxItem;
        if (inText)
        {
            if (e.Key == Key.Escape) { _vm.Search = ""; Keyboard.ClearFocus(); e.Handled = true; }
            else if (e.Key == Key.Down && Keyboard.FocusedElement == SearchBox) { Keyboard.ClearFocus(); _vm.MoveSelection(0, 0); e.Handled = true; }
            return;
        }
        switch (e.Key)
        {
            case Key.Left: _vm.MoveSelection(-1, 0); break;
            case Key.Right: _vm.MoveSelection(1, 0); break;
            case Key.Up: _vm.MoveSelection(0, -1); break;
            case Key.Down: _vm.MoveSelection(0, 1); break;
            case Key.Enter: _vm.LaunchSelected(); break;
            case Key.PageUp: _vm.CycleSystem(-1); break;
            case Key.PageDown: _vm.CycleSystem(1); break;
            case Key.S when Keyboard.Modifiers == ModifierKeys.None: ToggleAllSystems(); break;
            case Key.Escape when _vm.IsCouchMode: ToggleCouch(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnPad(PadButton b)
    {
        if (AllSystemsPopup.IsOpen)
        {
            switch (b)
            {
                case PadButton.Left: MovePopupSelection(-1); break;
                case PadButton.Right: MovePopupSelection(1); break;
                case PadButton.Up: MovePopupSelection(-PopupColumns); break;
                case PadButton.Down: MovePopupSelection(PopupColumns); break;
                case PadButton.A: ChooseSystem(AllSystemsList.SelectedItem as SystemItem); break;
                case PadButton.B: case PadButton.Back: AllSystemsToggle.IsChecked = false; break;
            }
            return;
        }
        switch (b)
        {
            case PadButton.Back: ToggleAllSystems(); break;
            case PadButton.Left: _vm.MoveSelection(-1, 0); break;
            case PadButton.Right: _vm.MoveSelection(1, 0); break;
            case PadButton.Up: _vm.MoveSelection(0, -1); break;
            case PadButton.Down: _vm.MoveSelection(0, 1); break;
            case PadButton.A: _vm.LaunchSelected(); break;
            case PadButton.Y: _vm.ToggleFavoriteSelected(); break;
            case PadButton.X: _vm.DetailsSelected(); break;
            case PadButton.LB: _vm.CycleSystem(-1); break;
            case PadButton.RB: _vm.CycleSystem(1); break;
            case PadButton.Start: ToggleCouch(); break;
            case PadButton.B: if (!string.IsNullOrEmpty(_vm.Search)) _vm.Search = ""; break;
        }
    }

    // ------------------------------------------------------------------ modo sofá

    private void CouchMode_Click(object sender, RoutedEventArgs e) => ToggleCouch();

    private void ToggleCouch() => _vm.IsCouchMode = !_vm.IsCouchMode;

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedSystem))
        {
            Dispatcher.BeginInvoke(BringSelectedSystemIntoView, DispatcherPriority.Loaded);
            return;
        }
        if (e.PropertyName == nameof(MainViewModel.CompactSystems))
        {
            Dispatcher.BeginInvoke(() => { UpdateSystemsNav(); BringSelectedSystemIntoView(); }, DispatcherPriority.Loaded);
            return;
        }
        if (e.PropertyName != nameof(MainViewModel.IsCouchMode)) return;
        if (_vm.IsCouchMode)
        {
            _stateBeforeCouch = WindowState;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal; // forzar a cubrir la barra de tareas
            WindowState = WindowState.Maximized;
            RootGrid.LayoutTransform = new ScaleTransform(1.18, 1.18);
            if (_vm.SelectedGame == null) _vm.MoveSelection(0, 0);
            _vm.Status = "Modo sofá: cruceta/stick para moverte, A jugar, LB/RB sistema, Back todos los sistemas, Start o F11 para salir.";
        }
        else
        {
            RootGrid.LayoutTransform = Transform.Identity;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _stateBeforeCouch;
        }
    }

    // ------------------------------------------------------------------ otros

    private void OpenRoot_Click(object sender, RoutedEventArgs e)
    {
        var err = LauncherService.OpenFolder(_vm.Config.RootPath);
        if (err != null) _vm.Status = err;
    }

    private bool PickRoot()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Selecciona la carpeta raíz de recomps/ports" };
        if (Directory.Exists(_vm.Config.RootPath)) dlg.InitialDirectory = _vm.Config.RootPath;
        if (dlg.ShowDialog(this) != true) return false;
        ConfigService.Update(_vm.Config, c => c.RootPath = dlg.FolderName);
        return true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _pad.Dispose();
        StopSysAnimation();
        if (_vm.AllGames.Count > 0 && !_vm.IsBusy) LibraryCacheService.Save(_vm.AllGames.ToList(), _vm.Config.RootPath);
        if (_vm.IsCouchMode) { RootGrid.LayoutTransform = Transform.Identity; WindowStyle = WindowStyle.SingleBorderWindow; }
        ConfigService.Update(_vm.Config, c =>
        {
            c.WindowMaximized = WindowState == WindowState.Maximized && !_vm.IsCouchMode || (_vm.IsCouchMode && _stateBeforeCouch == WindowState.Maximized);
            var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            if (b.Width > 0 && b.Height > 0 && !double.IsInfinity(b.Width)) { c.WindowWidth = b.Width; c.WindowHeight = b.Height; }
        });
        base.OnClosing(e);
    }

    private static T? FindParent<T>(DependencyObject d, Func<T, bool>? pred = null) where T : DependencyObject
    {
        var cur = d;
        while (cur != null)
        {
            if (cur is T t && (pred == null || pred(t))) return t;
            cur = cur is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur);
        }
        return null;
    }

    private static T? FindChild<T>(DependencyObject d) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is T t) return t;
            var r = FindChild<T>(c);
            if (r != null) return r;
        }
        return null;
    }
}
