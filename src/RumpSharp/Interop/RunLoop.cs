using System.Runtime.InteropServices;

namespace RumpSharp.Interop;

/// <summary>
/// Drives the CoreFoundation run loop. Notification delegate callbacks (clicks, action buttons,
/// replies) are delivered as run loop sources on the main thread, so a console application has to
/// pump the loop to receive them - this is the equivalent of the <c>NSApplication</c> run loop that
/// rumps relies on.
/// </summary>
internal static partial class RunLoop
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    /// <summary>The run loop ran to completion because it had no sources left.</summary>
    internal const int Finished = 1;

    /// <summary>The timeout elapsed.</summary>
    internal const int TimedOut = 3;

    /// <summary>How long to sleep when the run loop refuses to wait for us.</summary>
    private const int IdleSleepMilliseconds = 25;

    private static readonly IntPtr DefaultMode = ReadMode("kCFRunLoopDefaultMode");

    [LibraryImport(CoreFoundation, EntryPoint = "CFRunLoopRunInMode")]
    private static partial int CFRunLoopRunInMode(IntPtr mode, double seconds, [MarshalAs(UnmanagedType.U1)] bool returnAfterSourceHandled);

    private static IntPtr ReadMode(string symbol)
    {
        var address = Native.DlSym(Native.RtldDefault, symbol);
        return address == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(address);
    }

    /// <summary>
    /// Runs the current thread's run loop for at most <paramref name="seconds"/>, returning as soon
    /// as one source has been handled.
    /// </summary>
    internal static int Run(double seconds) => CFRunLoopRunInMode(DefaultMode, seconds, true);

    /// <summary>
    /// Pumps the current thread's run loop for up to <paramref name="timeout"/>, returning as soon
    /// as a source has been handled.
    /// </summary>
    /// <remarks>
    /// <c>CFRunLoopRunInMode</c> returns <see cref="Finished"/> <em>immediately</em>, without
    /// waiting, whenever the mode has no sources, timers or observers registered - which is the case
    /// on a thread nothing has attached anything to yet, and whenever the mode itself could not be
    /// resolved. Sleeping out the remainder in that case is what stops a polling caller from
    /// spinning a core at 100%.
    /// </remarks>
    /// <param name="timeout">How long to pump for. Must not be negative.</param>
    /// <returns><see langword="true"/> if a run loop source was handled.</returns>
    internal static bool Pump(TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        while (true)
        {
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                return false;
            }

            switch (Run(remaining / 1000.0))
            {
                case TimedOut:
                    return false;
                case Finished:
                    Thread.Sleep((int)Math.Min(remaining, IdleSleepMilliseconds));
                    break;
                default:
                    return true;
            }
        }
    }
}
