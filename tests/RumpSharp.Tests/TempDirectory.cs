namespace RumpSharp.Tests;

/// <summary>A throwaway directory that deletes itself at the end of a test.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rumpsharp-tests-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    /// <summary>Full path of the directory.</summary>
    public string Path { get; }

    /// <summary>Combines <see cref="Path"/> with <paramref name="parts"/>.</summary>
    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>Writes a file, creating any missing parent directories.</summary>
    public string Write(string relativePath, string content)
    {
        var target = Combine(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        File.WriteAllText(target, content);
        return target;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
