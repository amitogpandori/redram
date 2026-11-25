using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
namespace SmartRAM;

public partial class StorageWindow : Window
{
    CancellationTokenSource? cts;
    CancellationTokenSource? deleteCts;
    readonly ManualResetEventSlim pauseGate = new(true);
    StorageScanResult? result;
    List<FileRow> allRows = new();
    List<FileRow> visible = new();
    readonly HashSet<FileRow> ticked = new();
    Dictionary<string, int> duplicateCounts = new();
    long tickedBytes;
    DateTime scanStarted;
    int scanGeneration;
    bool scanning, deleting, suppressCategoryEvent;
    FileRow? anchor;
    int previewVersion;
    string currentCategory = "all";
    readonly DispatcherTimer summaryTimer;
    readonly DispatcherTimer searchTimer;
    const long LargeFileBytes = 250L * 1024 * 1024;
    const double CategoryBarMax = 196, SizeBarMax = 96;

    sealed record DriveChoice(string Root, string Label) { public override string ToString() => Label; }

    public StorageWindow()
    {
        InitializeComponent();
        summaryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        summaryTimer.Tick += (_, _) => { summaryTimer.Stop(); UpdateSelectionSummary(); };
        searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); RefreshView(); };
        LoadDrives();
        BuildCategories();
        UpdateSelectionSummary();
        ShowPreview(null);
    }

    // ───────────────────────── helpers ─────────────────────────
    public static string FormatBytes(long b)
    {
        if (b < 0) b = 0;
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b} B" : v >= 100 ? $"{v:F0} {u[i]}" : v >= 10 ? $"{v:F1} {u[i]}" : $"{v:F2} {u[i]}";
    }
    static string Plural(long n, string one, string many) => n == 1 ? one : many;

    void LoadDrives()
    {
        var list = new List<DriveChoice>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? d.DriveType.ToString() : d.VolumeLabel;
                list.Add(new DriveChoice(d.Name, $"{d.Name}  {label}  ·  {FormatBytes(d.AvailableFreeSpace)} free"));
            }
            catch { }
        }
        DriveBox.ItemsSource = list;
        DriveBox.SelectedIndex = list.Count > 0 ? 0 : -1;
    }
    string Root() => (DriveBox.SelectedItem as DriveChoice)?.Root ?? "C:\\";

    void SetScanning(bool on)
    {
        scanning = on;
        ScanButton.IsEnabled = !on && !deleting;
        PauseButton.IsEnabled = on;
        StopButton.IsEnabled = on || deleting;
        DriveBox.IsEnabled = !on;
        if (!on) PauseButton.Content = "Pause";
        UpdateSelectionSummary();
    }

    // ───────────────────────── scan ─────────────────────────
    async void Scan_Click(object s, RoutedEventArgs e)
    {
        if (scanning || deleting) return;
        var cats = new HashSet<string>();
        if (VideosFilter.IsChecked == true) cats.Add("Videos");
        if (ImagesFilter.IsChecked == true) cats.Add("Images");
        if (AudioFilter.IsChecked == true) cats.Add("Audio");
        if (DocumentsFilter.IsChecked == true) cats.Add("Documents");
        if (ArchivesFilter.IsChecked == true) cats.Add("Archives");
        if (OtherFilter.IsChecked == true) cats.Add("Other");
        if (cats.Count == 0) { MessageBox.Show(this, "Turn on at least one file type.", "RED RAM Storage Cleaner"); return; }
        if (!double.TryParse(MinSizeBox.Text, out var minMb) || minMb < 0) { MessageBox.Show(this, "Minimum size must be zero or greater.", "RED RAM Storage Cleaner"); return; }
        var maxMb = string.IsNullOrWhiteSpace(MaxSizeBox.Text) ? double.MaxValue : double.TryParse(MaxSizeBox.Text, out var mx) ? mx : -1;
        if (maxMb < minMb) { MessageBox.Show(this, "Maximum size must be greater than or equal to minimum size.", "RED RAM Storage Cleaner"); return; }

        var gen = ++scanGeneration;
        cts?.Dispose(); cts = new CancellationTokenSource();
        pauseGate.Set();
        SetScanning(true);
        ScanProgressBar.Value = 0; PercentText.Text = "0%";
        scanStarted = DateTime.Now;
        Status.Text = "Starting scan…"; CurrentScanItem.Text = "";
        var root = Root();
        var progress = new Progress<ScanProgress>(p =>
        {
            if (gen != scanGeneration) return;
            ScanProgressBar.Value = p.Percent;
            PercentText.Text = $"{p.Percent:F0}%";
            CurrentScanItem.Text = string.IsNullOrWhiteSpace(p.Current) ? "" : $"› {p.Current}";
            var elapsed = DateTime.Now - scanStarted;
            var folders = p.Folders > 0 ? $" · {p.Folders:N0} folders" : "";
            Status.Text = $"{p.Phase.ToUpperInvariant()} · {p.Files:N0} files · {FormatBytes(p.Bytes)}{folders} · {p.Skipped:N0} skipped · {elapsed:mm\\:ss}";
        });
        try
        {
            var r = await StorageScanner.ScanAsync(root, progress, cts.Token, cats, (long)(minMb * 1048576), maxMb == double.MaxValue ? long.MaxValue : (long)(maxMb * 1048576), pauseGate, SimilarCheck.IsChecked == true);
            if (gen != scanGeneration) return;
            ScanProgressBar.Value = 100; PercentText.Text = "100%";
            LoadResult(r, keepTicks: false);
            var took = DateTime.Now - scanStarted;
            Status.Text = $"COMPLETE · {r.Files.Count:N0} files · {FormatBytes(r.TotalBytes)} · {r.Skipped:N0} skipped (no access) · {took:mm\\:ss}";
            CurrentScanItem.Text = "Pick a category on the left. Tick files or use Smart select, then press Review & move to Recycle Bin.";
        }
        catch (OperationCanceledException) { if (gen == scanGeneration) Status.Text = "Scan stopped safely — nothing was changed."; }
        catch (Exception ex) { if (gen == scanGeneration) { Status.Text = "Scan ended with an error."; MessageBox.Show(this, ex.Message, "RED RAM Storage Cleaner"); } }
        finally { if (gen == scanGeneration) SetScanning(false); }
    }

    void LoadResult(StorageScanResult r, bool keepTicks)
    {
        var keep = keepTicks ? new HashSet<string>(ticked.Select(x => x.Item.Path), StringComparer.OrdinalIgnoreCase) : null;
        result = r;
        ticked.Clear(); tickedBytes = 0;
        allRows = r.Files.Select(f => new FileRow(f, OnRowTicked)).ToList();
        if (keep is not null)
            foreach (var row in allRows)
                if (keep.Contains(row.Item.Path) && row.CanSelect) { row.SetSelectedSilently(true); ticked.Add(row); tickedBytes += row.Bytes; }
        duplicateCounts = r.Files.Where(x => x.DuplicateGroup != null).GroupBy(x => x.DuplicateGroup!).ToDictionary(g => g.Key, g => g.Count());
        SafeSummary.Text = $"SAFE JUNK {FormatBytes(r.SafeBytes)}";
        DuplicateSummary.Text = $"DUPLICATES {FormatBytes(r.DuplicateRecoverableBytes)}";
        BuildCategories();
        BuildDriveMap();
        RefreshView();
        UpdateSelectionSummary();
    }

    void OnRowTicked(FileRow row, bool on)
    {
        if (on) { if (ticked.Add(row)) tickedBytes += row.Bytes; }
        else if (ticked.Remove(row)) tickedBytes -= row.Bytes;
        summaryTimer.Stop(); summaryTimer.Start();
    }

    // ───────────────────────── categories & drive map ─────────────────────────
    static Func<FileRow, bool> CardPredicate(string key)
    {
        var yearAgo = DateTime.Now.AddYears(-1);
        return key switch
        {
            "junk" => r => r.Item.Safety == FileSafety.SafeToClean && !r.Item.CloudOnly,
            "large" => r => r.Bytes >= LargeFileBytes,
            "dupes" => r => r.Item.DuplicateGroup != null && !r.Item.DuplicateKeeper,
            "similar" => r => r.Item.SimilarGroup != null,
            "downloads" => r => r.IsOldDownload,
            "old" => r => r.CanSelect && r.Item.ModifiedAt < yearAgo,
            "locked" => r => !r.CanSelect,
            "Videos" or "Images" or "Audio" or "Documents" or "Archives" or "Other" => r => r.Item.Category == key,
            _ => _ => true
        };
    }
    Func<FileRow, bool> ViewPredicate(string key)
    {
        if (key == "dupes" && ShowKeepersCheck.IsChecked == true) return r => r.Item.DuplicateGroup != null;
        return CardPredicate(key);
    }

    static readonly (string Key, string Glyph, string Title, string Hint)[] CategoryDefs =
    {
        ("all", "◉", "All scanned files", "Everything the scan found that matches your type and size filters."),
        ("junk", "✓", "Safe junk", "Old Windows temp files not used for 7+ days. The only files 'Clean safe junk' touches."),
        ("large", "▲", "Large files", "Single files of 250 MB or more — the fastest way to free space."),
        ("dupes", "⧉", "Duplicates", "Byte-for-byte identical copies. Size shown is what you free by keeping one copy of each."),
        ("similar", "◐", "Similar photos", "Pictures that look almost the same (bursts, edits, resized copies)."),
        ("downloads", "⇩", "Old downloads", "Files in your Downloads folder not changed for 90+ days."),
        ("old", "⌛", "Untouched for a year", "Your files not modified for more than a year."),
        ("Videos", "▶", "Videos", "Video files"),
        ("Images", "▣", "Images", "Pictures and photos"),
        ("Audio", "♪", "Audio", "Music and recordings"),
        ("Documents", "≡", "Documents", "PDF, Office and text documents"),
        ("Archives", "▤", "Archives", "ZIP, RAR, 7z, ISO and other archives"),
        ("Other", "•", "Other", "Everything else"),
        ("locked", "🔒", "Locked", "Windows, installed apps, Program Files, AppData, programs and cloud-only files. RED RAM never deletes these."),
    };

    void BuildCategories()
    {
        var cards = new List<CategoryCard>();
        long total = Math.Max(1, allRows.Sum(r => r.Bytes));
        foreach (var d in CategoryDefs)
        {
            var pred = CardPredicate(d.Key);
            long count = 0, bytes = 0;
            foreach (var r in allRows) if (pred(r)) { count++; bytes += r.Bytes; }
            cards.Add(new CategoryCard(d.Key, d.Glyph, d.Title, d.Hint, Palette.ForKey(d.Key), count, bytes,
                result is null ? 0 : Math.Max(count > 0 ? 3 : 0, CategoryBarMax * bytes / total)));
        }
        suppressCategoryEvent = true;
        CategoryList.ItemsSource = cards;
        CategoryList.SelectedItem = cards.FirstOrDefault(c => c.Key == currentCategory) ?? cards[0];
        suppressCategoryEvent = false;
    }

    void BuildDriveMap()
    {
        DriveMap.Children.Clear(); DriveMap.ColumnDefinitions.Clear();
        if (result is null) return;
        long used = 0, size = 0;
        try { var d = new DriveInfo(Path.GetPathRoot(result.Root)!); size = d.TotalSize; used = d.TotalSize - d.AvailableFreeSpace; } catch { }
        var parts = result.Files.GroupBy(f => f.Category).Select(g => (Key: g.Key, Bytes: g.Sum(x => x.Bytes))).OrderByDescending(x => x.Bytes).ToList();
        long matched = parts.Sum(p => p.Bytes);
        var segments = parts.Select(p => (p.Key, p.Bytes, Brush: Palette.ForKey(p.Key))).ToList();
        if (used > matched) segments.Add(("Not in scan", used - matched, Palette.Dim));
        int col = 0;
        foreach (var sgm in segments.Where(x => x.Bytes > 0))
        {
            DriveMap.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(sgm.Bytes, GridUnitType.Star) });
            var b = new Border { Background = sgm.Brush, Margin = new Thickness(0, 0, 1, 0), ToolTip = $"{sgm.Key}: {FormatBytes(sgm.Bytes)}" };
            Grid.SetColumn(b, col++); DriveMap.Children.Add(b);
        }
        if (size > used) DriveMap.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(size - used, GridUnitType.Star) });
        DriveUsageText.Text = size > 0 ? $"{result.Root}  {FormatBytes(used)} used / {FormatBytes(size)}" : result.Root;
        DriveMapLegend.Text = string.Join("  ·  ", parts.Take(3).Select(p => $"{p.Key} {FormatBytes(p.Bytes)}")) + (used > matched ? $"  ·  rest {FormatBytes(used - matched)}" : "");
    }

    void Category_Changed(object s, SelectionChangedEventArgs e)
    {
        if (suppressCategoryEvent || CategoryList.SelectedItem is not CategoryCard c) return;
        currentCategory = c.Key;
        RefreshView();
    }
    void SelectCategory(string key)
    {
        if (CategoryList.ItemsSource is not IEnumerable<CategoryCard> cards) return;
        var c = cards.FirstOrDefault(x => x.Key == key);
        if (c != null && !ReferenceEquals(CategoryList.SelectedItem, c)) CategoryList.SelectedItem = c; // triggers RefreshView
        else RefreshView();
    }

    // ───────────────────────── list view ─────────────────────────
    void RefreshView()
    {
        if (!IsInitialized) return;
        var def = CategoryDefs.FirstOrDefault(d => d.Key == currentCategory);
        ViewTitle.Text = def.Title ?? "All scanned files";
        if (result is null)
        {
            FilesGrid.ItemsSource = null; visible = new();
            ViewSubtitle.Text = "Run a scan to see your files.";
            EmptyText.Text = "Press  ⟳ Scan drive  to start."; EmptyText.Visibility = Visibility.Visible;
            return;
        }
        IEnumerable<FileRow> q = allRows.Where(ViewPredicate(currentCategory));
        switch (ShowBox.SelectedIndex)
        {
            case 1: q = q.Where(r => r.CanSelect); break;
            case 2: q = q.Where(r => r.Selected); break;
            case 3: q = q.Where(r => !r.CanSelect); break;
        }
        var find = SearchBox.Text?.Trim();
        if (!string.IsNullOrEmpty(find)) q = q.Where(r => r.Item.Path.Contains(find, StringComparison.OrdinalIgnoreCase) || (r.Item.Owner?.Contains(find, StringComparison.OrdinalIgnoreCase) ?? false));
        q = currentCategory switch
        {
            "dupes" => q.OrderByDescending(r => r.Bytes).ThenBy(r => r.Item.DuplicateGroup).ThenByDescending(r => r.Item.DuplicateKeeper),
            "similar" => q.OrderBy(r => r.Item.SimilarGroup).ThenByDescending(r => r.Item.Similarity),
            _ => q.OrderByDescending(r => r.Bytes)
        };
        visible = q.ToList();
        long max = visible.Count == 0 ? 0 : visible.Max(r => r.Bytes);
        foreach (var r in visible) r.BarWidth = max <= 0 ? 0 : Math.Max(2, SizeBarMax * r.Bytes / max);
        FilesGrid.ItemsSource = visible;
        anchor = null;
        long sum = visible.Sum(r => r.Bytes);
        int removable = visible.Count(r => r.CanSelect);
        ViewSubtitle.Text = $"{visible.Count:N0} {Plural(visible.Count, "file", "files")} · {FormatBytes(sum)} · {removable:N0} removable · {visible.Count - removable:N0} locked";
        EmptyText.Text = string.IsNullOrEmpty(find) ? "Nothing in this category — nice and clean." : "No files match your search.";
        EmptyText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Filter_Changed(object s, SelectionChangedEventArgs e) { if (IsLoaded) RefreshView(); }
    void KeeperView_Changed(object s, RoutedEventArgs e) { if (IsLoaded) RefreshView(); }
    void Search_Changed(object s, TextChangedEventArgs e) { if (!IsLoaded) return; searchTimer.Stop(); searchTimer.Start(); }
    void FilesGrid_Sorting(object s, DataGridSortingEventArgs e) { anchor = null; }
    void ViewMode_Changed(object s, SelectionChangedEventArgs e)
    {
        if (FilesGrid is null || ViewModeBox?.SelectedItem is not ComboBoxItem i) return;
        var compact = i.Content?.ToString() == "Compact";
        FilesGrid.MinRowHeight = compact ? 24 : 34;
        FilesGrid.RowHeight = compact ? 24 : double.NaN;
    }

    void SizePreset_Changed(object s, SelectionChangedEventArgs e)
    {
        if (MinSizeBox is null || MaxSizeBox is null || SizePresetBox.SelectedItem is not ComboBoxItem i) return;
        var name = i.Content?.ToString();
        var custom = name == "Custom";
        MinSizeBox.IsEnabled = custom; MaxSizeBox.IsEnabled = custom;
        switch (name)
        {
            case "Under 10 MB": MinSizeBox.Text = "0"; MaxSizeBox.Text = "10"; break;
            case "10–100 MB": MinSizeBox.Text = "10"; MaxSizeBox.Text = "100"; break;
            case "100–500 MB": MinSizeBox.Text = "100"; MaxSizeBox.Text = "500"; break;
            case "500 MB–1 GB": MinSizeBox.Text = "500"; MaxSizeBox.Text = "1024"; break;
            case "1–5 GB": MinSizeBox.Text = "1024"; MaxSizeBox.Text = "5120"; break;
            case "5–10 GB": MinSizeBox.Text = "5120"; MaxSizeBox.Text = "10240"; break;
            case "10 GB+": MinSizeBox.Text = "10240"; MaxSizeBox.Text = ""; break;
            case "Any size": MinSizeBox.Text = "0"; MaxSizeBox.Text = ""; break;
            case "Custom": if (string.IsNullOrWhiteSpace(MinSizeBox.Text)) MinSizeBox.Text = "0"; break;
        }
    }

    void Pause_Click(object s, RoutedEventArgs e)
    {
        if (pauseGate.IsSet) { pauseGate.Reset(); PauseButton.Content = "Resume"; Status.Text = "Scan paused."; }
        else { pauseGate.Set(); PauseButton.Content = "Pause"; Status.Text = "Resuming…"; }
    }
    void Stop_Click(object s, RoutedEventArgs e) { pauseGate.Set(); cts?.Cancel(); deleteCts?.Cancel(); }

    // ───────────────────────── ticking / selection ─────────────────────────
    void SetTicks(IEnumerable<FileRow> rows, bool on) { foreach (var r in rows.ToList()) if (r.CanSelect || !on) r.Selected = on; }

    void SelectVisible_Click(object s, RoutedEventArgs e) => SetTicks(visible.Where(r => r.CanSelect), true);
    void ClearSelection_Click(object s, RoutedEventArgs e) => SetTicks(visible.Where(r => r.Selected), false);
    void Invert_Click(object s, RoutedEventArgs e) { foreach (var r in visible.Where(r => r.CanSelect).ToList()) r.Selected = !r.Selected; }
    void HeaderCheck_Click(object s, RoutedEventArgs e)
    {
        var on = (s as CheckBox)?.IsChecked == true;
        if (on) SelectVisible_Click(s, e); else ClearSelection_Click(s, e);
    }

    void RowCheck_Click(object s, RoutedEventArgs e)
    {
        if (s is not CheckBox cb || cb.DataContext is not FileRow row) return;
        var on = cb.IsChecked == true;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && anchor is not null && !ReferenceEquals(anchor, row))
        {
            int a = FilesGrid.Items.IndexOf(anchor), b = FilesGrid.Items.IndexOf(row);
            if (a >= 0 && b >= 0)
                for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++)
                    if (FilesGrid.Items[i] is FileRow r && r.CanSelect) r.Selected = on;
        }
        anchor = row;
    }

    void SmartSelect_Click(object s, RoutedEventArgs e)
    {
        if (SmartButton.ContextMenu is null) return;
        SmartButton.ContextMenu.PlacementTarget = SmartButton;
        SmartButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        SmartButton.ContextMenu.IsOpen = true;
    }

    void SmartItem_Click(object s, RoutedEventArgs e)
    {
        if ((s as MenuItem)?.Tag is not string tag) return;
        if (result is null) { Status.Text = "Run a scan first."; return; }
        int before = ticked.Count; long beforeBytes = tickedBytes;
        string msg;
        switch (tag)
        {
            case "junk":
                SetTicks(allRows.Where(CardPredicate("junk")), true);
                SelectCategory("junk");
                msg = "ticked every safe junk file";
                break;
            case "dup-best":
            case "dup-newest":
            case "dup-oldest":
            {
                int sets = 0;
                foreach (var g in allRows.Where(r => r.Item.DuplicateGroup != null).GroupBy(r => r.Item.DuplicateGroup!))
                {
                    var copies = g.ToList();
                    if (copies.Count < 2) continue;
                    var keep = tag switch
                    {
                        "dup-newest" => copies.OrderByDescending(r => r.ModifiedAt).First(),
                        "dup-oldest" => copies.OrderBy(r => r.ModifiedAt).First(),
                        _ => copies.FirstOrDefault(r => r.Item.DuplicateKeeper) ?? copies.OrderByDescending(r => r.ModifiedAt).First()
                    };
                    keep.Selected = false;
                    foreach (var c in copies) if (!ReferenceEquals(c, keep) && c.CanSelect) c.Selected = true;
                    sets++;
                }
                ShowKeepersCheck.IsChecked = true;
                SelectCategory("dupes");
                msg = $"kept one copy in each of {sets:N0} duplicate {Plural(sets, "set", "sets")} ({(tag == "dup-newest" ? "newest" : tag == "dup-oldest" ? "oldest" : "RED RAM's pick")}) and ticked the rest";
                break;
            }
            case "similar":
            {
                int sets = 0;
                foreach (var g in allRows.Where(r => r.Item.SimilarGroup != null).GroupBy(r => r.Item.SimilarGroup!))
                {
                    var shots = g.ToList();
                    if (shots.Count < 2) continue;
                    var keep = shots.OrderByDescending(r => r.Bytes).ThenByDescending(r => r.ModifiedAt).First();
                    keep.Selected = false;
                    foreach (var c in shots) if (!ReferenceEquals(c, keep) && c.CanSelect) c.Selected = true;
                    sets++;
                }
                SelectCategory("similar");
                msg = $"kept the largest photo in each of {sets:N0} similar {Plural(sets, "set", "sets")} and ticked the rest";
                break;
            }
            case "gt1g": SetTicks(visible.Where(r => r.Bytes >= 1L << 30), true); msg = "ticked files over 1 GB in this view"; break;
            case "gt100m": SetTicks(visible.Where(r => r.Bytes >= 100L << 20), true); msg = "ticked files over 100 MB in this view"; break;
            case "old1y": { var t = DateTime.Now.AddYears(-1); SetTicks(visible.Where(r => r.ModifiedAt < t), true); msg = "ticked files not changed for a year in this view"; break; }
            case "old6m": { var t = DateTime.Now.AddMonths(-6); SetTicks(visible.Where(r => r.ModifiedAt < t), true); msg = "ticked files not changed for 6 months in this view"; break; }
            case "hl-on": SetTicks(FilesGrid.SelectedItems.OfType<FileRow>(), true); msg = "ticked the highlighted rows"; break;
            case "hl-off": SetTicks(FilesGrid.SelectedItems.OfType<FileRow>(), false); msg = "unticked the highlighted rows"; break;
            case "clear-all": SetTicks(ticked.ToList(), false); msg = "cleared every tick"; break;
            default: return;
        }
        UpdateSelectionSummary();
        var delta = ticked.Count - before;
        Status.Text = $"SMART SELECT · {msg} · {(delta >= 0 ? "+" : "")}{delta:N0} files ({(tickedBytes - beforeBytes >= 0 ? "+" : "-")}{FormatBytes(Math.Abs(tickedBytes - beforeBytes))}). Locked files are never ticked.";
    }

    void Window_PreviewKeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return; }
        if (!FilesGrid.IsKeyboardFocusWithin) return;
        if (e.Key == Key.Space)
        {
            var rows = FilesGrid.SelectedItems.OfType<FileRow>().Where(r => r.CanSelect).ToList();
            if (rows.Count == 0) return;
            var on = rows.Any(r => !r.Selected);
            foreach (var r in rows) r.Selected = on;
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && ticked.Count > 0) { Delete_Click(s, e); e.Handled = true; }
    }

    void UpdateSelectionSummary()
    {
        if (!IsInitialized) return;
        int n = ticked.Count;
        SelectionText.Text = $"{n:N0} {Plural(n, "file", "files")} ticked · {FormatBytes(tickedBytes)}";
        if (n == 0) SelectionDetail.Text = result is null ? "Scan a drive to get started." : "Tick files in the list, or use ✦ Smart select. Nothing is removed until you confirm.";
        else
        {
            int junk = ticked.Count(r => r.Item.Safety == FileSafety.SafeToClean);
            int dup = ticked.Count(r => r.Item.DuplicateGroup != null);
            int big = ticked.Count(r => r.Bytes >= LargeFileBytes);
            var parts = new List<string>();
            if (junk > 0) parts.Add($"{junk:N0} junk");
            if (dup > 0) parts.Add($"{dup:N0} duplicate {Plural(dup, "copy", "copies")}");
            if (big > 0) parts.Add($"{big:N0} large");
            int personal = n - junk;
            if (personal > 0) parts.Add($"{personal:N0} of your own {Plural(personal, "file", "files")}");
            SelectionDetail.Text = string.Join(" · ", parts) + " — all go to the Recycle Bin, restorable.";
        }
        SelectionDot.Fill = n > 0 ? Palette.Red : Palette.Dim;
        SelectionDot.Effect = n > 0 ? (System.Windows.Media.Effects.Effect)FindResource("RedGlow") : null;
        DeleteButton.IsEnabled = n > 0 && !scanning && !deleting;
        DeleteButton.Content = n > 0 ? $"🗑  Move {n:N0} to Recycle Bin · {FormatBytes(tickedBytes)}" : "🗑  Review & move to Recycle Bin";
        CleanSafeButton.IsEnabled = result is not null && result.SafeBytes > 0 && !scanning && !deleting;
        CleanSafeButton.Content = result is not null && result.SafeBytes > 0 ? $"✓  Clean safe junk · {FormatBytes(result.SafeBytes)}" : "✓  Clean safe junk";
    }

    // ───────────────────────── preview ─────────────────────────
    FileRow? Current() => FilesGrid.SelectedItem as FileRow;
    void FilesGrid_SelectionChanged(object s, SelectionChangedEventArgs e) => ShowPreview(Current());

    async void ShowPreview(FileRow? r)
    {
        var v = ++previewVersion;
        if (r is null)
        {
            PreviewImage.Source = null; PreviewPlaceholder.Visibility = Visibility.Visible;
            PreviewName.Text = "No file selected"; PreviewSize.Text = ""; PreviewReason.Text = "Click a row to see details here. Double-click opens the file.";
            PreviewFolder.Text = ""; PreviewDetails.Text = ""; PreviewGroup.Text = ""; PreviewSafetyBadge.Visibility = Visibility.Collapsed;
            return;
        }
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
        PreviewImage.Source = FileIconCache.Get(r.Item.Extension, large: true);
        PreviewImage.Width = 64; PreviewImage.Height = 64;
        PreviewName.Text = r.Name;
        PreviewSize.Text = r.Size;
        PreviewSafetyBadge.Visibility = Visibility.Visible;
        PreviewSafetyBadge.Background = r.SafetySoftBrush; PreviewSafetyBadge.BorderBrush = r.SafetyBrush;
        PreviewSafetyText.Text = r.SafetyLabel; PreviewSafetyText.Foreground = r.SafetyBrush;
        PreviewReason.Text = r.CanSelect ? r.Item.Reason : $"Locked — {r.Item.Reason}";
        PreviewFolder.Text = r.Folder;
        PreviewDetails.Text = $"type      {r.Type}\nmodified  {r.ModifiedAt:yyyy-MM-dd HH:mm}\ncreated   {r.Item.CreatedAt:yyyy-MM-dd HH:mm}\nowner     {r.Owner}";
        if (r.Item.DuplicateGroup is { } g)
        {
            duplicateCounts.TryGetValue(g, out var copies);
            var keeper = allRows.FirstOrDefault(x => x.Item.DuplicateGroup == g && x.Item.DuplicateKeeper);
            PreviewGroup.Text = $"⧉ {copies} identical copies. RED RAM suggests keeping: {keeper?.Folder ?? "—"}";
        }
        else if (r.Item.SimilarGroup is { } sg) PreviewGroup.Text = $"◐ Looks {r.Item.Similarity}% like other photos in set {sg}.";
        else PreviewGroup.Text = "";

        if (r.Item.Category == "Images" && !r.Item.CloudOnly && r.Bytes < 80L * 1024 * 1024)
        {
            var path = r.Item.Path;
            var img = await Task.Run(() => LoadThumbnail(path));
            if (v == previewVersion && img is not null) { PreviewImage.Width = double.NaN; PreviewImage.Height = double.NaN; PreviewImage.Source = img; }
        }
    }

    static BitmapSource? LoadThumbnail(string path)
    {
        try
        {
            using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bmp = new BitmapImage();
            bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.DecodePixelWidth = 320; bmp.StreamSource = fs; bmp.EndInit(); bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    void FilesGrid_DoubleClick(object s, MouseButtonEventArgs e)
    {
        // Ignore double-clicks on the header or on a checkbox.
        if (e.OriginalSource is DependencyObject d && FindParent<CheckBox>(d) is not null) return;
        if (e.OriginalSource is DependencyObject d2 && FindParent<DataGridRow>(d2) is null) return;
        Open(Current());
    }
    static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null && d is not T) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }
    void OpenFile_Click(object s, RoutedEventArgs e) => Open(Current());
    void Open(FileRow? r)
    {
        if (r is null) return;
        if (r.Item.CloudOnly && MessageBox.Show(this, "This is a cloud-only file. Opening it may download it from OneDrive. Continue?", "Cloud file", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try { Process.Start(new ProcessStartInfo(r.Item.Path) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "RED RAM"); }
    }
    void Explorer_Click(object s, RoutedEventArgs e)
    {
        var r = Current(); if (r is null) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{r.Item.Path}\"") { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "RED RAM"); }
    }
    void Properties_Click(object s, RoutedEventArgs e)
    {
        var r = Current(); if (r is null) return;
        try { var sei = new SHELLEXECUTEINFO { cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(), lpVerb = "properties", lpFile = r.Item.Path, nShow = 1, fMask = 0x0000000C }; ShellExecuteEx(ref sei); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "RED RAM"); }
    }
    void CopyPath_Click(object s, RoutedEventArgs e) { var r = Current(); if (r != null) try { Clipboard.SetText(r.Item.Path); Status.Text = "Path copied."; } catch { } }
    void OpenRecycleBin_Click(object s, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "RED RAM"); }
    }

    // ───────────────────────── cleanup ─────────────────────────
    async void CleanSafe_Click(object s, RoutedEventArgs e)
    {
        if (result is null || scanning || deleting) return;
        var candidates = result.Files.Where(x => x.Safety == FileSafety.SafeToClean && !x.CloudOnly).ToList();
        Status.Text = "Re-checking safe junk…";
        var safe = await Task.Run(() => candidates.Where(StorageScanner.CanDelete).ToList());
        await RunCleanup(safe, "safe junk", manual: false);
    }
    async void Delete_Click(object s, RoutedEventArgs e)
    {
        if (result is null || scanning || deleting || ticked.Count == 0) return;
        await RunCleanup(ticked.Select(r => r.Item).ToList(), "ticked", manual: true);
    }

    async Task RunCleanup(List<FileItem> items, string label, bool manual)
    {
        if (result is null) return;
        if (items.Count == 0) { MessageBox.Show(this, manual ? "Tick some files first." : "There is no safe junk left to clean.", "RED RAM Storage Cleaner"); return; }
        var selection = StorageScanner.KeepOneCopyPerDuplicateGroup(items, result.Files, out var keptBack);
        Status.Text = "Checking every file against RED RAM's safety rules…";
        var (allowed, blocked) = await Task.Run(() =>
        {
            var ok = new List<FileItem>(); var no = new List<(FileItem, string)>();
            foreach (var it in selection)
            {
                if (!manual && !StorageScanner.CanDelete(it)) { no.Add((it, "No longer qualifies as safe junk")); continue; }
                if (StorageScanner.CanRecycle(it, out var why)) ok.Add(it); else no.Add((it, why));
            }
            return (ok, no);
        });
        if (allowed.Count == 0)
        {
            var why = string.Join("\n", blocked.Take(8).Select(b => $"• {Path.GetFileName(b.Item1.Path)} — {b.Item2}"));
            MessageBox.Show(this, "Nothing can be moved.\n\n" + (keptBack.Count > 0 ? $"{keptBack.Count} file(s) kept so one copy of each duplicate set survives.\n" : "") + why, "RED RAM Storage Cleaner");
            Status.Text = "Nothing was changed.";
            return;
        }
        var requireAck = manual && allowed.Any(x => x.Safety != FileSafety.SafeToClean);
        var dlg = new CleanupConfirmWindow(allowed, blocked, keptBack, requireAck) { Owner = this };
        if (dlg.ShowDialog() != true) { Status.Text = "Cancelled — nothing was changed."; return; }

        deleting = true; SetScanning(false);
        deleteCts?.Dispose(); deleteCts = new CancellationTokenSource();
        StopButton.IsEnabled = true;
        DeleteProgressPanel.Visibility = Visibility.Visible; DeleteProgress.Value = 0;
        var progress = new Progress<(int Done, int Total, string Current)>(p =>
        {
            DeleteProgress.Value = p.Total == 0 ? 100 : p.Done * 100d / p.Total;
            DeleteProgressText.Text = $"Recycling {p.Done:N0}/{p.Total:N0}  {Path.GetFileName(p.Current)}";
        });
        try
        {
            var outcome = await StorageScanner.RecycleAsync(allowed, progress, deleteCts.Token);
            SaveCleanupReport(outcome, label, blocked);
            RemoveFromResult(outcome.Moved);
            var skipped = outcome.Skipped.Count + blocked.Count;
            Status.Text = $"DONE · moved {outcome.Moved.Count:N0} {Plural(outcome.Moved.Count, "file", "files")} ({FormatBytes(outcome.MovedBytes)}) to the Recycle Bin" + (skipped > 0 ? $" · {skipped:N0} skipped" : "") + (outcome.Cancelled ? " · stopped early" : "") + ". Use Open Recycle Bin to restore anything.";
            if (outcome.Skipped.Count > 0)
            {
                var lines = string.Join("\n", outcome.Skipped.Take(12).Select(x => $"• {Path.GetFileName(x.Item.Path)} — {x.Reason}"));
                MessageBox.Show(this, $"Moved {outcome.Moved.Count:N0} file(s) to the Recycle Bin.\n\n{outcome.Skipped.Count:N0} file(s) were left in place:\n{lines}" + (outcome.Skipped.Count > 12 ? "\n…" : ""), "RED RAM Storage Cleaner", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "RED RAM Storage Cleaner"); }
        finally
        {
            deleting = false;
            DeleteProgressPanel.Visibility = Visibility.Collapsed;
            SetScanning(false);
        }
    }

    void RemoveFromResult(IReadOnlyList<FileItem> moved)
    {
        if (result is null || moved.Count == 0) return;
        var gone = new HashSet<string>(moved.Select(m => m.Path), StringComparer.OrdinalIgnoreCase);
        var files = result.Files.Where(f => !gone.Contains(f.Path)).ToList();
        // Re-evaluate duplicate sets: a set with one file left is no longer a duplicate; a set without a keeper gets one.
        foreach (var g in files.Select((f, i) => (f, i)).Where(x => x.f.DuplicateGroup != null).GroupBy(x => x.f.DuplicateGroup!).ToList())
        {
            var members = g.ToList();
            if (members.Count < 2) { foreach (var m in members) files[m.i] = m.f with { DuplicateGroup = null, DuplicateKeeper = false }; continue; }
            if (!members.Any(m => m.f.DuplicateKeeper)) { var k = members.OrderByDescending(m => m.f.ModifiedAt).First(); files[k.i] = k.f with { DuplicateKeeper = true }; }
        }
        LoadResult(new StorageScanResult(result.Root, files, files.Sum(f => f.Bytes), result.Skipped), keepTicks: true);
    }

    static void SaveCleanupReport(RecycleOutcome outcome, string label, List<(FileItem Item, string Reason)> blocked)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RED RAM", "History");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"cleanup-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            var lines = new List<string>
            {
                $"RED RAM cleanup report — {DateTime.Now:g}", $"Mode: {label}",
                $"Moved to Recycle Bin: {outcome.Moved.Count}", $"Bytes moved to Recycle Bin: {outcome.MovedBytes}",
                $"Skipped: {outcome.Skipped.Count + blocked.Count}", outcome.Cancelled ? "Stopped early by user" : "", "",
                "Moved (restore from the Windows Recycle Bin if needed):"
            };
            lines.AddRange(outcome.Moved.Select(m => "  " + m.Path));
            lines.Add(""); lines.Add("Skipped:");
            lines.AddRange(outcome.Skipped.Concat(blocked).Select(x => $"  {x.Item.Path}  —  {x.Reason}"));
            File.WriteAllLines(path, lines);
        }
        catch { }
    }

    protected override void OnClosed(EventArgs e)
    {
        pauseGate.Set(); cts?.Cancel(); deleteCts?.Cancel();
        summaryTimer.Stop(); searchTimer.Stop();
        base.OnClosed(e);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHELLEXECUTEINFO { public int cbSize; public uint fMask; public IntPtr hwnd; [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb; [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile; [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters; [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory; public int nShow; public IntPtr hInstApp; public IntPtr lpIDList; [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass; public IntPtr hkeyClass; public uint dwHotKey; public IntPtr hIcon; public IntPtr hProcess; }
}
