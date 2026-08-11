using System.Diagnostics;
using System.IO.Compression;
using RumpSharp.Samples;
using Xunit;

namespace RumpSharp.Tests;

/// <summary>
/// Covers the sample's hand-rolled PNG encoder. It has no library behind it, so the output is checked
/// both structurally and against a decoder that ships with macOS.
/// </summary>
public sealed class IconFactoryTests
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void WritesAStructurallyValidPng()
    {
        using var temp = new TempDirectory();
        const int size = 32;

        var path = IconFactory.CreateAppIcon(temp.Combine("icon.png"), size);
        var bytes = File.ReadAllBytes(path);

        Assert.Equal(Signature, bytes[..8]);

        var chunks = ReadChunks(bytes);
        Assert.Equal(["IHDR", "IDAT", "IEND"], chunks.Select(c => c.Type));

        var header = chunks[0].Data;
        Assert.Equal(13, header.Length);
        Assert.Equal(size, ReadBigEndian(header, 0));
        Assert.Equal(size, ReadBigEndian(header, 4));
        Assert.Equal(8, header[8]);   // bit depth
        Assert.Equal(6, header[9]);   // colour type: RGBA
        Assert.Empty(chunks[2].Data);
    }

    [Fact]
    public void PixelDataDecompressesToOneFilterBytePerScanline()
    {
        using var temp = new TempDirectory();
        const int size = 16;

        var path = IconFactory.CreateThumbnail(temp.Combine("thumb.png"), 236, 88, 120, size);
        var idat = ReadChunks(File.ReadAllBytes(path)).Single(c => c.Type == "IDAT").Data;

        using var compressed = new MemoryStream(idat);
        using var inflate = new ZLibStream(compressed, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflate.CopyTo(raw);

        var stride = (size * 4) + 1;
        var pixels = raw.ToArray();

        Assert.Equal(size * stride, pixels.Length);
        for (var y = 0; y < size; y++)
        {
            Assert.Equal(0, pixels[y * stride]);   // filter type: none
        }
    }

    /// <summary>An independent oracle: the same decoder <c>iconutil</c> feeds off.</summary>
    [Fact]
    public void MacOsReadsTheGeneratedPng()
    {
        using var temp = new TempDirectory();

        var path = IconFactory.CreateAppIcon(temp.Combine("icon.png"), 64);
        var output = Sips(path);

        Assert.Contains("pixelWidth: 64", output, StringComparison.Ordinal);
        Assert.Contains("pixelHeight: 64", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingFilesAreLeftAlone()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("icon.png", "not really a png");

        Assert.Equal(path, IconFactory.CreateAppIcon(path));
        Assert.Equal("not really a png", File.ReadAllText(path));
    }

    [Fact]
    public void TrayIconIsWrittenAtStatusItemSize()
    {
        using var temp = new TempDirectory();

        var path = IconFactory.CreateTrayIcon(temp.Combine("tray.png"));
        var header = ReadChunks(File.ReadAllBytes(path))[0].Data;

        Assert.Equal(44, ReadBigEndian(header, 0));
        Assert.Equal(44, ReadBigEndian(header, 4));
    }

    private static List<(string Type, byte[] Data)> ReadChunks(byte[] png)
    {
        var chunks = new List<(string, byte[])>();
        var offset = Signature.Length;

        while (offset < png.Length)
        {
            var length = ReadBigEndian(png, offset);
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            var data = png[(offset + 8)..(offset + 8 + length)];

            chunks.Add((type, data));
            offset += 12 + length;
        }

        Assert.Equal(png.Length, offset);
        return chunks;
    }

    private static int ReadBigEndian(byte[] buffer, int offset) =>
        (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];

    private static string Sips(string path)
    {
        var info = new ProcessStartInfo("sips")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        info.ArgumentList.Add("-g");
        info.ArgumentList.Add("pixelWidth");
        info.ArgumentList.Add("-g");
        info.ArgumentList.Add("pixelHeight");
        info.ArgumentList.Add(path);

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(TimeSpan.FromSeconds(30));

        Assert.Equal(0, process.ExitCode);
        return output;
    }
}
