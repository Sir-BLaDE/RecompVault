using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using RecompVault.Services;

namespace RecompVault;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Ningún error debe cerrar el launcher sin explicación: se registra y se muestra.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Excepción no observada en tarea", args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Excepción no controlada", args.ExceptionObject as Exception);

        Log.Info($"RecompVault {typeof(App).Assembly.GetName().Version} iniciado");
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Excepción en la interfaz", e.Exception);
        e.Handled = true;
        try
        {
            MessageBox.Show($"Ha ocurrido un error, pero RecompVault sigue funcionando:\n\n{e.Exception.Message}\n\nDetalles: {Log.FilePath}",
                "RecompVault", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { }
    }
}
