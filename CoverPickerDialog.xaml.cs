using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using RecompVault.Models;
using RecompVault.Services;

namespace RecompVault;

public partial class CoverPickerDialog : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public class CandidateItem : INotifyPropertyChanged
    {
        public string Url { get; set; } = "";
        public string? ThumbUrl { get; set; }
        public string Source { get; set; } = "";
        public string Title { get; set; } = "";
        private BitmapImage? _thumb;
        public BitmapImage? ThumbImage { get => _thumb; set { _thumb = value; OnPropertyChanged(); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    private readonly GameEntry _game;
    private readonly AppConfig _cfg;
    public bool Applied { get; private set; }
    public ObservableCollection<CandidateItem> Results { get; } = new();

    public CoverPickerDialog(GameEntry game, AppConfig cfg)
    {
        InitializeComponent();
        _game = game; _cfg = cfg;
        ResultsList.ItemsSource = Results;
        HeaderText.Text = $"Elegir portada online: {game.Name}";
        QueryBox.Text = game.Name;
        SourceInitialized += (_, _) => TryEnableDarkTitleBar();
        Loaded += async (_, _) => { QueryBox.Focus(); await SearchAsync(); };
    }

    private void TryEnableDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int useDark = 1;
            DwmSetWindowAttribute(hwnd, 20, ref useDark, sizeof(int));
            DwmSetWindowAttribute(hwnd, 19, ref useDark, sizeof(int));
        }
        catch { }
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();

    private async Task SearchAsync()
    {
        var source = (SourceBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "quiver";
        var query = QueryBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query)) query = _game.Name;
        Results.Clear();
        StatusText.Text = $"Buscando en {source}...";
        StatusText.Visibility = Visibility.Visible;
        SearchBtn.IsEnabled = false;
        try
        {
            var found = await CoverDownloadService.SearchAsync(source, query, _game, _cfg);
            foreach (var c in found)
            {
                var item = new CandidateItem { Url = c.Url, ThumbUrl = c.ThumbUrl, Source = c.Source, Title = c.Title };
                Results.Add(item);
                _ = LoadThumbAsync(item);
            }
            StatusText.Text = Results.Count == 0 ? "No se encontraron resultados. Prueba otro origen o búsqueda." : "";
            StatusText.Visibility = Results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error al buscar: " + ex.Message;
            StatusText.Visibility = Visibility.Visible;
        }
        finally { SearchBtn.IsEnabled = true; }
    }

    private static async Task LoadThumbAsync(CandidateItem item)
    {
        try
        {
            var src = item.ThumbUrl ?? item.Url;
            BitmapImage bmp;
            if (File.Exists(src))
            {
                var bytes = await File.ReadAllBytesAsync(src);
                bmp = LoadBitmap(bytes);
            }
            else
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                try { http.DefaultRequestHeaders.UserAgent.ParseAdd("RecompVault/1.0"); } catch { }
                var bytes = await http.GetByteArrayAsync(src);
                bmp = LoadBitmap(bytes);
            }
            bmp.Freeze();
            var d = Application.Current?.Dispatcher;
            if (d != null && !d.CheckAccess()) d.Invoke(() => item.ThumbImage = bmp);
            else item.ThumbImage = bmp;
        }
        catch { }
    }

    private static BitmapImage LoadBitmap(byte[] bytes)
    {
        var ms = new MemoryStream(bytes);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.DecodePixelWidth = 300; // thumbnail
        bmp.EndInit();
        return bmp;
    }

    private async void Item_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not CandidateItem item) return;
        StatusText.Text = "Aplicando portada seleccionada...";
        StatusText.Visibility = Visibility.Visible;
        SearchBtn.IsEnabled = false;
        try
        {
            bool ok;
            if (File.Exists(item.Url))
            {
                // copy local file as cover
                var tag = "onlinepick_" + DateTime.Now.Ticks;
                var cfg = _cfg;
                // reuse SaveLocal via ApplyFromUrl not possible for local path format differences; do inline
                ok = await Task.Run(async () =>
                {
                    try
                    {
                        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RecompVault", "covers");
                        Directory.CreateDirectory(dir);
                        var ext = Path.GetExtension(item.Url); if (string.IsNullOrWhiteSpace(ext)) ext = ".png";
                        var dest = Path.Combine(dir, tag + ext);
                        var bytes = await File.ReadAllBytesAsync(item.Url);
                        await File.WriteAllBytesAsync(dest, bytes);
                        cfg.CoverOverrides[_game.FolderPath] = dest;
                        ConfigService.Save(cfg);
                        // apply
                        var bmp = new BitmapImage();
                        bmp.BeginInit(); bmp.UriSource = new Uri(dest, UriKind.Absolute); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.EndInit(); bmp.Freeze();
                        var d = Application.Current?.Dispatcher;
                        if (d != null && !d.CheckAccess()) d.Invoke(() => _game.CoverImage = bmp); else _game.CoverImage = bmp;
                        return true;
                    }
                    catch { return false; }
                });
            }
            else
            {
                ok = await CoverDownloadService.ApplyFromUrlAsync(_game, item.Url, _cfg, "onlinepick");
            }

            if (ok)
            {
                Applied = true;
                DialogResult = true;
                Close();
            }
            else
            {
                StatusText.Text = "No se pudo descargar/aplicar la portada seleccionada.";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error: " + ex.Message;
        }
        finally { SearchBtn.IsEnabled = true; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}