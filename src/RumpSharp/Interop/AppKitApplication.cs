using System.Runtime.InteropServices;

namespace RumpSharp.Interop;

/// <summary>Turns the current process into an accessory (menu-bar style) application.</summary>
/// <remarks>
/// <c>NSApplicationActivationPolicyAccessory</c> means no Dock icon and no app switcher entry, while
/// still being a real application - which is both what a menu-bar tool wants and what lets macOS
/// route a notification click back to the process.
/// </remarks>
internal static class AppKitApplication
{
    private const long Accessory = 1;

    /// <summary>
    /// Creates the shared <c>NSApplication</c> if there is none, then makes it an accessory
    /// application.
    /// </summary>
    /// <remarks>Main thread only: AppKit requires the shared application be created there.</remarks>
    internal static void BecomeAccessory()
    {
        var application = ObjC.Send(Cls.NSApplication, Sel.SharedApplication);
        if (application != IntPtr.Zero)
        {
            ObjC.SendVoidLong(application, Sel.SetActivationPolicy, Accessory);
        }
    }

    /// <summary>
    /// Makes the process an accessory application, but only if something has already created an
    /// <c>NSApplication</c>.
    /// </summary>
    /// <returns><see langword="true"/> if the policy was applied.</returns>
    /// <remarks>
    /// This is the version used when notifications go through the helper. The helper is the
    /// application macOS sees, so the host does not need AppKit for notifications to work - and a
    /// console process has no business creating an <c>NSApplication</c> at all. A UI framework that
    /// has already created one, though, has also already put the host in the Dock, and a menu-bar
    /// application still wants that undone.
    /// </remarks>
    internal static bool BecomeAccessoryIfPresent()
    {
        // Reading the exported NSApp global rather than asking for +sharedApplication, which would
        // create the very thing this is checking for. A null result also covers "AppKit is not even
        // loaded", because then the symbol does not exist.
        var symbol = Native.DlSym(Native.RtldDefault, "NSApp");
        if (symbol == IntPtr.Zero)
        {
            return false;
        }

        var application = Marshal.ReadIntPtr(symbol);
        if (application == IntPtr.Zero)
        {
            return false;
        }

        ObjC.SendVoidLong(application, Sel.SetActivationPolicy, Accessory);
        return true;
    }
}
