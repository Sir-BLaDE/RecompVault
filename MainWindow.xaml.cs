using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using RecompVault.Services;
using RecompVault.ViewModels;

namespace RecompVault;

public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        // tamaño guardado, sin salirse de la pantalla
        Width = Math.Clamp(_vm.Config.WindowWidth, MinWidth, SystemParameters.VirtualScreenWidth);
        Height = Math.Clamp(_vm.Config.WindowHeight, MinHeight, SystemParameters.VirtualScreenHeight);
        if (_vm.Config.WindowMaximized) WindowState = WindowState.Maximized;

        SourceInitialized += (_, _) => TryEnableDarkTitleBar();
        Loaded += (_, _) => OnFirstLoad();
    }

    private void OnFirstLoad()
    {
        if (!Directory.Exists(_vm.Config.RootPath))
        {
            DialogService.Info("Carpeta de juegos", $"No existe la carpeta raíz configurada:\n{_vm.Config.RootPath}\n\nElige la carpeta que contiene las subcarpetas por sistema (N64, PSX, X360...).");
            PickRoot();
        }
        _vm.Rescan(afterStartup: true);
    }

    private void TryEnableDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int useDark = 1;
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Win10 2004+), 19 en builds anteriores
            if (DwmSetWindowAttribute(hwnd, 20, ref useDark, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref useDark, sizeof(int));
        }
        catch { }
    }

    private void OpenRoot_Click(object sender, RoutedEventArgs e)
    {
        var err = LauncherService.OpenFolder(_vm.Config.RootPath);
        if (err != null) _vm.Status = err;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (PickRoot()) _vm.Rescan();
    }

    private bool PickRoot()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Selecciona la carpeta raíz de recomps/ports" };
        if (Directory.Exists(_vm.Config.RootPath)) dlg.InitialDirectory = _vm.Config.RootPath;
        if (dlg.ShowDialog(this) != true) return false;
        ConfigService.Update(_vm.Config, c => c.RootPath = dlg.FolderName);
        return true;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        ConfigService.Update(_vm.Config, c =>
        {
            c.WindowMaximized = WindowState == WindowState.Maximized;
            var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            if (b.Width > 0 && b.Height > 0) { c.WindowWidth = b.Width; c.WindowHeight = b.Height; }
        });
        base.OnClosing(e);
    }
}
