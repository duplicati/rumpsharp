namespace RumpSharp;

/// <summary>
/// Whether the user has allowed this application to post notifications. Mirrors
/// <c>UNAuthorizationStatus</c>.
/// </summary>
/// <remarks>
/// macOS requires explicit user consent for every application that posts notifications; there is no
/// API that bypasses it. RumpSharp keeps the bundle identifier stable across runs so the prompt
/// appears once for your application and never again.
/// </remarks>
public enum AuthorizationStatus
{
    /// <summary>The user has not been asked yet. The next send triggers the system prompt.</summary>
    NotDetermined = 0,

    /// <summary>The user explicitly denied notifications. Nothing will be displayed.</summary>
    Denied = 1,

    /// <summary>Notifications are allowed.</summary>
    Authorized = 2,

    /// <summary>Notifications are allowed, but delivered quietly to Notification Center only.</summary>
    Provisional = 3,

    /// <summary>Delivery is time-limited (not applicable to macOS applications).</summary>
    Ephemeral = 4,

    /// <summary>The status could not be determined, e.g. the process has no bundle identity.</summary>
    Unavailable = -1,
}
