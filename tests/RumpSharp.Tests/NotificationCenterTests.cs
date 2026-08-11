using RumpSharp.Interop;
using Xunit;

namespace RumpSharp.Tests;

/// <summary>
/// Covers how <see cref="NotificationCenter"/> decides between talking to macOS in-process and going
/// through the helper. The test host is a plain console executable with no bundle identity, which is
/// the case that must never reach <c>+[UNUserNotificationCenter currentNotificationCenter]</c>: doing
/// that raises an Objective-C exception, which aborts the process instead of surfacing as a catchable
/// .NET exception.
/// </summary>
public sealed class NotificationCenterTests
{
    [Fact]
    public void TheTestHostHasNoBundleIdentity()
    {
        Assert.Null(AppBundle.CurrentBundlePath);
        Assert.False(AppBundle.IsBundled);
    }

    /// <summary>
    /// A console application is supported now: it borrows the generated bundle's identity through the
    /// helper instead of having one of its own.
    /// </summary>
    [Fact]
    public void IsSupportedWithoutABundleOfItsOwn() => Assert.True(NotificationCenter.IsSupported);

    /// <summary>
    /// The test host has no bundle of its own, so a bundle has to be prepared for it - and the answer
    /// is what tells the caller that notifications will go through the helper.
    /// </summary>
    [Fact]
    public void PrepareIfNeededReportsThatABundleWasPrepared()
    {
        // Built and removed by TestBundle; this only checks what PrepareIfNeeded reports about it.
        using var bundle = new TestBundle("RumpSharp Prepare Tests");

        Assert.True(AppBundle.PrepareIfNeeded(bundle.Options));
        Assert.True(Directory.Exists(bundle.Path));
        Assert.Equal(bundle.Path, AppBundle.NotificationBundlePath);

        // The answer describes the process, not the disk, so it does not change once the bundle is
        // already there and up to date.
        Assert.True(AppBundle.PrepareIfNeeded(bundle.Options));
    }

    /// <summary>
    /// Creating a bundle explicitly has to be enough: otherwise <see cref="NotificationCenter"/> would
    /// ignore it and quietly build a second one from the defaults.
    /// </summary>
    [Fact]
    public void CreateRegistersTheBundleItBuilt()
    {
        using var directory = new TempDirectory();
        var bundle = AppBundle.Create(new AppBundleOptions
        {
            Name = "Explicit",
            BundleIdentifier = "dev.rumpsharp.explicit",
            Location = directory.Path,
            CodeSign = false,
        });

        Assert.Equal(bundle, AppBundle.NotificationBundlePath);
    }

    /// <summary>
    /// The in-process backend needs the framework; the helper carries its own binding to it. Either
    /// way it has to be loadable here, or nothing about this library works.
    /// </summary>
    [Fact]
    public void UserNotificationsFrameworkIsAvailable()
    {
        Assert.True(Cls.HasUserNotifications);
        Assert.NotEqual(IntPtr.Zero, Cls.UNUserNotificationCenter);
        Assert.NotEqual(IntPtr.Zero, Cls.UNMutableNotificationContent);
        Assert.NotEqual(IntPtr.Zero, Cls.UNNotificationCategory);
        Assert.NotEqual(IntPtr.Zero, Cls.UNTextInputNotificationAction);
    }
}
