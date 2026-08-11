using System.Runtime.InteropServices;

namespace RumpSharp.Interop;

/// <summary>Loads the system frameworks RumpSharp needs and exposes a few libdyld helpers.</summary>
internal static partial class Native
{
    private const string LibDl = "/usr/lib/libSystem.B.dylib";

    internal const int RtldLazy = 0x1;
    internal const int RtldGlobal = 0x8;

    /// <summary>Pseudo-handle meaning "search every loaded image".</summary>
    internal static readonly IntPtr RtldDefault = new(-2);

    [LibraryImport(LibDl, EntryPoint = "dlopen", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr DlOpen(string path, int mode);

    [LibraryImport(LibDl, EntryPoint = "dlsym", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr DlSym(IntPtr handle, string symbol);

    /// <summary>Reads the value of an exported <c>NSString *</c> constant.</summary>
    internal static string? StringConstant(string symbol)
    {
        var address = DlSym(RtldDefault, symbol);
        return address == IntPtr.Zero ? null : ObjC.FromNSString(Marshal.ReadIntPtr(address));
    }
}

/// <summary>Cached Objective-C class handles. Touching this type loads the required frameworks.</summary>
internal static class Cls
{
    // IMPORTANT: static field initializers run in textual order and *before* any static
    // constructor body, so the frameworks must be dlopen'd by the very first initializers -
    // otherwise every class lookup below silently returns nil.
    private static readonly IntPtr FoundationHandle = Load("Foundation");
    private static readonly IntPtr UserNotificationsHandle = Load("UserNotifications");

    private static IntPtr Load(string framework) =>
        Native.DlOpen($"/System/Library/Frameworks/{framework}.framework/{framework}", Native.RtldLazy | Native.RtldGlobal);

    internal static readonly IntPtr NSAutoreleasePool = ObjC.GetClass("NSAutoreleasePool");
    internal static readonly IntPtr NSString = ObjC.GetClass("NSString");
    internal static readonly IntPtr NSMutableDictionary = ObjC.GetClass("NSMutableDictionary");
    internal static readonly IntPtr NSNumber = ObjC.GetClass("NSNumber");
    internal static readonly IntPtr NSBundle = ObjC.GetClass("NSBundle");
    internal static readonly IntPtr NSObject = ObjC.GetClass("NSObject");
    internal static readonly IntPtr NSURL = ObjC.GetClass("NSURL");
    internal static readonly IntPtr NSArray = ObjC.GetClass("NSArray");
    internal static readonly IntPtr NSSet = ObjC.GetClass("NSSet");

    internal static readonly IntPtr UNUserNotificationCenter = ObjC.LookUpClass("UNUserNotificationCenter");
    internal static readonly IntPtr UNMutableNotificationContent = ObjC.LookUpClass("UNMutableNotificationContent");
    internal static readonly IntPtr UNNotificationRequest = ObjC.LookUpClass("UNNotificationRequest");
    internal static readonly IntPtr UNNotificationSound = ObjC.LookUpClass("UNNotificationSound");
    internal static readonly IntPtr UNTimeIntervalNotificationTrigger = ObjC.LookUpClass("UNTimeIntervalNotificationTrigger");
    internal static readonly IntPtr UNNotificationAttachment = ObjC.LookUpClass("UNNotificationAttachment");
    internal static readonly IntPtr UNNotificationAction = ObjC.LookUpClass("UNNotificationAction");
    internal static readonly IntPtr UNTextInputNotificationAction = ObjC.LookUpClass("UNTextInputNotificationAction");
    internal static readonly IntPtr UNNotificationCategory = ObjC.LookUpClass("UNNotificationCategory");

    /// <summary>True when the UserNotifications framework is present and usable.</summary>
    internal static bool HasUserNotifications =>
        UserNotificationsHandle != IntPtr.Zero && UNUserNotificationCenter != IntPtr.Zero && FoundationHandle != IntPtr.Zero;

    /// <summary>Loads AppKit on demand (only required for the interactive/click support).</summary>
    internal static IntPtr NSApplication => AppKit.NSApplication;

    /// <summary>
    /// AppKit is only needed to become an accessory application, so it is loaded on first use rather
    /// than up front - but exactly once. Its own type so that the field initializers below run in
    /// textual order at that point: <c>dlopen</c> is reference counted, and calling it per access would
    /// leak a reference every time.
    /// </summary>
    private static class AppKit
    {
        internal static readonly IntPtr Handle = Load("AppKit");
        internal static readonly IntPtr NSApplication = ObjC.GetClass("NSApplication");
    }
}

/// <summary>
/// The action identifiers macOS reports for a plain click and for a dismissal, read from the
/// <c>NSString</c> constants that UserNotifications exports.
/// </summary>
/// <remarks>
/// Read on first use rather than in a field initializer on <see cref="NotificationCenter"/>: those run
/// before the constructor body that loads UserNotifications.framework has had a chance to, so the
/// lookup would always fail and silently fall back to a hard-coded literal.
/// </remarks>
internal static class ActionIdentifiers
{
    private static string? _default;
    private static string? _dismiss;

    /// <summary>Reported when the user clicks the notification body.</summary>
    internal static string Default =>
        _default ??= Read("UNNotificationDefaultActionIdentifier", "com.apple.UNNotificationDefaultActionIdentifier");

    /// <summary>Reported when the user dismisses an expanded notification.</summary>
    internal static string Dismiss =>
        _dismiss ??= Read("UNNotificationDismissActionIdentifier", "com.apple.UNNotificationDismissActionIdentifier");

    /// <summary>
    /// Resolves one constant, falling back to its documented value if the symbol is missing. Racing
    /// callers may both do the work; they arrive at the same string.
    /// </summary>
    private static string Read(string symbol, string fallback)
    {
        // Touching Cls is what guarantees the framework exporting these symbols is loaded.
        if (!Cls.HasUserNotifications)
        {
            return fallback;
        }

        using var pool = ObjC.Pool();
        return Native.StringConstant(symbol) ?? fallback;
    }
}

/// <summary>Cached Objective-C selectors.</summary>
internal static class Sel
{
    internal static readonly IntPtr Alloc = ObjC.GetSelector("alloc");
    internal static readonly IntPtr Init = ObjC.GetSelector("init");
    internal static readonly IntPtr Drain = ObjC.GetSelector("drain");
    internal static readonly IntPtr Retain = ObjC.GetSelector("retain");
    internal static readonly IntPtr RespondsToSelector = ObjC.GetSelector("respondsToSelector:");
    internal static readonly IntPtr Release = ObjC.GetSelector("release");

    internal static readonly IntPtr StringWithBytesLengthEncoding = ObjC.GetSelector("stringWithBytes:length:encoding:");

    /// <summary><c>+[NSString string]</c>, the only safe way to build an empty one.</summary>
    internal static readonly IntPtr EmptyString = ObjC.GetSelector("string");
    internal static readonly IntPtr UTF8String = ObjC.GetSelector("UTF8String");
    internal static readonly IntPtr Dictionary = ObjC.GetSelector("dictionary");
    internal static readonly IntPtr SetObjectForKey = ObjC.GetSelector("setObject:forKey:");
    internal static readonly IntPtr ObjectForKey = ObjC.GetSelector("objectForKey:");
    internal static readonly IntPtr AllKeys = ObjC.GetSelector("allKeys");
    internal static readonly IntPtr Count = ObjC.GetSelector("count");
    internal static readonly IntPtr ObjectAtIndex = ObjC.GetSelector("objectAtIndex:");
    internal static readonly IntPtr NumberWithInt = ObjC.GetSelector("numberWithInt:");
    internal static readonly IntPtr Array = ObjC.GetSelector("array");
    internal static readonly IntPtr ArrayWithObjects = ObjC.GetSelector("arrayWithObjects:count:");

    internal static readonly IntPtr MainBundle = ObjC.GetSelector("mainBundle");
    internal static readonly IntPtr BundleIdentifier = ObjC.GetSelector("bundleIdentifier");
    internal static readonly IntPtr BundlePath = ObjC.GetSelector("bundlePath");

    internal static readonly IntPtr LocalizedDescription = ObjC.GetSelector("localizedDescription");

    internal static readonly IntPtr CurrentNotificationCenter = ObjC.GetSelector("currentNotificationCenter");
    internal static readonly IntPtr RequestAuthorization = ObjC.GetSelector("requestAuthorizationWithOptions:completionHandler:");
    internal static readonly IntPtr GetNotificationSettings = ObjC.GetSelector("getNotificationSettingsWithCompletionHandler:");
    internal static readonly IntPtr AddNotificationRequest = ObjC.GetSelector("addNotificationRequest:withCompletionHandler:");
    internal static readonly IntPtr SetDelegate = ObjC.GetSelector("setDelegate:");
    internal static readonly IntPtr AuthorizationStatus = ObjC.GetSelector("authorizationStatus");
    internal static readonly IntPtr RemoveAllDelivered = ObjC.GetSelector("removeAllDeliveredNotifications");
    internal static readonly IntPtr RemoveDelivered = ObjC.GetSelector("removeDeliveredNotificationsWithIdentifiers:");
    internal static readonly IntPtr RemoveAllPending = ObjC.GetSelector("removeAllPendingNotificationRequests");
    internal static readonly IntPtr GetDelivered = ObjC.GetSelector("getDeliveredNotificationsWithCompletionHandler:");
    internal static readonly IntPtr WillPresent = ObjC.GetSelector("userNotificationCenter:willPresentNotification:withCompletionHandler:");
    internal static readonly IntPtr DidReceiveResponse = ObjC.GetSelector("userNotificationCenter:didReceiveNotificationResponse:withCompletionHandler:");

    internal static readonly IntPtr SetTitle = ObjC.GetSelector("setTitle:");
    internal static readonly IntPtr SetSubtitle = ObjC.GetSelector("setSubtitle:");
    internal static readonly IntPtr SetBody = ObjC.GetSelector("setBody:");
    internal static readonly IntPtr SetSound = ObjC.GetSelector("setSound:");
    internal static readonly IntPtr SetUserInfo = ObjC.GetSelector("setUserInfo:");
    internal static readonly IntPtr SetThreadIdentifier = ObjC.GetSelector("setThreadIdentifier:");
    internal static readonly IntPtr SetCategoryIdentifier = ObjC.GetSelector("setCategoryIdentifier:");
    internal static readonly IntPtr SetBadge = ObjC.GetSelector("setBadge:");
    internal static readonly IntPtr DefaultSound = ObjC.GetSelector("defaultSound");
    internal static readonly IntPtr SoundNamed = ObjC.GetSelector("soundNamed:");
    internal static readonly IntPtr RequestWithIdentifier = ObjC.GetSelector("requestWithIdentifier:content:trigger:");
    internal static readonly IntPtr TriggerWithTimeInterval = ObjC.GetSelector("triggerWithTimeInterval:repeats:");

    internal static readonly IntPtr SetAttachments = ObjC.GetSelector("setAttachments:");
    internal static readonly IntPtr AttachmentWithIdentifier = ObjC.GetSelector("attachmentWithIdentifier:URL:options:error:");
    internal static readonly IntPtr FileUrlWithPath = ObjC.GetSelector("fileURLWithPath:");
    internal static readonly IntPtr SetCategories = ObjC.GetSelector("setNotificationCategories:");
    internal static readonly IntPtr SetWithArray = ObjC.GetSelector("setWithArray:");
    internal static readonly IntPtr ActionWithIdentifier = ObjC.GetSelector("actionWithIdentifier:title:options:");
    internal static readonly IntPtr TextActionWithIdentifier =
        ObjC.GetSelector("actionWithIdentifier:title:options:textInputButtonTitle:textInputPlaceholder:");
    internal static readonly IntPtr CategoryWithIdentifier =
        ObjC.GetSelector("categoryWithIdentifier:actions:intentIdentifiers:options:");

    internal static readonly IntPtr Notification = ObjC.GetSelector("notification");
    internal static readonly IntPtr Request = ObjC.GetSelector("request");
    internal static readonly IntPtr Identifier = ObjC.GetSelector("identifier");
    internal static readonly IntPtr Content = ObjC.GetSelector("content");
    internal static readonly IntPtr UserInfo = ObjC.GetSelector("userInfo");
    internal static readonly IntPtr ActionIdentifier = ObjC.GetSelector("actionIdentifier");
    internal static readonly IntPtr UserText = ObjC.GetSelector("userText");
    internal static readonly IntPtr Title = ObjC.GetSelector("title");
    internal static readonly IntPtr Subtitle = ObjC.GetSelector("subtitle");
    internal static readonly IntPtr Body = ObjC.GetSelector("body");

    internal static readonly IntPtr SharedApplication = ObjC.GetSelector("sharedApplication");
    internal static readonly IntPtr SetActivationPolicy = ObjC.GetSelector("setActivationPolicy:");
}
