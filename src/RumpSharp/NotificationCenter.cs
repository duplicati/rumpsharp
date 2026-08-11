using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using RumpSharp.Backends;
using RumpSharp.Interop;

namespace RumpSharp;

/// <summary>
/// Posts notifications to the macOS Notification Center and reports what the user does with them.
/// </summary>
/// <remarks>
/// <para>
/// macOS only accepts notifications from an application with a <c>.app</c> bundle identity, and there
/// are two ways to have one, so there are two ways this class works. By default the choice follows
/// whether this process has a bundle of its own; <see cref="NotificationCenterOptions.Transport"/>
/// makes it explicit:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// If your application already runs from a <c>.app</c> bundle - an Avalonia app, or anything launched
/// through LaunchServices - it talks to <c>UNUserNotificationCenter</c> directly, in this process.
/// Callbacks then arrive on the main thread's run loop, so they only fire while that run loop is
/// pumped, by your UI framework or by <see cref="ProcessEvents"/> / <see cref="RunEventLoop"/>.
/// </description>
/// </item>
/// <item>
/// <description>
/// If it is a plain console application, RumpSharp posts through a small bundled helper process (see
/// <see cref="AppBundle"/>) and callbacks arrive over a pipe, on a background thread. Your process
/// keeps its console, its working directory and its exit code; nothing is relaunched.
/// </description>
/// </item>
/// </list>
/// <para>
/// An application that has a bundle of its own can still ask for the helper, with
/// <see cref="NotificationTransport.Helper"/>, so that the notifications carry the name and icon of
/// <see cref="NotificationCenterOptions.Bundle"/> instead of its own. <see cref="BundlePath"/> always
/// says which bundle an instance ended up posting from.
/// </para>
/// <para>
/// Create the instance early - before showing any notification - so that whichever mechanism receives
/// clicks is in place in time.
/// </para>
/// <para>
/// Either way, the user must grant permission once per bundle identifier.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// AppBundle.PrepareIfNeeded(new AppBundleOptions { Name = "Reminders" });
/// using var center = new NotificationCenter();
/// center.Show(new Notification("Build finished", body: "All 42 tests passed."));
/// </code>
/// </example>
[SupportedOSPlatform("macos")]
public sealed class NotificationCenter : IDisposable
{
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(250);

    private static readonly Lazy<NotificationCenter> Shared = new(() => new NotificationCenter());

    private readonly INotificationBackend _backend;

    private bool _disposed;

    /// <summary>Creates a notification center.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Construct this on the main thread.</strong> In an application that already has a bundle
    /// identity, the default <see cref="NotificationCenterOptions.BecomeAccessoryApplication"/> calls
    /// <c>+[NSApplication sharedApplication]</c>, and AppKit requires that the shared application be
    /// created on the main thread - doing it anywhere else is undefined behaviour rather than a
    /// diagnosable error. Set the option to <see langword="false"/> if you have to construct off the
    /// main thread, at the cost of notification clicks no longer being routed back to the process.
    /// </para>
    /// <para>
    /// In a UI application, construct it once the framework has created <c>NSApplication</c> - for
    /// Avalonia, in <c>OnFrameworkInitializationCompleted</c>.
    /// </para>
    /// <para>
    /// Whenever notifications go through the helper - always for a console application - the
    /// constructor creates or refreshes the <c>.app</c> bundle the helper runs from and starts the
    /// helper inside it. Set <see cref="NotificationCenterOptions.Bundle"/>, or call
    /// <see cref="AppBundle.PrepareIfNeeded"/> beforehand, if you want to choose the name and icon the
    /// user sees; otherwise they are derived from the entry assembly.
    /// </para>
    /// </remarks>
    /// <param name="options">Behaviour settings, or <see langword="null"/> for the defaults.</param>
    /// <exception cref="PlatformNotSupportedException">
    /// The process is not running on macOS, the UserNotifications framework is missing, or
    /// <see cref="NotificationTransport.InProcess"/> was asked for by a process that has no bundle
    /// identity to post with.
    /// </exception>
    /// <exception cref="NotificationException">The notification helper could not be started.</exception>
    public NotificationCenter(NotificationCenterOptions? options = null)
    {
        options ??= new NotificationCenterOptions();

        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("RumpSharp notifications are only available on macOS.");
        }

