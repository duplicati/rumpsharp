using System.Reflection;
using Xunit;

namespace RumpSharp.Tests;

/// <summary>
/// Covers writing the embedded helper into a bundle. Rewriting a byte-identical file would invalidate
/// the code signature and the LaunchServices registration the notification permission hangs off, so
/// "no change" has to mean "no write".
/// </summary>
public sealed class InjectHelperTests
{
    [Fact]
    public void WritesTheEmbeddedHelper()
    {
        using var directory = new TempDirectory();

        Assert.True(AppBundle.InjectHelper(directory.Path));

        var helper = directory.Combine("rumpsharp-helper");
        Assert.True(File.Exists(helper));
        Assert.True(new FileInfo(helper).Length > 0);
    }

    /// <summary>The bundle is useless if macOS cannot execute what is in it.</summary>
    [Fact]
    public void MakesTheHelperExecutable()
    {
        using var directory = new TempDirectory();
        AppBundle.InjectHelper(directory.Path);

        var mode = File.GetUnixFileMode(directory.Combine("rumpsharp-helper"));

        Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
        Assert.True(mode.HasFlag(UnixFileMode.GroupExecute));
        Assert.True(mode.HasFlag(UnixFileMode.OtherExecute));
    }

    /// <summary>The helper is a Mach-O universal binary, so it runs on Apple silicon and on Intel.</summary>
    [Fact]
    public void TheEmbeddedHelperIsAUniversalMachO()
    {
        using var directory = new TempDirectory();
        AppBundle.InjectHelper(directory.Path);

        var magic = new byte[4];
        using (var stream = File.OpenRead(directory.Combine("rumpsharp-helper")))
        {
            stream.ReadExactly(magic);
        }

        // FAT_CIGAM: a universal binary's header, big-endian on disk.
        Assert.Equal([0xCA, 0xFE, 0xBA, 0xBE], magic);
    }

    /// <summary>
    /// The helper is embedded compressed, so that an application which never needs it - one that
    /// already ships as a .app - carries a quarter of the weight. Embedding it raw would still work,
    /// silently, which is why the saving is asserted rather than assumed.
    /// </summary>
    [Fact]
    public void TheEmbeddedHelperIsStoredCompressed()
    {
        var assembly = typeof(AppBundle).Assembly;
        var name = Assert.Single(assembly.GetManifestResourceNames(), n => n.Contains("rumpsharp-helper", StringComparison.Ordinal));

        using var resource = assembly.GetManifestResourceStream(name);
        Assert.NotNull(resource);

        using var directory = new TempDirectory();
        AppBundle.InjectHelper(directory.Path);
        var written = new FileInfo(directory.Combine("rumpsharp-helper")).Length;

        Assert.EndsWith(".gz", name, StringComparison.Ordinal);
        Assert.True(
            resource.Length < written / 2,
            $"expected the embedded resource to be less than half the helper's size, but it was "
            + $"{resource.Length} bytes against {written}");
    }

    [Fact]
    public void ReportsNoChangeAndDoesNotRewriteWhenTheBytesAreIdentical()
    {
        using var directory = new TempDirectory();
        var helper = directory.Combine("rumpsharp-helper");

        Assert.True(AppBundle.InjectHelper(directory.Path));
        var first = File.GetLastWriteTimeUtc(helper);

        Assert.False(AppBundle.InjectHelper(directory.Path));
        Assert.Equal(first, File.GetLastWriteTimeUtc(helper));
    }

    [Fact]
    public void ReplacesAHelperWhoseBytesDiffer()
    {
        using var directory = new TempDirectory();
        var helper = directory.Combine("rumpsharp-helper");

        AppBundle.InjectHelper(directory.Path);
        var expected = File.ReadAllBytes(helper);

        File.WriteAllText(helper, "an older helper");
        Assert.True(AppBundle.InjectHelper(directory.Path));
        Assert.Equal(expected, File.ReadAllBytes(helper));
    }

    /// <summary>
    /// Bundles built by earlier versions hold a full copy of the host application here. Leaving that
    /// behind would waste the space this whole mechanism exists to save, and its nested Mach-O files
    /// would break <c>codesign --deep</c>.
    /// </summary>
    [Fact]
    public void RemovesLeftoversFromAnOlderBundle()
    {
        using var directory = new TempDirectory();
        directory.Write("MyTool", "the old apphost");
        directory.Write("MyTool.dll", "managed code");
        directory.Write("MyTool.pdb", "symbols");
        directory.Write("runtimes/osx-arm64/native/libSkiaSharp.dylib", "a nested Mach-O");

        Assert.True(AppBundle.InjectHelper(directory.Path));

        Assert.Equal(["rumpsharp-helper"], Directory.GetFileSystemEntries(directory.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void RemovingLeftoversCountsAsAChangeEvenWhenTheHelperIsUpToDate()
    {
        using var directory = new TempDirectory();
        AppBundle.InjectHelper(directory.Path);
        Assert.False(AppBundle.InjectHelper(directory.Path));

        directory.Write("MyTool.dll", "managed code");

        Assert.True(AppBundle.InjectHelper(directory.Path));
    }

    /// <summary>
    /// Writing the helper means unlinking it and putting it back, so two threads doing that to one
    /// bundle would either collide on the file or leave a truncated one behind. <c>AppBundle.Create</c>
    /// serializes them; without that this fails intermittently, which is the worst way for it to fail.
    /// </summary>
    [Fact]
    public void CreateToleratesConcurrentCallsForTheSameBundle()
    {
        using var reference = new TempDirectory();
        AppBundle.InjectHelper(reference.Path);
        var expected = File.ReadAllBytes(reference.Combine("rumpsharp-helper"));

        using var directory = new TempDirectory();
        var results = new string[8];

        Parallel.For(0, results.Length, i => results[i] = AppBundle.Create(new AppBundleOptions
        {
            Name = "Concurrent",
            BundleIdentifier = "dev.rumpsharp.tests.concurrent",
            Location = directory.Path,

            // Signing is slow and reports failure only through Trace, so the assertion is on the
            // helper's bytes, which is what a lost race would corrupt in the first place.
            CodeSign = false,
        }));

        Assert.Equal(directory.Combine("Concurrent.app"), Assert.Single(results.Distinct(StringComparer.Ordinal)));
        Assert.Equal(expected, File.ReadAllBytes(AppBundle.HelperPath(results[0])));
    }
}
