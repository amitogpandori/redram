using System.IO;
using System.Windows;
using System.Windows.Controls;
namespace SmartRAM;

public partial class CleanupConfirmWindow : Window
{
    public sealed record ConfirmRow(string Name, string Size, long Bytes, string Kind, string Folder);
    readonly bool requireAck;

    public CleanupConfirmWindow(IReadOnlyList<FileItem> items, IReadOnlyList<(FileItem Item, string Reason)> blocked, IReadOnlyList<FileItem> keptBack, bool requireAck)
    {
        InitializeComponent();
        this.requireAck = requireAck;
        long bytes = items.Sum(x => x.Bytes);
        Headline.Text = $"Move {items.Count:N0} {(items.Count == 1 ? "file" : "files")} ({StorageWindow.FormatBytes(bytes)}) to the Recycle Bin?";
        ItemsGrid.ItemsSource = items.OrderByDescending(x => x.Bytes).Select(x => new ConfirmRow(Path.GetFileName(x.Path), StorageWindow.FormatBytes(x.Bytes), x.Bytes, Kind(x), Path.GetDirectoryName(x.Path) ?? "")).ToList();
        foreach (var g in items.GroupBy(Kind).OrderByDescending(g => g.Sum(x => x.Bytes)))
            Chips.Children.Add(Chip($"{g.Key}  ·  {g.Count():N0}  ·  {StorageWindow.FormatBytes(g.Sum(x => x.Bytes))}"));
        var notes = new List<string>();
        if (keptBack.Count > 0) notes.Add($"★ {keptBack.Count:N0} file(s) were kept so at least one copy of every duplicate set survives.");
        if (blocked.Count > 0) notes.Add($"🔒 {blocked.Count:N0} ticked file(s) will be left alone (locked, changed since the scan, cloud-only, or on a drive without a Recycle Bin).");
        notes.Add("Windows, installed apps, Program Files, ProgramData, AppData and programs/scripts are never removed. A report is saved to %LocalAppData%\\RED RAM\\History.");
        Notes.Text = string.Join("\n", notes);
        ConfirmButton.Content = $"🗑  Move {items.Count:N0} to Recycle Bin";
        AckCheck.Visibility = requireAck ? Visibility.Visible : Visibility.Collapsed;
        ConfirmButton.IsEnabled = !requireAck;
    }

    static string Kind(FileItem x) => x.Safety == FileSafety.SafeToClean ? "Safe junk" : x.DuplicateGroup != null ? "Duplicate copy" : x.SimilarGroup != null ? "Similar photo" : x.Category;

    static Border Chip(string text) => new()
    {
        Style = (Style)Application.Current.FindResource("Pill"),
        Margin = new Thickness(0, 0, 6, 6),
        Child = new TextBlock { Text = text, FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("MonoFont"), FontSize = 11.5, Foreground = Palette.Text }
    };

    void Ack_Changed(object s, RoutedEventArgs e) => ConfirmButton.IsEnabled = !requireAck || AckCheck.IsChecked == true;
    void Confirm_Click(object s, RoutedEventArgs e) { if (requireAck && AckCheck.IsChecked != true) return; DialogResult = true; }
    void Cancel_Click(object s, RoutedEventArgs e) => DialogResult = false;
}
