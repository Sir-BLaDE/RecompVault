using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace RecompVault.Services;

public static class DialogService
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private static Brush Brush(string key, string fallback)
        => Application.Current?.TryFindResource(key) as Brush ?? (SolidColorBrush)(new BrushConverter().ConvertFromString(fallback)!);

    private static void ApplyDarkTitle(Window w)
    {
        w.SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                int useDark = 1;
                DwmSetWindowAttribute(hwnd, 20, ref useDark, sizeof(int));
                DwmSetWindowAttribute(hwnd, 19, ref useDark, sizeof(int));
            }
            catch { }
        };
    }

    private static Window? FindOwner()
    {
        var app = Application.Current;
        if (app == null) return null;
        var w = app.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive) ?? app.MainWindow;
        return w != null && w.IsLoaded && w.IsVisible ? w : null;
    }

    public static string? Prompt(string title, string message, string defaultValue = "", string okText = "Guardar")
    {
        var w = new Window
        {
            Title = title,
            Width = 540,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = FindOwner(),
            ResizeMode = ResizeMode.NoResize,
            Background = Brush("BgBrush", "#0F1115"),
            Foreground = Brush("TextBrush", "#E6E8EE"),
            WindowStyle = WindowStyle.ToolWindow
        };
        ApplyDarkTitle(w);

        var root = new Border { Padding = new Thickness(16), Background = Brush("BgBrush", "#0F1115") };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Foreground = Brush("TextBrush", "#E6E8EE")
        });
        var tb = new TextBox
        {
            Text = defaultValue,
            MinWidth = 420
        };
        sp.Children.Add(tb);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "Cancelar", MinWidth = 90, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = okText, MinWidth = 90, IsDefault = true };

        if (Application.Current?.TryFindResource("GhostButton") is Style gs) cancel.Style = gs;
        if (Application.Current?.TryFindResource("PrimaryButton") is Style ps) ok.Style = ps;

        btns.Children.Add(cancel);
        btns.Children.Add(ok);
        sp.Children.Add(btns);
        root.Child = sp;
        w.Content = root;

        bool? res = null;
        ok.Click += (s, e) => { res = true; w.Close(); };
        cancel.Click += (s, e) => { res = false; w.Close(); };
        w.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        w.ShowDialog();
        return res == true ? tb.Text : null;
    }

    public static bool Confirm(string title, string message, string okText = "Aceptar", string cancelText = "Cancelar")
    {
        var w = new Window
        {
            Title = title,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            MinHeight = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = FindOwner(),
            ResizeMode = ResizeMode.NoResize,
            Background = Brush("BgBrush", "#0F1115"),
            Foreground = Brush("TextBrush", "#E6E8EE"),
            WindowStyle = WindowStyle.ToolWindow
        };
        ApplyDarkTitle(w);

        var root = new Border { Padding = new Thickness(16), Background = Brush("BgBrush", "#0F1115") };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
            Foreground = Brush("TextBrush", "#E6E8EE")
        });

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = cancelText, MinWidth = 90, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = okText, MinWidth = 90, IsDefault = true };
        if (Application.Current?.TryFindResource("GhostButton") is Style gs) cancel.Style = gs;
        if (Application.Current?.TryFindResource("PrimaryButton") is Style ps) ok.Style = ps;

        btns.Children.Add(cancel);
        btns.Children.Add(ok);
        sp.Children.Add(btns);
        root.Child = sp;
        w.Content = root;

        bool? res = null;
        ok.Click += (s, e) => { res = true; w.Close(); };
        cancel.Click += (s, e) => { res = false; w.Close(); };
        w.ShowDialog();
        return res == true;
    }

    /// <summary>Mensaje con varios botones (el primero es el principal). Devuelve el índice pulsado o -1 si se cierra.</summary>
    public static int Choose(string title, string message, string? details, params string[] options)
    {
        var d = Application.Current?.Dispatcher;
        if (d != null && !d.CheckAccess()) return d.Invoke(() => Choose(title, message, details, options));
        var w = new Window
        {
            Title = title,
            Width = 600,
            SizeToContent = SizeToContent.Height,
            MinHeight = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = FindOwner(),
            ResizeMode = ResizeMode.NoResize,
            Background = Brush("BgBrush", "#0F1115"),
            Foreground = Brush("TextBrush", "#E6E8EE"),
            WindowStyle = WindowStyle.ToolWindow
        };
        if (w.Owner == null) w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ApplyDarkTitle(w);

        var root = new Border { Padding = new Thickness(16), Background = Brush("BgBrush", "#0F1115") };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), Foreground = Brush("TextBrush", "#E6E8EE") });
        if (!string.IsNullOrWhiteSpace(details))
        {
            var box = new TextBox
            {
                Text = details, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 280, Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 0, 14), FontFamily = new FontFamily("Consolas"), FontSize = 12,
                Background = Brush("PanelBrush", "#1A1F2B"), Foreground = Brush("TextBrush", "#E6E8EE"), BorderThickness = new Thickness(0)
            };
            sp.Children.Add(box);
        }
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        int res = -1;
        for (int i = 0; i < options.Length; i++)
        {
            var idx = i;
            var b = new Button { Content = options[i], MinWidth = 90, Margin = new Thickness(i == options.Length - 1 ? 0 : 0, 0, i == options.Length - 1 ? 0 : 8, 0), IsDefault = i == 0 };
            var key = i == 0 ? "PrimaryButton" : "GhostButton";
            if (Application.Current?.TryFindResource(key) is Style st) b.Style = st;
            b.Click += (_, _) => { res = idx; w.Close(); };
            btns.Children.Add(b);
        }
        sp.Children.Add(btns);
        root.Child = sp;
        w.Content = root;
        w.ShowDialog();
        return res;
    }

    public static void Info(string title, string message, string okText = "Aceptar")
    {
        var d = Application.Current?.Dispatcher;
        if (d != null && !d.CheckAccess()) { d.Invoke(() => Info(title, message, okText)); return; }
        var w = new Window
        {
            Title = title,
            Width = 560,
            SizeToContent = SizeToContent.Height,
            MinHeight = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = FindOwner(),
            ResizeMode = ResizeMode.NoResize,
            Background = Brush("BgBrush", "#0F1115"),
            Foreground = Brush("TextBrush", "#E6E8EE"),
            WindowStyle = WindowStyle.ToolWindow
        };
        ApplyDarkTitle(w);

        var root = new Border { Padding = new Thickness(16), Background = Brush("BgBrush", "#0F1115") };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
            Foreground = Brush("TextBrush", "#E6E8EE")
        });

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = okText, MinWidth = 90, IsDefault = true, IsCancel = true };
        if (Application.Current?.TryFindResource("PrimaryButton") is Style ps) ok.Style = ps;
        btns.Children.Add(ok);
        sp.Children.Add(btns);
        root.Child = sp;
        w.Content = root;
        ok.Click += (s, e) => w.Close();
        w.ShowDialog();
    }
}