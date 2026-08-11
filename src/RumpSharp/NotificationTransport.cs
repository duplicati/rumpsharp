namespace RumpSharp;

/// <summary>
/// How <see cref="NotificationCenter"/> reaches macOS: from this process, or through RumpSharp's
/// bundled helper.
/// </summary>
/// <remarks>
/// macOS only accepts notifications from an application with a <c>.app</c> bundle identity, and
/// <see cref="Automatic"/> picks whichever mechanism can supply one. The other two are for the cases
/// where you know better than the default.
/// </remarks>
public enum NotificationTransport
{
    /// <summary>
    /// Post in-process if this process has its own bundle identity, and through the helper if it does
    /// not. This is almost always what you want.
    /// </summary>
    Automatic = 0,

    /// <summary>
    /// Always post in-process, talking to <c>UNUserNotificationCenter</c> directly.
    /// </summary>
    /// <remarks>
    /// Requires this process to have a bundle identity of its own; construction throws
    /// <see cref="PlatformNotSupportedException"/> if it has none, rather than quietly falling back to
    /// the helper. Remember that callbacks then arrive on the main thread's run loop and only while
    /// something is pumping it.
    /// </remarks>
    InProcess = 1,

    /// <summary>
    /// Always post through the helper process, even when this process could do it itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two reasons to ask for this. The first is that notifications are then attributed to the bundle
    /// described by <see cref="NotificationCenterOptions.Bundle"/> rather than to your application, so
    /// you choose the name and icon the user sees - useful when the application's own name is not the
    /// one that should appear on a notification.
    /// </para>
    /// <para>
    /// The second is that the helper owns its own run loop, so <see cref="NotificationCenter.Activated"/>
    /// fires whether or not your process pumps one. In-process callbacks need the <em>main</em> thread's
    /// run loop, which a background service or an application whose main thread is owned by something
    /// else may never be able to give.
    /// </para>
    /// <para>
    /// The cost is that the helper cannot run from your application's bundle - that bundle's
    /// <c>CFBundleExecutable</c> is your application - so a second bundle is created for it. Unless you
    /// point <see cref="AppBundleOptions.BundleIdentifier"/> at your own identifier, macOS treats it as a
    /// different application and asks the user for permission again.
    /// </para>
    /// </remarks>
    Helper = 2,
}
