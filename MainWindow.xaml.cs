using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using RecompVault.Converters;
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

        Width = _vm.Config.WindowWidth;
        Height = _vm.Config.WindowHeight;
        SourceInitialized += (_, _) => TryEnableDarkTitleBar();
    }

    private void TryEnableDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int useDark = 1;
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Win10 2004+), 19 for older builds
            DwmSetWindowAttribute(hwnd, 20, ref useDark, sizeof(int));
            DwmSetWindowAttribute(hwnd, 19, ref useDark, sizeof(int));
        }
        catch { }
    }

    private void OpenRoot_Click(object sender, RoutedEventArgs e)
    {
        LauncherService.OpenFolder(_vm.Config.RootPath);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Selecciona la carpeta raíz de recomps/ports",
            InitialDirectory = _vm.Config.RootPath
        };
        if (dlg.ShowDialog() == true)
        {
            _vm.Config.RootPath = dlg.FolderName;
            ConfigService.Save(_vm.Config);
            _vm.Rescan();
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _vm.Config.WindowWidth = Width;
        _vm.Config.WindowHeight = Height;
        ConfigService.Save(_vm.Config);
        base.OnClosing(e);
    }
}