using System.Diagnostics;
using System.Runtime.InteropServices;
using RumpSharp.Interop;
using Xunit;

namespace RumpSharp.Tests;

/// <summary>
/// Covers run loop pumping. <c>CFRunLoopRunInMode</c> returns "finished" <em>immediately</em> when the
/// mode has no sources registered - which is the case on a thread nothing has attached anything to,
/// like the one running these tests - so a naive pump returns instantly and a polling caller spins a
/// core at 100%.
/// </summary>
public sealed partial class RunLoopTests
{
    /// <summary><c>CLOCK_THREAD_CPUTIME_ID</c> on Darwin.</summary>
    private const int ClockThreadCpuTime = 16;

    [Fact]
    public void PumpWaitsInsteadOfReturningImmediately()
    {
        // Warm up, so resolving kCFRunLoopDefaultMode is not counted.
        RunLoop.Pump(TimeSpan.FromMilliseconds(1));

        var stopwatch = Stopwatch.StartNew();
        var handled = RunLoop.Pump(TimeSpan.FromMilliseconds(300));
        stopwatch.Stop();

        Assert.True(
            handled || stopwatch.ElapsedMilliseconds >= 250,
            $"pump returned after {stopwatch.ElapsedMilliseconds}ms without handling anything");
    }

    [Fact]
    public void PumpReturnsImmediatelyForAZeroTimeout()
    {
        Assert.False(RunLoop.Pump(TimeSpan.Zero));
    }

    /// <summary>
    /// The regression test for the busy loop: pumping an idle run loop for half a second must not cost
    /// half a second of CPU.
    /// </summary>
    /// <remarks>
    /// Deliberately measured per <em>thread</em>, not per process. <c>Pump</c> burns CPU on the thread
    /// that calls it, and the process-wide figure also counts whatever else happens to be running -
    /// the runtime reaping the child processes other tests start, for instance - which made this fail
    /// perhaps one run in five for reasons that had nothing to do with the run loop.
    /// </remarks>
    [Fact]
    public void PumpDoesNotBurnCpuWhileIdle()
    {
        var before = ThreadCpuTime();
        var wall = Stopwatch.StartNew();

        while (wall.ElapsedMilliseconds < 500)
        {
            RunLoop.Pump(TimeSpan.FromMilliseconds(100));
        }

        wall.Stop();
        var cpu = ThreadCpuTime() - before;

        Assert.True(
            cpu < wall.Elapsed * 0.5,
            $"used {cpu.TotalMilliseconds:F0}ms of CPU on this thread while idling for {wall.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Proves the measurement above can actually see CPU being burned. Without this, a
    /// <see cref="ThreadCpuTime"/> that always answered zero would make that test pass whatever the run
    /// loop did.
    /// </summary>
    [Fact]
    public void TheThreadCpuMeasurementDetectsABusyLoop()
    {
        var before = ThreadCpuTime();
        var wall = Stopwatch.StartNew();

        while (wall.ElapsedMilliseconds < 200)
        {
            // Spin on purpose.
        }

        wall.Stop();
        var cpu = ThreadCpuTime() - before;

        Assert.True(
            cpu > wall.Elapsed * 0.5,
            $"a deliberate busy loop only registered {cpu.TotalMilliseconds:F0}ms of CPU over "
            + $"{wall.ElapsedMilliseconds}ms, so the measurement cannot be trusted");
    }

    /// <summary>CPU time consumed by the calling thread.</summary>
    /// <remarks>
    /// There is no managed API for this - <see cref="Process.TotalProcessorTime"/> is the whole process
    /// and <see cref="Thread"/> exposes nothing - so ask POSIX.
    /// </remarks>
    private static TimeSpan ThreadCpuTime()
    {
        Assert.Equal(0, ClockGetTime(ClockThreadCpuTime, out var time));
        return TimeSpan.FromSeconds(time.Seconds) + TimeSpan.FromTicks(time.Nanoseconds / 100);
    }

    [LibraryImport("libSystem.B.dylib", EntryPoint = "clock_gettime", SetLastError = true)]
    private static partial int ClockGetTime(int clock, out TimeSpec time);

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeSpec
    {
        internal nint Seconds;
        internal nint Nanoseconds;
    }
}
