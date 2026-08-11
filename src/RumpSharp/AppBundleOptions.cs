using System.Reflection;

namespace RumpSharp;

/// <summary>
/// Describes the <c>.app</c> bundle that RumpSharp creates for console applications.
/// </summary>
/// <remarks>
/// macOS only lets an application post notifications if it has a bundle identity that
/// LaunchServices knows about, which is why a plain console executable cannot show notifications on
/// its own. This is the same constraint that forces rumps users to build with <c>py2app</c>;
/// RumpSharp just automates it.
/// </remarks>
public sealed class AppBundleOptions
{
    /// <summary>
    /// Display name of the application, also used for the bundle folder name. Defaults to the entry
    /// assembly name. This is the name the user sees in System Settings &gt; Notifications.
    /// </summary>
    public string Name { get; set; } = DefaultName();

    /// <summary>
    /// Reverse-DNS bundle identifier. Notification permission is granted per identifier, so keep
    /// this stable across releases: changing it makes macOS treat your app as brand new and prompt
    /// the user again.
    /// </summary>
    public string BundleIdentifier { get; set; } = $"dev.rumpsharp.{Sanitize(DefaultName())}";

    /// <summary>Bundle version string. Defaults to the entry assembly's informational version.</summary>
    public string Version { get; set; } = DefaultVersion();

    /// <summary>
    /// Path to the application icon - either an <c>.icns</c> file or a square <c>.png</c> that
    /// RumpSharp converts using the <c>sips</c> and <c>iconutil</c> tools shipped with macOS. This
    /// is the small icon macOS draws in the corner of every notification banner.
    /// </summary>
    public string? IconPath { get; set; }

    /// <summary>
    /// Directory that holds the generated bundle. Defaults to
    /// <c>~/Library/Application Support/RumpSharp</c>, a stable location that survives rebuilds so
    /// the notification permission is only requested once.
    /// </summary>
    /// <remarks>
    /// Keep it out of temporary storage. macOS refuses notifications outright for a bundle inside the
    /// per-user temporary directory (<c>/var/folders/...</c>, i.e. <see cref="Path.GetTempPath"/>),
    /// with no prompt and no way to grant permission afterwards.
    /// </remarks>
    public string Location { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RumpSharp");

    /// <summary>
    /// Whether the generated application appears in the Dock and app switcher. Console apps normally
    /// want <see langword="false"/> (an <c>LSUIElement</c> background application), which is the
    /// default.
    /// </summary>
    /// <remarks>
    /// This describes the notification helper, which is the process macOS sees as the application.
    /// Your own process is unaffected either way.
    /// </remarks>
    public bool ShowInDock { get; set; }

    /// <summary>
    /// Whether to ad-hoc code sign the generated bundle with <c>codesign</c>. Signing keeps the
    /// bundle's identity stable for the notification database; failures are ignored.
    /// </summary>
    public bool CodeSign { get; set; } = true;

    private static string DefaultName() =>
        Assembly.GetEntryAssembly()?.GetName().Name
        ?? Path.GetFileNameWithoutExtension(Environment.ProcessPath)
        ?? "RumpSharpApp";

    private static string DefaultVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";

    private static string Sanitize(string value) =>
        string.Concat(value.Where(c => char.IsLetterOrDigit(c) || c is '-' or '.')).Trim('.') is { Length: > 0 } s
            ? s
            : "app";
}
