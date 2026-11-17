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

    public static string? Prompt(string title, string message, string defaultValue = "", string okText = "Guardar")
    {
        var w = new Window
        {
            Title = title,
            Width = 520,
            Height = 190,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive) ?? Application.Current?.MainWindow,
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
            Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive) ?? Application.Current?.MainWindow,
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

    public static void Info(string title, string message, string okText = "Aceptar")
    {
        var w = new Window
        {
            Title = title,
            Width = 560,
            SizeToContent = SizeToContent.Height,
            MinHeight = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive) ?? Application.Current?.MainWindow,
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