        if (PostsInProcess(options.Transport))
        {
            // The helper carries its own binding to the framework, so this only has to hold here.
            if (!Cls.HasUserNotifications)
            {
                throw new PlatformNotSupportedException(
                    "The UserNotifications framework could not be loaded, so this process cannot post notifications.");
            }

            _backend = new ObjCBackend(options);
        }
        else
        {
            _backend = new HelperBackend(options);
        }

        _backend.Activated = response => Activated?.Invoke(this, response);
    }

    /// <summary>
    /// Raised when the user clicks a notification, presses one of its action buttons, or dismisses
    /// it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In an application that has its own bundle identity, macOS delivers these callbacks to the
    /// <em>main</em> thread's run loop, so they only fire while that run loop is being pumped - by a UI
    /// framework, or by <see cref="ProcessEvents"/> / <see cref="RunEventLoop"/> on the main thread -
    /// and the handler runs on that thread. Keep the handler quick: macOS is waiting for it before it
    /// considers the response handled.
    /// </para>
    /// <para>
    /// When RumpSharp is posting through its helper process (the console application case), the handler
    /// runs on the thread that reads from the helper instead, as soon as the user acts, whether or not
    /// anything is pumping. Marshal to your own thread if that matters to you.
    /// </para>
    /// </remarks>
    public event EventHandler<NotificationResponse>? Activated;

    /// <summary>A lazily created shared instance using the default options.</summary>
    /// <remarks>
    /// Touch this first from the main thread - the instance is built on whichever thread gets here
    /// first, and the constructor has an AppKit main-thread requirement.
    /// </remarks>
    public static NotificationCenter Default => Shared.Value;

    /// <summary>Whether the current process can post notifications at all.</summary>
    /// <remarks>
    /// A console application counts as supported: it gets its identity from the generated bundle and
    /// posts through the helper. Only a non-macOS process, or one whose UserNotifications framework
    /// cannot be loaded, does not.
    /// </remarks>
    public static bool IsSupported =>
        OperatingSystem.IsMacOS() && (!AppBundle.IsBundled || Cls.HasUserNotifications);

    /// <summary>The message macOS gave for the most recent failed authorization request, if any.</summary>
    public string? LastAuthorizationError => _backend.LastAuthorizationError;

    /// <summary>
    /// The <c>.app</c> bundle whose name and icon the user sees on notifications from this instance.
    /// </summary>
    /// <remarks>
    /// This is the authoritative answer, and it is not always
    /// <see cref="AppBundle.NotificationBundlePath"/>: posting in-process it is your own application's
    /// bundle, while <see cref="NotificationTransport.Helper"/> makes it the generated bundle the helper
    /// runs from, even for an application that has a bundle of its own.
    /// </remarks>
    public string? BundlePath => _backend.BundlePath;

    /// <summary>Reads the current permission state without prompting the user.</summary>
    /// <returns>The permission state, or <see cref="AuthorizationStatus.Unavailable"/> on timeout.</returns>
    public AuthorizationStatus GetAuthorizationStatus()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _backend.GetAuthorizationStatus();
    }

    /// <summary>
    /// Asks the user for permission to post notifications, showing the system prompt the first time
    /// it is called for this bundle identifier.
    /// </summary>
    /// <param name="timeout">
    /// How long to wait for an answer. <see langword="null"/> waits indefinitely, which is usually
    /// what you want because the user has to interact with a dialog.
    /// </param>
    /// <returns><see langword="true"/> if notifications are allowed.</returns>
    public bool RequestAuthorization(TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _backend.RequestAuthorization(timeout);
    }

    /// <summary>Shows a notification with only text.</summary>
    /// <param name="title">Bold headline text.</param>
    /// <param name="subtitle">Smaller text below the title.</param>
    /// <param name="body">Body text below the subtitle.</param>
    public void Show(string title, string? subtitle = null, string? body = null) =>
        Show(new Notification(title, subtitle, body));

    /// <summary>Shows a notification.</summary>
    /// <param name="notification">The notification to display.</param>
    /// <exception cref="NotificationException">macOS refused the notification.</exception>
    public void Show(Notification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _backend.Show(notification);
    }

    /// <summary>Shows a notification without blocking the calling thread.</summary>
    /// <param name="notification">The notification to display.</param>
    /// <param name="cancellationToken">Cancels the wait, not the delivery.</param>
    /// <returns>A task that completes once macOS has accepted the notification.</returns>
    public Task ShowAsync(Notification notification, CancellationToken cancellationToken = default) =>
        Task.Run(() => Show(notification), cancellationToken);

    /// <summary>
    /// Lets pending callbacks run for up to <paramref name="timeout"/>, returning as soon as one has
    /// been handled.
    /// </summary>
    /// <remarks>
    /// In an application with its own bundle identity this pumps the main thread's run loop, which is
    /// where macOS delivers notification responses, so call it on the main thread. When posting through
    /// the helper it waits for a response to arrive over the pipe instead. Either way the call waits
    /// out <paramref name="timeout"/> when there is nothing to do, so it is safe to use as the body of
    /// a polling loop.
    /// </remarks>
    /// <param name="timeout">Maximum time to wait for a callback. Must not be negative.</param>
    /// <returns><see langword="true"/> if a callback was handled.</returns>
    public bool ProcessEvents(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);

        return _backend.ProcessEvents(timeout);
    }

    /// <summary>
    /// Runs until the token is cancelled, dispatching <see cref="Activated"/> callbacks as the user
    /// interacts with notifications.
    /// </summary>
    /// <remarks>Call this on the main thread - see <see cref="ProcessEvents"/>.</remarks>
    /// <param name="cancellationToken">Stops the loop when cancelled.</param>
    public void RunEventLoop(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (!cancellationToken.IsCancellationRequested)
        {
            _backend.ProcessEvents(PumpInterval);
        }
    }

    /// <summary>Identifiers of the notifications currently listed in Notification Center.</summary>
    /// <returns>The delivered notification identifiers.</returns>
    public IReadOnlyList<string> GetDeliveredIdentifiers()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _backend.GetDeliveredIdentifiers();
    }

    /// <summary>Removes specific notifications from Notification Center.</summary>
    /// <param name="identifiers">The <see cref="Notification.Identifier"/> values to remove.</param>
    public void RemoveDelivered(params string[] identifiers)
    {
        ArgumentNullException.ThrowIfNull(identifiers);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (identifiers.Length == 0)
        {
            return;
        }

        _backend.RemoveDelivered(identifiers);
    }

    /// <summary>Clears every notification this application has delivered.</summary>
    public void RemoveAllDelivered()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _backend.RemoveAllDelivered();
    }

    /// <summary>Cancels notifications that were scheduled with a <see cref="Notification.Delay"/>.</summary>
    public void RemoveAllPending()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _backend.RemoveAllPending();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Stops the helper process, if one was started, and detaches from the Objective-C delegate if it
    /// is still ours. Notifications that have already been delivered stay in Notification Center.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _backend.Dispose();
        Activated = null;
    }

    /// <summary>Whether to talk to macOS from this process, rather than through the helper.</summary>
    /// <exception cref="PlatformNotSupportedException">
    /// <see cref="NotificationTransport.InProcess"/> was demanded by a process that has no bundle
    /// identity to do it with.
    /// </exception>
    private static bool PostsInProcess(NotificationTransport transport)
    {
        switch (transport)
        {
            case NotificationTransport.InProcess when !AppBundle.IsBundled:
                // Asked for explicitly, so say why it cannot be done instead of quietly doing
                // something else.
                throw new PlatformNotSupportedException(
                    "NotificationTransport.InProcess needs this process to have a .app bundle identity of its "
                    + "own, and it has none, so macOS would refuse its notifications. Ship the application as a "
                    + ".app bundle, or use NotificationTransport.Automatic and let RumpSharp post through its "
                    + "helper instead.");

            case NotificationTransport.InProcess:
                return true;

            case NotificationTransport.Helper:
                return false;

            default:
                return AppBundle.IsBundled;
        }
    }

    /// <summary>A stable identifier for a set of action buttons.</summary>
    /// <remarks>
    /// The digest has to be stable across processes, not secure: macOS stores the category identifier
    /// on every notification it has delivered, so a later run has to derive the same one or the
    /// buttons on notifications still sitting in Notification Center stop working. That rules out
    /// <see cref="object.GetHashCode"/>, whose string seed is randomised per process. Both backends
    /// derive it the same way, so moving between them does not break the buttons either.
    /// </remarks>
    internal static string CategoryIdentifier(IList<NotificationAction> actions)
    {
        var key = string.Join(
            '\u001f',
            actions.Select(a => $"{a.Identifier}|{a.Title}|{a.IsDestructive}|{a.ActivatesApplication}|{a.RequiresAuthentication}|{a.TextInput?.SendButtonTitle}|{a.TextInput?.Placeholder}"));

        return "rumpsharp." + Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(key)));
    }
}
