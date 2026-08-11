using Xunit;

namespace RumpSharp.Tests;

/// <summary>
/// Covers the validation of <see cref="AppBundleOptions"/>. The name ends up as a path segment under
/// <see cref="AppBundleOptions.Location"/>, so anything that is not a plain folder name would let a
/// bundle be written somewhere else entirely.
/// </summary>
public sealed class AppBundleValidationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../escaped")]
    [InlineData("../../escaped")]
    [InlineData("nested/name")]
    [InlineData("/absolute")]
    [InlineData("has:colon")]
    public void CreateRejectsANameThatIsNotAPlainFolderName(string name)
    {
        var options = new AppBundleOptions { Name = name, BundleIdentifier = "com.example.test" };

        var error = Assert.Throws<ArgumentException>(() => AppBundle.Create(options));

        Assert.Equal("options", error.ParamName);
        Assert.Contains("Name", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateRejectsAnInvalidBundleIdentifier()
    {
        var options = new AppBundleOptions { Name = "Valid Name", BundleIdentifier = "com.example.my tool" };

        var error = Assert.Throws<ArgumentException>(() => AppBundle.Create(options));

        Assert.Equal("options", error.ParamName);
        Assert.Contains("BundleIdentifier", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateRejectsAnEmptyVersion()
    {
        var options = new AppBundleOptions
        {
            Name = "Valid Name",
            BundleIdentifier = "com.example.test",
            Version = "",
        };

        var error = Assert.Throws<ArgumentException>(() => AppBundle.Create(options));

        Assert.Contains("Version", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateRejectsNullOptions() =>
        Assert.Throws<ArgumentNullException>(() => AppBundle.Create(null!));

    [Theory]
    [InlineData("")]
    [InlineData(".com.example")]
    [InlineData("com.example.")]
    [InlineData("com..example")]
    [InlineData("com.example.my tool")]
    [InlineData("com.example.my_tool")]
    [InlineData("com.example.tool!")]
    [InlineData("com.example.tööl")]
    [InlineData("com/example")]
    public void InvalidBundleIdentifiersAreRejected(string identifier) =>
        Assert.False(AppBundle.IsValidBundleIdentifier(identifier));

    [Theory]
    [InlineData("com.example.mytool")]
    [InlineData("dev.rumpsharp.sample")]
    [InlineData("com.example.my-tool")]
    [InlineData("com.example.Tool2")]
    [InlineData("single")]
    public void ValidBundleIdentifiersAreAccepted(string identifier) =>
        Assert.True(AppBundle.IsValidBundleIdentifier(identifier));

    /// <summary>
    /// The defaults are derived from the entry assembly name, so they have to survive the same
    /// validation as anything a caller supplies.
    /// </summary>
    [Fact]
    public void DefaultOptionsPassValidation()
    {
        var options = new AppBundleOptions();

        Assert.False(string.IsNullOrWhiteSpace(options.Name));
        Assert.Equal(options.Name, Path.GetFileName(options.Name));
        Assert.True(AppBundle.IsValidBundleIdentifier(options.BundleIdentifier));
        Assert.False(string.IsNullOrWhiteSpace(options.Version));
        Assert.True(Path.IsPathRooted(options.Location));
    }
}
