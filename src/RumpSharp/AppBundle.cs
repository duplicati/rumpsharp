using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text;
using RumpSharp.Interop;

namespace RumpSharp;

/// <summary>
/// Gives a plain .NET console application the <c>.app</c> bundle identity that macOS requires before
/// it will display notifications.
/// </summary>
/// <remarks>
/// <para>
/// macOS derives notification identity from the location of the running executable, so a bare
/// console binary cannot post notifications. <see cref="PrepareIfNeeded"/> builds a small
/// <c>.app</c> bundle whose executable is RumpSharp's prebuilt notification helper - the host
/// application itself is never copied - and <see cref="NotificationCenter"/> then drives that helper
/// over a pipe.
/// </para>
/// <para>
/// If your application already ships as a real <c>.app</c> bundle, <see cref="PrepareIfNeeded"/> does
/// nothing and <see cref="NotificationCenter"/> talks to macOS in-process instead.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public static class AppBundle
{
    /// <summary>
    /// File name of the notification helper inside the bundle, and its <c>CFBundleExecutable</c>.
    /// </summary>
    internal const string HelperName = "rumpsharp-helper";

    /// <summary>Manifest resource holding the prebuilt universal helper binary, gzipped.</summary>
    private const string HelperResource = "RumpSharp." + HelperName + ".gz";

    /// <summary>
    /// Roughly what the helper decompresses to, so the buffer it is read into does not have to grow.
    /// Being wrong only costs a reallocation.
    /// </summary>
    private const int HelperSizeHint = 336 * 1024;

    /// <summary>
    /// What the generated bundle claims as its <c>LSMinimumSystemVersion</c>. Keep this and the
    /// requirement documented in the README in step.
    /// </summary>
    private const string MinimumSystemVersion = "11.0";

    /// <summary>The bundle that hosts the helper for this process, built at most once.</summary>
    private static string? _prepared;

    /// <summary>
    /// Serializes bundle creation. Held by <see cref="Create"/> itself, and by <see cref="Prepare"/>
    /// across its "is there one already?" check as well - <see cref="Lock"/> is reentrant, so the
    /// nesting is fine.
    /// </summary>
    private static readonly Lock PrepareGate = new();

    /// <summary>Path of the bundle the current process is running from, if any.</summary>
    /// <remarks>
    /// The pool matters: <c>-[NSBundle bundlePath]</c> answers an autoreleased string, and a console
    /// application's main thread has no pool of its own, so without one every read leaks the string
    /// (and, with <c>OBJC_DEBUG_MISSING_POOLS</c> set, complains about it). <c>FromNSString</c> copies
    /// into managed memory, so the result outlives the drain.
    /// </remarks>
    public static string? CurrentBundlePath
    {
        get
        {
            using var pool = ObjC.Pool();
            var path = ObjC.FromNSString(ObjC.Send(ObjC.Send(Cls.NSBundle, Sel.MainBundle), Sel.BundlePath));
            return path?.EndsWith(".app", StringComparison.OrdinalIgnoreCase) == true ? path : null;
        }
    }

    /// <summary>Bundle identifier of the current process, or <see langword="null"/> when unbundled.</summary>
    public static string? CurrentBundleIdentifier
    {
        get
        {
            using var pool = ObjC.Pool();
            return ObjC.FromNSString(ObjC.Send(ObjC.Send(Cls.NSBundle, Sel.MainBundle), Sel.BundleIdentifier));
        }
    }

    /// <summary>
    /// Whether this process has a usable bundle identity, i.e. whether it can post notifications
    /// itself rather than through the helper.
    /// </summary>
    public static bool IsBundled => CurrentBundlePath is not null && !string.IsNullOrEmpty(CurrentBundleIdentifier);

    /// <summary>
    /// The <c>.app</c> bundle this process will post notifications from by default, once there is one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For an application that ships as a bundle this is <see cref="CurrentBundlePath"/>. For a console
    /// application it is the generated bundle that hosts the helper, which is <see langword="null"/>
    /// until <see cref="PrepareIfNeeded"/>, <see cref="Create"/> or the first
    /// <see cref="NotificationCenter"/> has built it.
    /// </para>
    /// <para>
    /// "By default" is the caveat: a <see cref="NotificationCenter"/> that was told to use
    /// <see cref="NotificationTransport.Helper"/> posts from the helper's bundle even though this
    /// process has one of its own, and this property does not know that. Ask
    /// <see cref="NotificationCenter.BundlePath"/> when an instance exists - that answer is always
    /// right.
    /// </para>
    /// </remarks>
    public static string? NotificationBundlePath => CurrentBundlePath ?? Volatile.Read(ref _prepared);

    /// <summary>
    /// Prepares the <c>.app</c> bundle that notifications from this process will be attributed to, if
    /// this process needs one at all.
    /// </summary>
    /// <param name="options">Bundle settings; defaults are derived from the entry assembly.</param>
    /// <returns>
    /// <para>
    /// <see langword="true"/> if a bundle was prepared, which also tells you that notifications from
    /// this process will be posted by the helper inside it rather than by the process itself.
    /// </para>
    /// <para>
    /// <see langword="false"/> if nothing was needed: the process already has a bundle identity of its
    /// own and will post notifications in-process, or it is not running on macOS.
    /// </para>
    /// </returns>
    /// <remarks>
    /// <para>
    /// The answer describes this process, not the state of the disk: it is <see langword="true"/> on
    /// every call for an unbundled process, whether the bundle had to be written or was already there
    /// and up to date. Use <see cref="NotificationBundlePath"/> to find out where it is.
    /// </para>
    /// <para>
    /// This does <em>not</em> relaunch the process. Your <c>Main</c> keeps running in the process the
    /// user started, with its console, working directory and exit code intact.
    /// </para>
    /// <para>
    /// Calling it is optional but recommended: it front-loads bundle creation and, more importantly,
    /// applies the <see cref="AppBundleOptions"/> the user will see - <see cref="AppBundleOptions.Name"/>
    /// and <see cref="AppBundleOptions.IconPath"/> - instead of leaving <see cref="NotificationCenter"/>
    /// to build a bundle from the defaults on first use.
    /// </para>
    /// </remarks>
    public static bool PrepareIfNeeded(AppBundleOptions? options = null)
    {
        if (!OperatingSystem.IsMacOS() || IsBundled)
        {
            return false;
        }

        Prepare(options ?? new AppBundleOptions());
        return true;
    }

    /// <summary>
    /// The bundle the helper runs from, creating it with the default settings if nothing has prepared
    /// one yet.
    /// </summary>
    internal static string PrepareDefault() => Prepare(null);

    /// <summary>Creates or refreshes the helper's bundle, at most once per process.</summary>
    /// <param name="options">Bundle settings, or <see langword="null"/> to accept whatever exists.</param>
    private static string Prepare(AppBundleOptions? options)
    {
        lock (PrepareGate)
        {
            // Create records the result itself, so an explicit Create by the caller counts too.
            return _prepared is { } existing && options is null
                ? existing
                : Create(options ?? new AppBundleOptions());
        }
    }

    /// <summary>
    /// Creates or refreshes the <c>.app</c> bundle that hosts the notification helper.
    /// </summary>
    /// <param name="options">Bundle settings.</param>
    /// <returns>The full path of the <c>.app</c> bundle.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="options"/> has an empty or unusable <see cref="AppBundleOptions.Name"/>,
    /// <see cref="AppBundleOptions.BundleIdentifier"/> or <see cref="AppBundleOptions.Version"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The bundle contains the helper and the icon, nothing else - a few hundred kilobytes, whatever
    /// the size of the application using it.
    /// </para>
    /// <para>
    /// Calls from several threads are serialized, because writing a bundle is not something two of
    /// them can do to the same directory at once: the helper is unlinked and written again, and the
    /// loser of that race would be left with a truncated executable and an invalid signature.
    /// </para>
    /// </remarks>
    public static string Create(AppBundleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);

        lock (PrepareGate)
        {
            return CreateCore(options);
        }
    }

    /// <summary>Writes the bundle. Callers hold <see cref="PrepareGate"/>.</summary>
    private static string CreateCore(AppBundleOptions options)
    {
        var bundlePath = Path.Combine(options.Location, options.Name + ".app");
        var contents = Path.Combine(bundlePath, "Contents");
        var macOsDir = Path.Combine(contents, "MacOS");
        var resources = Path.Combine(contents, "Resources");

        Directory.CreateDirectory(macOsDir);
        Directory.CreateDirectory(resources);

        var changed = InjectHelper(macOsDir);
        var iconFile = CreateIcon(options.IconPath, resources, ref changed);
        changed |= WriteIfDifferent(
            Path.Combine(contents, "Info.plist"),
            BuildInfoPlist(options, HelperName, iconFile));

        if (changed && options.CodeSign)
        {
            // An ad-hoc signature gives LaunchServices the stable identity that the notification
            // database keys off. Failure is non-fatal, but notifications will most likely be refused.
            // "--deep" costs nothing now that the only Mach-O in here is the helper.
            Run(
                "codesign",
                ["--force", "--deep", "--sign", "-", "--timestamp=none", bundlePath],
                TimeSpan.FromSeconds(60));
        }

        // This is now the bundle this process posts through. Recording it here rather than only in
        // PrepareIfNeeded matters: otherwise calling Create directly would build a bundle and then
        // NotificationCenter would quietly build and use a second one from the defaults.
        Volatile.Write(ref _prepared, bundlePath);
        return bundlePath;
    }

    /// <summary>
    /// Opens the System Settings pane where the user can grant or revoke notification permission for
    /// this application.
    /// </summary>
    public static void OpenNotificationSettings() =>
        Run("open", ["x-apple.systempreferences:com.apple.preference.notifications"], TimeSpan.FromSeconds(10));

    /// <summary>The helper executable inside <paramref name="bundlePath"/>.</summary>
    internal static string HelperPath(string bundlePath) =>
        Path.Combine(bundlePath, "Contents", "MacOS", HelperName);

    /// <summary>
    /// Rejects options that would produce a broken bundle - or, in the case of a name containing path
    /// separators, one written outside <see cref="AppBundleOptions.Location"/> entirely.
    /// </summary>
    private static void Validate(AppBundleOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Name))
        {
            throw new ArgumentException("AppBundleOptions.Name must not be empty.", nameof(options));
        }

        if (options.Name != Path.GetFileName(options.Name)
            || options.Name is "." or ".."
            || options.Name.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"AppBundleOptions.Name must be a plain folder name without path separators, but was '{options.Name}'.",
                nameof(options));
        }

        if (!IsValidBundleIdentifier(options.BundleIdentifier))
        {
            throw new ArgumentException(
                $"AppBundleOptions.BundleIdentifier must be a reverse-DNS name of ASCII letters, digits, "
                + $"hyphens and dots, such as 'com.example.mytool', but was '{options.BundleIdentifier}'.",
                nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.Version))
        {
            throw new ArgumentException("AppBundleOptions.Version must not be empty.", nameof(options));
        }
    }

    /// <summary>Whether a string is acceptable to LaunchServices as a <c>CFBundleIdentifier</c>.</summary>
    internal static bool IsValidBundleIdentifier(string value) =>
        !string.IsNullOrEmpty(value)
        && !value.StartsWith('.')
        && !value.EndsWith('.')
        && !value.Contains("..", StringComparison.Ordinal)
        && value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.');

    // ------------------------------------------------------------------ bundle contents

    /// <summary>
    /// Writes the embedded helper binary into <paramref name="macOsDir"/> and removes anything else
    /// that is in there.
    /// </summary>
    /// <param name="macOsDir">The bundle's <c>Contents/MacOS</c> directory.</param>
    /// <returns>
    /// <see langword="true"/> if anything on disk changed, which is what decides whether the bundle
    /// has to be signed again.
    /// </returns>
    /// <remarks>
    /// Rewriting an identical file would be worse than pointless: it invalidates the code signature
    /// and the LaunchServices registration that the notification permission is attached to, so the
    /// bytes are compared first. The cleanup matters for upgrades - bundles built by RumpSharp 1.0
    /// hold a full copy of the host application here, and leaving that behind would both waste the
    /// space this change is about and break <c>codesign --deep</c> on the nested Mach-O files.
    /// </remarks>
    internal static bool InjectHelper(string macOsDir)
    {
        var helper = Path.Combine(macOsDir, HelperName);
        var bytes = ReadHelperResource();
        var changed = false;

        if (!IsSameContent(helper, bytes))
        {
            // Unlink rather than overwrite: a running helper keeps the inode it was started from,
            // whereas writing into the file in place fails with ETXTBSY.
            TryDelete(helper);
            File.WriteAllBytes(helper, bytes);
            changed = true;
        }

        File.SetUnixFileMode(
            helper,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        foreach (var entry in Directory.EnumerateFileSystemEntries(macOsDir))
        {
            if (string.Equals(Path.GetFileName(entry), HelperName, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.Delete(entry);
                }

                changed = true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return changed;
    }

    /// <summary>Decompresses the embedded helper binary.</summary>
    /// <remarks>
    /// The resource is stored gzipped, which is why this cannot simply read
    /// <see cref="Stream.Length"/> bytes: that is the compressed size.
    /// </remarks>
    private static byte[] ReadHelperResource()
    {
        using var resource = typeof(AppBundle).Assembly.GetManifestResourceStream(HelperResource)
            ?? throw new InvalidOperationException(
                $"The '{HelperResource}' resource is missing from RumpSharp.dll, so the notification helper "
                + "cannot be installed. This assembly was not built from an intact source tree.");

        using var decompressed = new GZipStream(resource, CompressionMode.Decompress);
        using var buffer = new MemoryStream(HelperSizeHint);
        decompressed.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static bool IsSameContent(string path, byte[] expected)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != expected.Length)
        {
            return false;
        }

        try
        {
            return File.ReadAllBytes(path).AsSpan().SequenceEqual(expected);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string? CreateIcon(string? iconPath, string resources, ref bool changed)
    {
        if (string.IsNullOrEmpty(iconPath))
        {
            return null;
        }

        if (!File.Exists(iconPath))
        {
            throw new FileNotFoundException($"Icon file not found: {iconPath}", iconPath);
        }

        const string iconName = "AppIcon.icns";
        var target = Path.Combine(resources, iconName);

        var source = new FileInfo(iconPath);
        var existing = new FileInfo(target);
        if (existing.Exists && existing.LastWriteTimeUtc > source.LastWriteTimeUtc)
        {
            return iconName;
        }

        if (string.Equals(source.Extension, ".icns", StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(iconPath, target, overwrite: true);
            changed = true;
            return iconName;
        }

        // Build a proper multi-resolution iconset from the source image using the tools that ship
        // with macOS, falling back to a straight conversion if iconutil is unhappy.
        var iconset = Path.Combine(Path.GetTempPath(), $"rumpsharp-{Guid.NewGuid():N}.iconset");
        Directory.CreateDirectory(iconset);
        try
        {
            foreach (var size in (int[])[16, 32, 128, 256, 512])
            {
                Resize(iconPath, Path.Combine(iconset, $"icon_{size}x{size}.png"), size);
                Resize(iconPath, Path.Combine(iconset, $"icon_{size}x{size}@2x.png"), size * 2);
            }

            if (!Run("iconutil", ["-c", "icns", iconset, "-o", target], TimeSpan.FromSeconds(60)))
            {
                Run("sips", ["-s", "format", "icns", iconPath, "--out", target], TimeSpan.FromSeconds(60));
            }
        }
        finally
        {
            try
            {
                Directory.Delete(iconset, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        changed = true;
        return File.Exists(target) ? iconName : null;

        static void Resize(string from, string to, int size) =>
            Run("sips", ["-z", size.ToString(), size.ToString(), from, "--out", to], TimeSpan.FromSeconds(30));
    }

    internal static string BuildInfoPlist(AppBundleOptions options, string executableName, string? iconFile)
    {
        var plist = new StringBuilder();
        plist.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        plist.AppendLine("""<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">""");
        plist.AppendLine("""<plist version="1.0">""");
        plist.AppendLine("<dict>");
        Add("CFBundleName", options.Name);
        Add("CFBundleDisplayName", options.Name);
        Add("CFBundleIdentifier", options.BundleIdentifier);
        Add("CFBundleExecutable", executableName);
        Add("CFBundleVersion", options.Version);
        Add("CFBundleShortVersionString", options.Version);
        Add("CFBundlePackageType", "APPL");
        Add("CFBundleInfoDictionaryVersion", "6.0");
        Add("NSPrincipalClass", "NSApplication");
        Add("LSMinimumSystemVersion", MinimumSystemVersion);
        if (iconFile is not null)
        {
            Add("CFBundleIconFile", iconFile);
        }

        // The helper reads this back to decide between a regular and an accessory application.
        plist.AppendLine("  <key>LSUIElement</key>");
        plist.AppendLine(options.ShowInDock ? "  <false/>" : "  <true/>");
        plist.AppendLine("</dict>");
        plist.AppendLine("</plist>");
        return plist.ToString();

        void Add(string key, string value)
        {
            plist.AppendLine($"  <key>{Escape(key)}</key>");
            plist.AppendLine($"  <string>{Escape(value)}</string>");
        }

        static string Escape(string value) => value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }

    internal static bool WriteIfDifferent(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content)
        {
            return false;
        }

        File.WriteAllText(path, content);
        return true;
    }

    private static bool Run(string fileName, string[] arguments, TimeSpan timeout)
    {
        try
        {
            var info = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }

            return process.WaitForExit(timeout) && process.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
