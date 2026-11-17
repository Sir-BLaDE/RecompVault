using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using RecompVault.Core;
using RecompVault.Models;
using RecompVault.Services;
using RecompVault.ViewModels;

namespace RecompVault;

/// <summary>Lista de copias de deshacer de toda la biblioteca: espacio ocupado, deshacer o borrar.</summary>
public partial class UndoManagerDialog : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public sealed class Row
    {
        public GameEntry Game { get; init; } = null!;
        public UndoManifest Manifest { get; init; } = null!;
        public string Title { get; init; } = "";
        public string Detail { get; init; } = "";
        public string Size { get; init; } = "";
    }

    private readonly MainViewModel _vm;

    public UndoManagerDialog(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        SourceInitialized += (_, _) =>
        {
            try { int d = 1; var h = new WindowInteropHelper(this).Handle; if (DwmSetWindowAttribute(h, 20, ref d, 4) != 0) DwmSetWindowAttribute(h, 19, ref d, 4); } catch { }
        };
        Refresh();
    }

    private void Refresh()
    {
        var rows = new List<Row>();
        foreach (var g in _vm.AllGames)
        {
            foreach (var (_, m) in UndoStore.List(g.FolderPath))
            {
                rows.Add(new Row
                {
                    Game = g, Manifest = m,
                    Title = $"{g.Name}   {(m.FromVersion ?? "?")} → {m.ToVersion}",
                    Detail = $"{m.CreatedUtc.ToLocalTime():dd/MM/yyyy HH:mm} · {m.AssetName} · {m.Replaced.Count} reemplazados, {m.Created.Count} nuevos · {g.FolderPath}",
                    Size = $"{m.BackupBytes / 1048576.0:0.#} MB"
                });
            }
            g.HasUndo = UndoStore.Latest(g.FolderPath) != null;
        }
        rows = rows.OrderByDescending(r => r.Manifest.CreatedUtc).ToList();
        List.ItemsSource = rows;
        var total = rows.Sum(r => r.Manifest.BackupBytes);
        SummaryText.Text = rows.Count == 0
            ? "No hay copias. Se crean automáticamente cada vez que RecompVault actualiza un juego."
            : $"{rows.Count} copias · {total / 1048576.0:0.#} MB en total. Deshacer solo afecta a la última actualización de cada juego.";
    }

    private List<Row> Selected() => List.SelectedItems.OfType<Row>().ToList();

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        var r = Selected().FirstOrDefault();
        if (r == null) return;
        _vm.UndoUpdate(r.Game);
        // UndoUpdate trabaja en segundo plano; refrescar al volver
        Dispatcher.BeginInvoke(new Action(async () => { await Task.Delay(1500); Refresh(); }));
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        if (!DialogService.Confirm("Borrar copias", $"¿Borrar {sel.Count} copias de deshacer? Esas actualizaciones ya no se podrán deshacer.", "Borrar", "Cancelar")) return;
        foreach (var g in sel.Select(r => r.Game).Distinct()) UndoStore.Discard(g.FolderPath);
        Refresh();
    }

    private void DeleteAll_Click(object sender, RoutedEventArgs e)
    {
        if (!DialogService.Confirm("Borrar todas", "¿Borrar todas las copias de deshacer de la biblioteca?", "Borrar todas", "Cancelar")) return;
        foreach (var g in _vm.AllGames) UndoStore.Discard(g.FolderPath);
        Refresh();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
