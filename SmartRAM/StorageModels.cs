using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace SmartRAM;

/// <summary>Neon Core colours used from code (kept in sync with Themes/RedRamTheme.xaml).</summary>
public static class Palette
{
    static SolidColorBrush B(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
    public static readonly SolidColorBrush Red = B("#FF2D55"), RedSoft = B("#26FF2D55");
    public static readonly SolidColorBrush Cyan = B("#00E5FF"), CyanSoft = B("#2200E5FF");
    public static readonly SolidColorBrush Green = B("#2BF5A0"), GreenSoft = B("#222BF5A0");
    public static readonly SolidColorBrush Amber = B("#FFB547"), AmberSoft = B("#22FFB547");
    public static readonly SolidColorBrush Violet = B("#9D7BFF");
    public static readonly SolidColorBrush Text = B("#ECEFF7"), Muted = B("#8C94A8"), Dim = B("#3A4258"), DimSoft = B("#1A3A4258");
    public static readonly SolidColorBrush Videos = B("#FF5E7E"), Images = B("#4FC3F7"), Audio = B("#B388FF"), Documents = B("#FFD166"), Archives = B("#FF9F43"), Other = B("#7D8AA6");
    public static Brush ForKey(string key) => key switch
    {
        "all" => Text, "junk" => Green, "large" => Red, "dupes" => Cyan, "similar" => Violet, "downloads" => Amber, "old" => Muted, "locked" => Muted,
        "Videos" => Videos, "Images" => Images, "Audio" => Audio, "Documents" => Documents, "Archives" => Archives, _ => Other
    };
}

public sealed class CategoryCard
{
    public CategoryCard(string key, string glyph, string title, string hint, Brush brush, long count, long bytes, double barWidth)
    { Key = key; Glyph = glyph; Title = title; Hint = hint; Brush = brush; Count = count; Bytes = bytes; BarWidth = barWidth; }
    public string Key { get; }
    public string Glyph { get; }
    public string Title { get; }
    public string Hint { get; }
    public Brush Brush { get; }
    public long Count { get; }
    public long Bytes { get; }
    public double BarWidth { get; }
    public string SizeText => Count == 0 ? "—" : StorageWindow.FormatBytes(Bytes);
    public string CountText => Count == 0 ? "none" : $"{Count:N0} {(Count == 1 ? "file" : "files")}";
}

/// <summary>One row in the Storage Cleaner list. Rows live for the whole scan so ticks survive switching categories.</summary>
public sealed class FileRow : INotifyPropertyChanged
{
    readonly Action<FileRow, bool> onTick;
    bool selected; double barWidth; bool? oldDownload;
    public FileRow(FileItem item, Action<FileRow, bool> onTick) { Item = item; this.onTick = onTick; CanSelect = StorageScanner.IsSelectable(item); }
    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public FileItem Item { get; }
    public bool CanSelect { get; }
    public bool Selected
    {
        get => selected;
        set { if (value && !CanSelect) value = false; if (selected == value) return; selected = value; Changed(nameof(Selected)); onTick(this, value); }
    }
    internal void SetSelectedSilently(bool on) { selected = on && CanSelect; }
    public double BarWidth { get => barWidth; set { if (Math.Abs(barWidth - value) < 0.01) return; barWidth = value; Changed(nameof(BarWidth)); } }

    public string Name => Path.GetFileName(Item.Path);
    public string Folder => Path.GetDirectoryName(Item.Path) ?? "";
    public long Bytes => Item.Bytes;
    public string Size => StorageWindow.FormatBytes(Item.Bytes);
    public string Type => string.IsNullOrWhiteSpace(Item.Extension) ? "FILE" : Item.Extension;
    public string Owner => Item.Owner ?? Item.Ownership ?? "";
    public DateTime ModifiedAt => Item.ModifiedAt;
    public int SafetyRank => Item.CloudOnly ? 3 : Item.Safety switch { FileSafety.SafeToClean => 0, FileSafety.Review => 1, _ => 2 };
    public string SafetyLabel => Item.CloudOnly ? "☁ Cloud" : Item.Safety switch
    {
        FileSafety.SafeToClean => "✓ Junk",
        FileSafety.Review => Item.DuplicateKeeper ? "★ Keep" : "● Yours",
        _ => "🔒 Locked"
    };
    public Brush SafetyBrush => Item.CloudOnly ? Palette.Muted : Item.Safety switch { FileSafety.SafeToClean => Palette.Green, FileSafety.Review => Item.DuplicateKeeper ? Palette.Cyan : Palette.Amber, _ => Palette.Muted };
    public Brush SafetySoftBrush => Item.CloudOnly ? Palette.DimSoft : Item.Safety switch { FileSafety.SafeToClean => Palette.GreenSoft, FileSafety.Review => Item.DuplicateKeeper ? Palette.CyanSoft : Palette.AmberSoft, _ => Palette.DimSoft };
    public Brush CategoryBrush => Palette.ForKey(Item.Category);
    public string Status => Item.DuplicateKeeper ? "RED RAM suggests keeping this copy"
        : Item.DuplicateGroup != null ? "Identical copy of another file"
        : Item.SimilarGroup != null ? $"Similar photo ({Item.Similarity}% match)"
        : Item.Reason;
    public string Group => Item.DuplicateGroup != null ? $"DUP·{Item.DuplicateGroup[..Math.Min(6, Item.DuplicateGroup.Length)]}" : Item.SimilarGroup ?? "";
    public string CheckTip => CanSelect ? "Tick to send this file to the Recycle Bin (Shift+click ticks a range)" : $"Locked — {Item.Reason}";
    public ImageSource? Icon => FileIconCache.Get(Item.Extension, large: false);
    public bool IsOldDownload => oldDownload ??= CanSelect && Item.ModifiedAt < DateTime.Now.AddDays(-90) && StorageScanner.IsInDownloads(Item.Path);
}

/// <summary>Windows shell icons per file extension (no disk access, cached, UI thread only).</summary>
public static class FileIconCache
{
    static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    public static ImageSource? Get(string extension, bool large)
    {
        var ext = string.IsNullOrWhiteSpace(extension) ? "bin" : extension.ToLowerInvariant();
        var key = (large ? "L:" : "S:") + ext;
        if (Cache.TryGetValue(key, out var img)) return img;
        img = Load("file." + ext, large);
        Cache[key] = img;
        return img;
    }
    static ImageSource? Load(string fakeName, bool large)
    {
        try
        {
            var info = new SHFILEINFO();
            uint flags = SHGFI_ICON | SHGFI_USEFILEATTRIBUTES | (large ? SHGFI_LARGEICON : SHGFI_SMALLICON);
            var ok = SHGetFileInfo(fakeName, FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
            if (ok == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { DestroyIcon(info.hIcon); }
        }
        catch { return null; }
    }
    const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0, SHGFI_SMALLICON = 0x1, SHGFI_USEFILEATTRIBUTES = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO { public IntPtr hIcon; public int iIcon; public uint dwAttributes; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);
}
