using Xunit;

namespace RumpSharp.Tests;

/// <summary>Covers the generated <c>Info.plist</c>, which is what gives the process its identity.</summary>
public sealed class InfoPlistTests
{
    private static string Build(AppBundleOptions options, string executableName = "MyTool", string? iconFile = null) =>
        AppBundle.BuildInfoPlist(options, executableName, iconFile).ReplaceLineEndings("\n");

    /// <summary>
    /// The bundle's executable is the helper, never the host application - that is what makes the
    /// bundle small and what lets the host keep running as itself.
    /// </summary>
    [Fact]
    public void TheBundleExecutableIsTheHelper()
    {
        using var directory = new TempDirectory();
        var bundle = AppBundle.Create(new AppBundleOptions
        {
            Name = "Tool",
            BundleIdentifier = "com.example.tool",
            Location = directory.Path,
            CodeSign = false,
        });

        var plist = File.ReadAllText(Path.Combine(bundle, "Contents", "Info.plist")).ReplaceLineEndings("\n");

        Assert.Contains(
            "<key>CFBundleExecutable</key>\n  <string>rumpsharp-helper</string>",
            plist,
            StringComparison.Ordinal);
        Assert.Equal(
            ["rumpsharp-helper"],
            Directory.GetFileSystemEntries(Path.Combine(bundle, "Contents", "MacOS")).Select(Path.GetFileName));
    }

    [Fact]
    public void ContainsTheKeysLaunchServicesNeeds()
    {
        var plist = Build(new AppBundleOptions
        {
            Name = "My Tool",
            BundleIdentifier = "com.example.mytool",
            Version = "2.3.4",
        });

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", plist, StringComparison.Ordinal);
        Assert.Contains("<key>CFBundleIdentifier</key>\n  <string>com.example.mytool</string>", plist, StringComparison.Ordinal);
        Assert.Contains("<key>CFBundleName</key>\n  <string>My Tool</string>", plist, StringComparison.Ordinal);
        Assert.Contains("<key>CFBundleDisplayName</key>\n  <string>My Tool</string>", plist, StringComparison.Ordinal);
        Assert.Contains("<key>CFBundleExecutable</key>\n  <string>MyTool</string>", plist, StringComparison.Ordinal);
        Assert.Contains("<key>CFBundleVersion</key>\n  <string>2.3.4</string>", plist, StringComparison.Ordinal);
        Assert.Contains("<key>CFBundleShortVersionString</key>\n  <string>2.3.4</string>", plist, StringComparison.Ordinal);
        Assert.Contains("<key>CFBundlePackageType</key>\n  <string>APPL</string>", plist, StringComparison.Ordinal);
        Assert.Contains("<key>NSPrincipalClass</key>\n  <string>NSApplication</string>", plist, StringComparison.Ordinal);
        Assert.EndsWith("</plist>\n", plist, StringComparison.Ordinal);
    }

    /// <summary>Keeps the plist in step with the requirement documented in the README.</summary>
    [Fact]
    public void DeclaresTheDocumentedMinimumSystemVersion()
    {
        var plist = Build(new AppBundleOptions { Name = "Tool", BundleIdentifier = "com.example.tool" });

        Assert.Contains("<key>LSMinimumSystemVersion</key>\n  <string>11.0</string>", plist, StringComparison.Ordinal);
    }

    [Fact]
    public void EscapesXmlSpecialCharacters()
    {
        var plist = Build(new AppBundleOptions
        {
            Name = "Tom & Jerry <best>",
            BundleIdentifier = "com.example.tj",
        });

        Assert.Contains("<string>Tom &amp; Jerry &lt;best&gt;</string>", plist, StringComparison.Ordinal);
        Assert.DoesNotContain("<string>Tom & Jerry", plist, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "<false/>")]
    [InlineData(false, "<true/>")]
    public void LsuiElementIsTheInverseOfShowInDock(bool showInDock, string expected)
    {
        var plist = Build(new AppBundleOptions
        {
            Name = "Tool",
            BundleIdentifier = "com.example.tool",
            ShowInDock = showInDock,
        });

        Assert.Contains($"<key>LSUIElement</key>\n  {expected}", plist, StringComparison.Ordinal);
    }

    [Fact]
    public void IconKeyIsOnlyWrittenWhenThereIsAnIcon()
    {
        var options = new AppBundleOptions { Name = "Tool", BundleIdentifier = "com.example.tool" };

        Assert.DoesNotContain("CFBundleIconFile", Build(options), StringComparison.Ordinal);
        Assert.Contains(
            "<key>CFBundleIconFile</key>\n  <string>AppIcon.icns</string>",
            Build(options, iconFile: "AppIcon.icns"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void WriteIfDifferentOnlyTouchesTheFileWhenTheContentChanged()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("Info.plist");

        Assert.True(AppBundle.WriteIfDifferent(path, "first"));
        Assert.False(AppBundle.WriteIfDifferent(path, "first"));
        Assert.True(AppBundle.WriteIfDifferent(path, "second"));
        Assert.Equal("second", File.ReadAllText(path));
    }
}
