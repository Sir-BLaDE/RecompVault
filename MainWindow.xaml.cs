using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
        _vm.Rescan(afterStartup: true);
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

    private void SystemsList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var sv = FindChild<ScrollViewer>(SystemsList);
        if (sv == null) return;
        sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ teclado y mando

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11) { ToggleCouch(); e.Handled = true; return; }
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
            case Key.Escape when _vm.IsCouchMode: ToggleCouch(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnPad(PadButton b)
    {
        switch (b)
        {
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
            _vm.Status = "Modo sofá: cruceta/stick para moverte, A jugar, LB/RB sistema, Start o F11 para salir.";
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
