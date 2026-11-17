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
        private System.Windows.Media.ImageSource? _thumb;
        public System.Windows.Media.ImageSource? ThumbImage { get => _thumb; set { _thumb = value; OnPropertyChanged(); } }
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
        QueryBox.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await SearchAsync(); };
        Loaded += async (_, _) => { SelectInitialSource(); QueryBox.Focus(); QueryBox.SelectAll(); await SearchAsync(); };
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

    private CancellationTokenSource? _searchCts;

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();

    /// <summary>Elige como origen inicial el primero que tenga credenciales (Quiver/Libretro no necesitan).</summary>
    private void SelectInitialSource()
    {
        foreach (var item in SourceBox.Items.OfType<ComboBoxItem>())
        {
            var tag = item.Tag?.ToString() ?? "";
            if (CoverDownloadService.MissingCredentials(tag, _cfg) == null) { SourceBox.SelectedItem = item; return; }
        }
    }

    private async Task SearchAsync()
    {
        var source = (SourceBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "quiver";
        var query = QueryBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query)) query = _game.Name;
        Results.Clear();

        var missing = CoverDownloadService.MissingCredentials(source, _cfg);
        if (missing != null)
        {
            StatusText.Text = missing;
            StatusText.Visibility = Visibility.Visible;
            return;
        }

        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        StatusText.Text = $"Buscando en {(SourceBox.SelectedItem as ComboBoxItem)?.Content}...";
        StatusText.Visibility = Visibility.Visible;
        SearchBtn.IsEnabled = false;
        try
        {
            var found = await CoverDownloadService.SearchAsync(source, query, _game, _cfg, cts.Token);
            if (cts.IsCancellationRequested) return;
            foreach (var c in found)
            {
                var item = new CandidateItem { Url = c.Url, ThumbUrl = c.ThumbUrl, Source = c.Source, Title = c.Title };
                Results.Add(item);
                _ = LoadThumbAsync(item, cts.Token);
            }
            StatusText.Text = Results.Count == 0 ? "Sin resultados. Prueba otro origen o cambia la búsqueda." : "";
            StatusText.Visibility = Results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText.Text = "Error al buscar: " + ex.Message;
            StatusText.Visibility = Visibility.Visible;
        }
        finally { SearchBtn.IsEnabled = true; }
    }

    private static readonly SemaphoreSlim ThumbGate = new(6);

    private static async Task LoadThumbAsync(CandidateItem item, CancellationToken ct)
    {
        await ThumbGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var src = item.ThumbUrl ?? item.Url;
            var bytes = File.Exists(src)
                ? await File.ReadAllBytesAsync(src, ct).ConfigureAwait(false)
                : await CoverDownloadService.Http.GetByteArrayAsync(src, ct).ConfigureAwait(false);
            var bmp = ImageLoader.Load(bytes, 300);
            if (bmp != null) UiThread.Run(() => item.ThumbImage = bmp);
        }
        catch { }
        finally { ThumbGate.Release(); }
    }

    private async void Item_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not CandidateItem item) return;
        StatusText.Text = "Aplicando portada...";
        StatusText.Visibility = Visibility.Visible;
        SearchBtn.IsEnabled = false;
        try
        {
            var ok = File.Exists(item.Url)
                ? await CoverDownloadService.ApplyFromFileAsync(_game, item.Url, _cfg)
                : await CoverDownloadService.ApplyFromUrlAsync(_game, item.Url, _cfg, "manual");
            if (ok)
            {
                Applied = true;
                DialogResult = true;
                Close();
            }
            else StatusText.Text = "No se pudo descargar o leer esa imagen. Prueba otra.";
        }
        catch (Exception ex) { StatusText.Text = "Error: " + ex.Message; }
        finally { SearchBtn.IsEnabled = true; }
    }

    protected override void OnClosed(EventArgs e)
    {
        _searchCts?.Cancel();
        base.OnClosed(e);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}