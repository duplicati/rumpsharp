using Avalonia;
using Avalonia.Controls;
using RumpSharp;
using RumpSharp.Samples;

namespace RumpSharp.TrayIconSample;

internal static class Program
{
    /// <summary>Entry point.</summary>
    /// <param name="args">Command line arguments.</param>
    /// <returns>The process exit code.</returns>
    public static int Main(string[] args)
    {
        // The tray icon artwork doubles as the application icon shown on every notification banner.
        // Keep it outside the build output so regenerating it does not invalidate the bundle.
        var assets = Path.Combine(Path.GetTempPath(), "rumpsharp-tray-sample");
        App.TrayIconPath = IconFactory.CreateTrayIcon(Path.Combine(assets, "tray-icon.png"));
        var appIcon = IconFactory.CreateAppIcon(Path.Combine(assets, "app-icon.png"));

        // STEP 1: make sure there is a bundle for macOS to attribute the notifications to. Run this
        // sample from a real .app bundle (see package.sh) and it returns false - RumpSharp uses that
        // identity directly. Run it as a plain executable and it returns true, having built a bundle
        // for the helper to post from. LSUIElement (ShowInDock = false) is what keeps that out of the
        // Dock and the app switcher.
        var viaHelper = AppBundle.PrepareIfNeeded(new AppBundleOptions
        {
            Name = "RumpSharp Tray",
            BundleIdentifier = "dev.rumpsharp.trayicon",
            IconPath = appIcon,
            ShowInDock = false,
        });

        // "in-process: True" means this process has its own bundle identity and talks to macOS
        // directly; False means it posts through the helper in the bundle above. Use package.sh to
        // build this sample as a real .app and see the difference.
        Console.WriteLine($"Notifications come from {AppBundle.NotificationBundlePath}");
        Console.WriteLine($"  posting   : {(viaHelper ? "through the bundled helper" : "in-process")}");
        Console.WriteLine("Look for the icon in the menu bar. Use its menu to send notifications.");
        Console.WriteLine("Press Ctrl+C here, or choose Quit in the menu, to stop.");

        // STEP 2 - hand the process over to Avalonia. Avalonia runs the macOS run loop, which is
        // also what delivers notification callbacks, so RumpSharp never needs to pump it here.
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    /// <summary>Avalonia configuration, also used by the visual designer.</summary>
    /// <returns>The configured app builder.</returns>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
