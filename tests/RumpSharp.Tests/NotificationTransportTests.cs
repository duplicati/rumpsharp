using RumpSharp.Backends;
using Xunit;

namespace RumpSharp.Tests;

/// <summary>
/// Covers choosing the backend explicitly. The test host has no bundle identity of its own, which is
/// what makes <see cref="NotificationTransport.InProcess"/> impossible here and
/// <see cref="NotificationTransport.Helper"/> the only thing that can work.
/// </summary>
/// <remarks>See <see cref="HelperProcessTests"/> for the <c>RUMPSHARP_SKIP_HELPER_TESTS</c> switch.</remarks>
public sealed class NotificationTransportTests
{
    [Fact]
    public void AutomaticIsTheDefault() =>
        Assert.Equal(NotificationTransport.Automatic, new NotificationCenterOptions().Transport);

    /// <summary>
    /// Demanding the in-process backend without the identity for it has to fail loudly: falling back to
    /// the helper would silently ignore what the caller asked for.
    /// </summary>
    [Fact]
    public void InProcessIsRefusedWithoutABundleIdentity()
    {
        var error = Assert.Throws<PlatformNotSupportedException>(() =>
            new NotificationCenter(new NotificationCenterOptions
            {
                Transport = NotificationTransport.InProcess,
            }));

        Assert.Contains("InProcess", error.Message, StringComparison.Ordinal);
        Assert.Contains("Automatic", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The point of asking for the helper explicitly: the notifications are attributed to the bundle
    /// named here, and not to whatever was prepared for the process.
    /// </summary>
    /// <remarks>
    /// The two bundles are what makes this a test. With only one, the bundle the caller asks for is
    /// also the one already prepared, so ignoring <see cref="NotificationCenterOptions.Bundle"/>
    /// entirely would give the same answer and the test would pass either way.
    /// </remarks>
    [Fact]
    public void TheHelperPostsFromTheBundleItIsGiven()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("RUMPSHARP_SKIP_HELPER_TESTS") == "1",
            "RUMPSHARP_SKIP_HELPER_TESTS is set; the helper needs a GUI session.");

        using var chosen = new TestBundle("RumpSharp Transport Chosen");

        // Second, so that this is the bundle prepared for the process - the one the helper would use if
        // it disregarded what it was given. AppBundle.Create records whichever bundle was built last.
        using var prepared = new TestBundle("RumpSharp Transport Prepared");
        Assert.Equal(prepared.Path, AppBundle.NotificationBundlePath);

        using (var center = new NotificationCenter(new NotificationCenterOptions
        {
            Transport = NotificationTransport.Helper,
            Bundle = chosen.Options,

            // Nothing here posts a notification, so nothing should ever prompt.
            RequestAuthorizationOnDemand = false,
        }))
        {
            Assert.Equal(chosen.Path, center.BundlePath);
            Assert.NotEqual(prepared.Path, center.BundlePath);

            // A helper that answers at all is a helper that started, which is worth knowing because
            // BundlePath is read from the same field the helper was launched from. What macOS made of
            // that bundle is TheHelperTakesOnTheIdentityItIsGiven's job.
            Assert.NotEqual(AuthorizationStatus.Unavailable, center.GetAuthorizationStatus());

            // Building it also made it this process's bundle, so the two agree again from here on.
            Assert.Equal(chosen.Path, AppBundle.NotificationBundlePath);
        }

        Assert.True(Directory.Exists(chosen.Path));
    }

    /// <summary>
    /// Without <see cref="NotificationCenterOptions.Bundle"/> the helper uses whatever bundle was
    /// prepared for the process, rather than building a second one of its own.
    /// </summary>
    [Fact]
    public void TheHelperFallsBackToThePreparedBundle()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("RUMPSHARP_SKIP_HELPER_TESTS") == "1",
            "RUMPSHARP_SKIP_HELPER_TESTS is set; the helper needs a GUI session.");

        using var bundle = new TestBundle("RumpSharp Transport Fallback");

        // TestBundle built it through AppBundle.Create, which registers it as this process's bundle.
        Assert.Equal(bundle.Path, AppBundle.NotificationBundlePath);

        using var center = new NotificationCenter(new NotificationCenterOptions
        {
            Transport = NotificationTransport.Helper,
            RequestAuthorizationOnDemand = false,
        });

        Assert.Equal(bundle.Path, center.BundlePath);
        Assert.NotEqual(AuthorizationStatus.Unavailable, center.GetAuthorizationStatus());
        Assert.Equal(bundle.Path, AppBundle.NotificationBundlePath);
    }

    /// <summary>
    /// What the user ends up seeing: the identity macOS gives the helper is the one from the bundle the
    /// caller chose. This is the claim behind "post under a different name", and the only proof of it is
    /// the helper's own <c>ready</c> message - macOS's answer, not RumpSharp's.
    /// </summary>
    /// <remarks>
    /// Goes through <see cref="HelperBackend"/> rather than <see cref="NotificationCenter"/> because
    /// that identity is deliberately not on the public API; the transport itself is covered by
    /// <see cref="TheHelperPostsFromTheBundleItIsGiven"/>.
    /// </remarks>
    [Fact]
    public void TheHelperTakesOnTheIdentityItIsGiven()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("RUMPSHARP_SKIP_HELPER_TESTS") == "1",
            "RUMPSHARP_SKIP_HELPER_TESTS is set; the helper needs a GUI session.");

        using var chosen = new TestBundle("RumpSharp Transport Identity");

        // Again the decoy, so that the identity being asserted can only have come from the chosen
        // bundle and not from the one the process happens to have prepared.
        using var prepared = new TestBundle("RumpSharp Transport Identity Decoy");

        using var backend = new HelperBackend(new NotificationCenterOptions
        {
            Bundle = chosen.Options,
            RequestAuthorizationOnDemand = false,
        });

        Assert.Equal(chosen.Path, backend.BundlePath);
        Assert.Equal(chosen.Options.BundleIdentifier, backend.BundleIdentifier);
        Assert.NotEqual(prepared.Options.BundleIdentifier, backend.BundleIdentifier);
    }
}
