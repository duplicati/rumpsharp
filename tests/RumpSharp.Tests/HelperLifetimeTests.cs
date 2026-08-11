using System.Diagnostics;
using RumpSharp.Ipc;
using Xunit;

namespace RumpSharp.Tests;

/// <summary>
/// Covers the promise that a helper never outlives the process that started it. Nothing marks the
/// child for cleanup - macOS has no equivalent of <c>PR_SET_PDEATHSIG</c> - so the guarantee rests on
/// the helper exiting when stdin reaches EOF, which the operating system causes by closing the pipe
/// when the last descriptor referring to it goes away.
/// </summary>
/// <remarks>See <see cref="HelperProcessTests"/> for the <c>RUMPSHARP_SKIP_HELPER_TESTS</c> switch.</remarks>
public sealed class HelperLifetimeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>A bundle of this class's own, so no other test can delete it mid-run.</summary>
    private static TestBundle Bundle() => new("RumpSharp Lifetime Tests");

    /// <summary>Disposing is the graceful path: the helper is asked to shut down and does.</summary>
    [Fact]
    public void DisposingStopsTheHelper()
    {
        SkipIfHeadless();
        using var bundle = Bundle();

        var helper = new HelperProcess(bundle.Helper, _ => { });
        var pid = ProcessIdOf(helper, bundle);

        helper.Dispose();

        Assert.False(IsAlive(pid), "the helper survived Dispose");
    }

    /// <summary>
    /// The case that actually matters, because it covers every way a process can end: a crash, a
    /// <c>SIGKILL</c>, or simply forgetting to dispose. Closing stdin without a shutdown request
    /// stands in for the host's descriptors being closed by the kernel.
    /// </summary>
    [Fact]
    public void LosingStdinStopsTheHelper()
    {
        SkipIfHeadless();
        using var bundle = Bundle();

        var info = new ProcessStartInfo(bundle.Helper)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(info)!;

        // Wait for 'ready', so we know it reached the run loop it now has to leave.
        Assert.NotNull(process.StandardOutput.ReadLine());

        process.StandardInput.Close();

        Assert.True(
            process.WaitForExit((int)Timeout.TotalMilliseconds),
            "the helper kept running after its stdin was closed");
        Assert.Equal(0, process.ExitCode);
    }

    private static int ProcessIdOf(HelperProcess helper, TestBundle bundle)
    {
        helper.Start();
        Assert.True(helper.IsRunning);

        // The helper reports its own identity, but not its pid; ask the system instead.
        var info = new ProcessStartInfo("/usr/bin/pgrep") { RedirectStandardOutput = true };
        info.ArgumentList.Add("-f");
        info.ArgumentList.Add(bundle.Helper);

        using var pgrep = Process.Start(info)!;
        var output = pgrep.StandardOutput.ReadToEnd();
        pgrep.WaitForExit();

        var pid = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => int.TryParse(line.Trim(), out var value) ? value : 0)
            .FirstOrDefault(value => value > 0);

        Assert.True(pid > 0, "could not find the running helper");
        return pid;
    }

    private static bool IsAlive(int pid)
    {
        // Give a graceful shutdown a moment to complete before concluding anything.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var _ = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                return false;
            }

            Thread.Sleep(100);
        }

        return true;
    }

    private static void SkipIfHeadless() =>
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("RUMPSHARP_SKIP_HELPER_TESTS") == "1",
            "RUMPSHARP_SKIP_HELPER_TESTS is set; the helper needs a GUI session.");
}
