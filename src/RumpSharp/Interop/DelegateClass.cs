using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RumpSharp.Interop;

/// <summary>
/// Builds an Objective-C class at runtime that conforms to <c>UNUserNotificationCenterDelegate</c>
/// and forwards the two callbacks we care about into managed code.
/// </summary>
/// <remarks>
/// <c>UNUserNotificationCenter</c> is a process-wide singleton and therefore has a single delegate,
/// so the bridge itself is static.
/// </remarks>
internal static unsafe class DelegateClass
{
    private const string ClassName = "RumpSharpNotificationDelegate";

    private static IntPtr _instance;

    /// <summary>
    /// Invoked when a notification arrives while this process is the foreground application.
    /// Returns the presentation options to use.
    /// </summary>
    internal static Func<IntPtr, ulong>? WillPresent { get; set; }

    /// <summary>Invoked when the user clicks the notification, an action button, or dismisses it.</summary>
    internal static Action<IntPtr>? DidReceiveResponse { get; set; }

    /// <summary>Creates (once) and returns the shared delegate instance.</summary>
    internal static IntPtr Instance()
    {
        if (_instance != IntPtr.Zero)
        {
            return _instance;
        }

        var cls = ObjC.LookUpClass(ClassName);
        if (cls == IntPtr.Zero)
        {
            cls = ObjC.AllocateClassPair(Cls.NSObject, ClassName, 0);
            if (cls == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var protocol = ObjC.GetProtocol("UNUserNotificationCenterDelegate");
            if (protocol != IntPtr.Zero)
            {
                ObjC.AddProtocol(cls, protocol);
            }

            // "v@:@@@" == void return; self, selector, then three object arguments.
            ObjC.AddMethod(
                cls,
                Sel.WillPresent,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&WillPresentImpl,
                "v@:@@@");

            ObjC.AddMethod(
                cls,
                Sel.DidReceiveResponse,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidReceiveResponseImpl,
                "v@:@@@");

            ObjC.RegisterClassPair(cls);
        }

        _instance = ObjC.New(cls);
        return _instance;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void WillPresentImpl(IntPtr self, IntPtr selector, IntPtr center, IntPtr notification, IntPtr completionHandler)
    {
        // Default to the full banner + Notification Center listing + sound.
        var options = (ulong)(NotificationPresentationOptions.Sound | NotificationPresentationOptions.List | NotificationPresentationOptions.Banner);
        try
        {
            if (WillPresent is { } callback)
            {
                options = callback(notification);
            }
        }
        catch
        {
            // Swallow: an exception must not unwind into Objective-C.
        }

        Block.Invoke(completionHandler, options);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DidReceiveResponseImpl(IntPtr self, IntPtr selector, IntPtr center, IntPtr response, IntPtr completionHandler)
    {
        try
        {
            DidReceiveResponse?.Invoke(response);
        }
        catch
        {
        }

        // The system waits for this before considering the response handled.
        Block.Invoke(completionHandler);
    }
}

/// <summary>Mirrors <c>UNNotificationPresentationOptions</c>.</summary>
[Flags]
internal enum NotificationPresentationOptions : ulong
{
    None = 0,
    Badge = 1 << 0,
    Sound = 1 << 1,
    List = 1 << 3,
    Banner = 1 << 4,
}
