namespace RumpSharp.Tests;

/// <summary>
/// A real <c>.app</c> bundle, built for one test and removed afterwards.
/// </summary>
/// <remarks>
/// <para>
/// It has to live under <see cref="AppBundleOptions.Location"/> rather than in a temporary directory:
/// macOS refuses notifications outright for a bundle inside <c>/var/folders</c>, so a helper started
/// from there could not answer anything.
/// </para>
/// <para>
/// Each test class passes its own name. Sharing one bundle between classes means one test's cleanup
/// can collide with another's setup, which is the kind of thing that produces a test that fails once
/// every few runs and cannot be reproduced on demand.
/// </para>
/// </remarks>
internal sealed class TestBundle : IDisposable
{
    /// <param name="name">Bundle name, unique to the test class using it.</param>
    internal TestBundle(string name)
    {
        Options = new AppBundleOptions
        {
            Name = name,
            BundleIdentifier = "dev.rumpsharp.tests." + Slug(name),
            Version = "1.0.0",
        };

        Path = AppBundle.Create(Options);
    }

    /// <summary>The options the bundle was built from.</summary>
    internal AppBundleOptions Options { get; }

    /// <summary>Full path of the bundle.</summary>
    internal string Path { get; }

    /// <summary>Full path of the helper inside the bundle.</summary>
    internal string Helper => AppBundle.HelperPath(Path);

    public void Dispose()
    {
        // A recursive delete can lose a race with anything still finishing inside the bundle - a
        // helper being torn down, or codesign - and answer "directory not empty". Retry rather than
        // fail a test for it.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }

                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 10)
                {
                    throw;
                }

                Thread.Sleep(100);
            }
        }
    }

    private static string Slug(string name) =>
        new(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
}
