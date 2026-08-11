using System.Diagnostics;
using RumpSharp.Ipc;
using Xunit;

namespace RumpSharp.Tests;

/// <summary>
/// Exercises the real helper inside a real bundle: process launch, bundle identity, the NDJSON round
/// trip and shutdown. Nothing here posts a notification, so no permission prompt appears.
/// </summary>
/// <remarks>
/// The helper is an AppKit application and needs a logged-in GUI session. Set
/// <c>RUMPSHARP_SKIP_HELPER_TESTS=1</c> to skip these on a headless machine.
/// </remarks>
public sealed class HelperProcessTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>A bundle of this class's own, so no other test can delete it mid-run.</summary>
    private static TestBundle Bundle() => new("RumpSharp Helper Tests");

    [Fact]
    public void StartsWithTheBundleIdentityAndAnswersRequests()
    {
        SkipIfHeadless();
        using var bundle = Bundle();

        var diagnostics = new List<string>();
        using (var helper = new HelperProcess(bundle.Helper, Collect(diagnostics)))
        {
            var ready = helper.Start();

            // The whole design rests on this: a directly launched binary inside the bundle gets the
            // bundle's identity, without going through LaunchServices.
            Assert.Equal(bundle.Options.BundleIdentifier, ready.BundleId);
            Assert.Equal(bundle.Path, ready.BundlePath);
            Assert.True(helper.IsRunning);

            var configured = helper.Request(
                new HelperRequest { Type = HelperProtocol.Requests.Configure, PresentWhenForeground = true },
                Timeout);
            Assert.NotNull(configured);
            Assert.Null(configured.Error);

            // Reading the status does not prompt, unlike requesting authorization.
            var status = helper.Request(
                new HelperRequest { Type = HelperProtocol.Requests.AuthorizationStatus },
                Timeout);
            Assert.NotNull(status);
            Assert.NotEqual(AuthorizationStatus.Unavailable, HelperProtocol.ToAuthorizationStatus(status.Status));

            var delivered = helper.Request(
                new HelperRequest { Type = HelperProtocol.Requests.Delivered },
                Timeout);
            Assert.NotNull(delivered);
            Assert.NotNull(delivered.Ids);
        }

        lock (diagnostics)
        {
            Assert.DoesNotContain(diagnostics, message => message.Contains("unexpectedly", StringComparison.Ordinal));
        }
    }

    /// <summary>A line the helper cannot read must be reported and skipped, not end the session.</summary>
    [Fact]
    public void SurvivesAMalformedRequest()
    {
        SkipIfHeadless();
        using var bundle = Bundle();

        var diagnostics = new List<string>();
        using var helper = new HelperProcess(bundle.Helper, Collect(diagnostics));
        helper.Start();

        helper.Post(new HelperRequest { Type = "not a real request type" });

        var answer = helper.Request(
            new HelperRequest { Type = HelperProtocol.Requests.AuthorizationStatus },
            Timeout);

        Assert.NotNull(answer);
        Assert.True(helper.IsRunning);
        lock (diagnostics)
        {
            Assert.Contains(diagnostics, message => message.Contains("Unknown message type", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void SendingToAStoppedHelperFails()
    {
        SkipIfHeadless();
        using var bundle = Bundle();

        var helper = new HelperProcess(bundle.Helper, _ => { });
        helper.Start();
        helper.Dispose();

        Assert.False(helper.IsRunning);
        Assert.Throws<NotificationException>(() =>
            helper.Post(new HelperRequest { Type = HelperProtocol.Requests.Delivered }));
    }

    /// <summary>
    /// Clicking a notification after the host has exited makes LaunchServices launch the bundle on its
    /// own, with stdin on <c>/dev/null</c>. There is no host to talk to then, so the helper has to bow
    /// out rather than linger as an invisible application nobody can quit.
    /// </summary>
    [Fact]
    public void ExitsImmediatelyWhenNothingIsAttachedToStdin()
    {
        SkipIfHeadless();
        using var bundle = Bundle();

        var info = new ProcessStartInfo("/bin/sh") { RedirectStandardError = true };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add($"exec '{bundle.Helper}' < /dev/null");

        using var process = Process.Start(info);
        Assert.NotNull(process);

        Assert.True(process.WaitForExit(Timeout), "the helper kept running without a host attached");
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("no host attached", process.StandardError.ReadToEnd(), StringComparison.Ordinal);
    }

    private static Action<string> Collect(List<string> diagnostics) => message =>
    {
        lock (diagnostics)
        {
            diagnostics.Add(message);
        }
    };

    private static void SkipIfHeadless() =>
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("RUMPSHARP_SKIP_HELPER_TESTS") == "1",
            "RUMPSHARP_SKIP_HELPER_TESTS is set; the helper needs a GUI session.");
}
