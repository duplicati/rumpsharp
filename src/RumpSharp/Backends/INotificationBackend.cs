namespace RumpSharp.Backends;

/// <summary>
/// The notification implementation behind <see cref="NotificationCenter"/>.
/// </summary>
/// <remarks>
/// There are two, and which one is used depends on whether the process has a bundle identity of its
/// own: <see cref="ObjCBackend"/> talks to <c>UNUserNotificationCenter</c> in-process, and
/// <see cref="HelperBackend"/> drives the bundled <c>rumpsharp-helper</c> over a pipe. The
/// distinction is invisible through the public API.
/// </remarks>
internal interface INotificationBackend : IDisposable
{
    /// <summary>
    /// Invoked when the user clicks a notification, presses an action button, or dismisses one.
    /// </summary>
    Action<NotificationResponse>? Activated { get; set; }

    /// <summary>The message from the most recent failed authorization request, if any.</summary>
    string? LastAuthorizationError { get; }

    /// <summary>The <c>.app</c> bundle whose identity these notifications are posted with.</summary>
    string? BundlePath { get; }

    /// <summary>Reads the current permission state without prompting.</summary>
    AuthorizationStatus GetAuthorizationStatus();

    /// <summary>Asks the user for permission to post notifications.</summary>
    /// <param name="timeout">How long to wait for an answer, or <see langword="null"/> to wait forever.</param>
    bool RequestAuthorization(TimeSpan? timeout);

    /// <summary>Posts a notification.</summary>
    void Show(Notification notification);

    /// <summary>
    /// Lets pending callbacks run for up to <paramref name="timeout"/>, returning as soon as one has
    /// been handled.
    /// </summary>
    bool ProcessEvents(TimeSpan timeout);

    /// <summary>Identifiers of the notifications currently listed in Notification Center.</summary>
    IReadOnlyList<string> GetDeliveredIdentifiers();

    /// <summary>Removes specific notifications from Notification Center.</summary>
    void RemoveDelivered(string[] identifiers);

    /// <summary>Clears every notification this application has delivered.</summary>
    void RemoveAllDelivered();

    /// <summary>Cancels notifications that were scheduled with a delay.</summary>
    void RemoveAllPending();
}
