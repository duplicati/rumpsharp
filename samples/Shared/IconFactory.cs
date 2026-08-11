using System.IO.Compression;

namespace RumpSharp.Samples;

/// <summary>
/// Generates the PNG images this sample uses, so the repository stays free of binary assets.
/// Writes a minimal PNG by hand: macOS only needs a plain RGBA image.
/// </summary>
internal static class IconFactory
{
    /// <summary>Draws the app icon: a rounded gradient square with a white bell.</summary>
    /// <param name="path">Destination file. Existing files are kept.</param>
    /// <param name="size">Width and height in pixels.</param>
    /// <returns>The path that was written.</returns>
    public static string CreateAppIcon(string path, int size = 512)
    {
        if (File.Exists(path))
        {
            return path;
        }

        var pixels = new byte[size * size * 4];
        var radius = size * 0.22;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var offset = ((y * size) + x) * 4;
                if (!InRoundedSquare(x, y, size, radius))
                {
                    continue;
                }

                // Vertical gradient from indigo to a lighter violet.
                var t = (double)y / size;
                var (r, g, b) = (Lerp(88, 168, t), Lerp(80, 85, t), Lerp(236, 247, t));

                if (InBell(x, y, size))
                {
                    (r, g, b) = (255, 255, 255);
                }

                pixels[offset] = (byte)r;
                pixels[offset + 1] = (byte)g;
                pixels[offset + 2] = (byte)b;
                pixels[offset + 3] = 255;
            }
        }

        WritePng(path, size, size, pixels);
        return path;
    }

    /// <summary>Draws the menu-bar icon: the same artwork at status-item size.</summary>
    /// <param name="path">Destination file. Existing files are kept.</param>
    /// <returns>The path that was written.</returns>
    public static string CreateTrayIcon(string path) => CreateAppIcon(path, 44);

    /// <summary>Draws a flat coloured square used as a per-notification thumbnail.</summary>
    /// <param name="path">Destination file. Existing files are kept.</param>
    /// <param name="red">Red channel.</param>
    /// <param name="green">Green channel.</param>
    /// <param name="blue">Blue channel.</param>
    /// <param name="size">Width and height in pixels.</param>
    /// <returns>The path that was written.</returns>
    public static string CreateThumbnail(string path, byte red, byte green, byte blue, int size = 256)
    {
        if (File.Exists(path))
        {
            return path;
        }

        var pixels = new byte[size * size * 4];
        var centre = size / 2.0;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var offset = ((y * size) + x) * 4;
                var distance = Math.Sqrt(((x - centre) * (x - centre)) + ((y - centre) * (y - centre))) / centre;
                var shade = Math.Clamp(1.25 - distance, 0, 1);

                pixels[offset] = (byte)(red * shade);
                pixels[offset + 1] = (byte)(green * shade);
                pixels[offset + 2] = (byte)(blue * shade);
                pixels[offset + 3] = 255;
            }
        }

        WritePng(path, size, size, pixels);
        return path;
    }

    private static bool InRoundedSquare(int x, int y, int size, double radius)
    {
        double cx = Math.Min(x, size - 1 - x);
        double cy = Math.Min(y, size - 1 - y);
        if (cx >= radius || cy >= radius)
        {
            return true;
        }

        var dx = radius - cx;
        var dy = radius - cy;
        return (dx * dx) + (dy * dy) <= radius * radius;
    }

    /// <summary>Bell silhouette: a dome, a flared body, a base bar and the clapper.</summary>
    private static bool InBell(int x, int y, int size)
    {
        var nx = ((x / (double)size) - 0.5) * 2;
        var ny = (y / (double)size) - 0.5;

        // Clapper.
        if ((nx * nx) + ((ny - 0.34) * (ny - 0.34) * 4) <= 0.0075)
        {
            return true;
        }

        // Base bar.
        if (ny is >= 0.20 and <= 0.26 && Math.Abs(nx) <= 0.52)
        {
            return true;
        }

        // Body: widens towards the bottom.
        if (ny is >= -0.30 and <= 0.20)
        {
            var halfWidth = 0.16 + (0.36 * (ny + 0.30) / 0.50);
            return Math.Abs(nx) <= halfWidth;
        }

        // Dome.
        return (nx * nx) + ((ny + 0.30) * (ny + 0.30)) <= 0.16 * 0.16 && ny <= -0.30;
    }

    private static double Lerp(double from, double to, double t) => from + ((to - from) * t);

    // ------------------------------------------------------------------ minimal PNG encoder

    private static void WritePng(string path, int width, int height, byte[] rgba)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);

        file.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        WriteBigEndian(header, 0, width);
        WriteBigEndian(header, 4, height);
        header[8] = 8;  // bit depth
        header[9] = 6;  // colour type: RGBA
        WriteChunk(file, "IHDR", header);

        // Each scanline is prefixed with its filter type (0 = none).
        var raw = new byte[height * ((width * 4) + 1)];
        for (var y = 0; y < height; y++)
        {
            var source = y * width * 4;
            var target = (y * ((width * 4) + 1)) + 1;
            rgba.AsSpan(source, width * 4).CopyTo(raw.AsSpan(target));
        }

        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(raw);
        }

        WriteChunk(file, "IDAT", compressed.ToArray());
        WriteChunk(file, "IEND", []);
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, data.Length);
        stream.Write(length);

        var payload = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++)
        {
            payload[i] = (byte)type[i];
        }

        data.CopyTo(payload, 4);
        stream.Write(payload);

        var crc = new byte[4];
        WriteBigEndian(crc, 0, unchecked((int)Crc32(payload)));
        stream.Write(crc);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)(-(int)(crc & 1)));
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
