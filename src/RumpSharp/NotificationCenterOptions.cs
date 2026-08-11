namespace RumpSharp;

/// <summary>Settings for <see cref="NotificationCenter"/>.</summary>
public sealed class NotificationCenterOptions
{
    /// <summary>
    /// Whether to post notifications from this process or through RumpSharp's helper. Defaults to
    /// <see cref="NotificationTransport.Automatic"/>, which decides for you.
    /// </summary>
    public NotificationTransport Transport { get; set; } = NotificationTransport.Automatic;

    /// <summary>
    /// The bundle the helper posts from - the name and icon the user sees on the notification.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only used when notifications actually go through the helper. It is ignored when this process
    /// posts in-process, because then the identity is your own application's and comes from its
    /// <c>Info.plist</c>.
    /// </para>
    /// <para>
    /// When <see langword="null"/>, the bundle prepared by <see cref="AppBundle.PrepareIfNeeded"/> or
    /// <see cref="AppBundle.Create"/> is used, or one built from the defaults if neither was called.
    /// Set it to name the notifications explicitly, which is the point of
    /// <see cref="NotificationTransport.Helper"/>:
    /// </para>
    /// <code>
    /// using var center = new NotificationCenter(new NotificationCenterOptions
    /// {
    ///     Transport = NotificationTransport.Helper,
    ///     Bundle = new AppBundleOptions { Name = "Backup", IconPath = "backup.png" },
    /// });
    /// </code>
    /// </remarks>
    public AppBundleOptions? Bundle { get; set; }

    /// <summary>
    /// Whether the first send automatically asks the user for permission (and blocks until the
    /// prompt is answered). When <see langword="false"/>, call
    /// <see cref="NotificationCenter.RequestAuthorization"/> yourself.
    /// </summary>
    public bool RequestAuthorizationOnDemand { get; set; } = true;

    /// <summary>
    /// Whether notifications are still shown as banners while this application is in the foreground.
    /// macOS suppresses them by default; RumpSharp overrides that.
    /// </summary>
    public bool PresentWhenForeground { get; set; } = true;

    /// <summary>
    /// Whether to register as an accessory (menu-bar style) application on startup - no Dock icon and
    /// no app switcher entry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a process with no <c>NSApplication</c> of its own - a console application posting
    /// in-process, from inside a bundle - this is load-bearing: creating the shared application and
    /// making it an accessory is what turns the process into something macOS will route a notification
    /// click back to, and turning it off costs you <see cref="NotificationCenter.Activated"/> events.
    /// </para>
    /// <para>
    /// For an application whose UI framework has already created an <c>NSApplication</c>, it only
    /// decides whether that application appears in the Dock and the app switcher. Set it to
    /// <see langword="false"/> in a windowed application, so that RumpSharp leaves the activation
    /// policy the framework chose alone.
    /// </para>
    /// <para>
    /// When notifications go through RumpSharp's helper, the helper is the application macOS talks to,
    /// so this only affects how your own process appears: it is applied if an <c>NSApplication</c>
    /// already exists, and ignored in a plain console process, which never gets one. It is also ignored
    /// for an application that has a bundle of its own - one that reached the helper by asking for
    /// <see cref="NotificationTransport.Helper"/> - because taking a real application out of the Dock
    /// is not something choosing a transport should do.
    /// </para>
    /// </remarks>
    public bool BecomeAccessoryApplication { get; set; } = true;

    /// <summary>
    /// How long synchronous operations wait for macOS to answer before giving up. Does not apply to
    /// the permission prompt, which waits for the user.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long an automatic permission request (see <see cref="RequestAuthorizationOnDemand"/>)
    /// waits for the user to answer the system prompt before giving up, so an unattended process
    /// cannot block forever on a dialog nobody will click.
    /// </summary>
    public TimeSpan AuthorizationPromptTimeout { get; set; } = TimeSpan.FromMinutes(2);
}
