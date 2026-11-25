using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
namespace SmartRAM;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Give every RED RAM window a dark Windows title bar so it matches the Neon Core theme.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => { if (s is Window w) ThemeHelper.UseDarkTitleBar(w); }));
        base.OnStartup(e);
    }
}

public static class ThemeHelper
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void UseDarkTitleBar(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int on = 1;
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 20H1+ / 11); 19 on older Windows 10 builds.
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
            // 35 = DWMWA_CAPTION_COLOR (Windows 11). Near-black caption; ignored on Windows 10.
            int caption = 0x000A0605; // COLORREF 0x00BBGGRR -> #05060A
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
        }
        catch { /* purely cosmetic */ }
    }
}
