namespace RumpSharp;

/// <summary>How the user interacted with a notification.</summary>
public enum NotificationActivation
{
    /// <summary>The user clicked the notification body, opening the application.</summary>
    Default,

    /// <summary>The user dismissed the notification (only reported for expanded notifications).</summary>
    Dismissed,

    /// <summary>The user pressed one of the <see cref="Notification.Actions"/> buttons.</summary>
    Action,
}

/// <summary>Describes a user interaction with a delivered notification.</summary>
public sealed class NotificationResponse
{
    internal NotificationResponse(
        string identifier,
        string actionIdentifier,
        NotificationActivation activation,
        string title,
        string? subtitle,
        string? body,
        string? userText,
        IReadOnlyDictionary<string, string> userInfo)
    {
        Identifier = identifier;
        ActionIdentifier = actionIdentifier;
        Activation = activation;
        Title = title;
        Subtitle = subtitle;
        Body = body;
        UserText = userText;
        UserInfo = userInfo;
    }

    /// <summary>Identifier of the notification that was interacted with.</summary>
    public string Identifier { get; }

    /// <summary>
    /// Identifier of the pressed <see cref="NotificationAction"/>, or one of the system identifiers
    /// for a plain click or a dismissal. Prefer <see cref="Activation"/> to distinguish those.
    /// </summary>
    public string ActionIdentifier { get; }

    /// <summary>What kind of interaction occurred.</summary>
    public NotificationActivation Activation { get; }

    /// <summary>Title of the notification.</summary>
    public string Title { get; }

    /// <summary>Subtitle of the notification.</summary>
    public string? Subtitle { get; }

    /// <summary>Body of the notification.</summary>
    public string? Body { get; }

    /// <summary>Text the user typed, for a reply action. <see langword="null"/> otherwise.</summary>
    public string? UserText { get; }

    /// <summary>The <see cref="Notification.UserInfo"/> that was attached when sending.</summary>
    public IReadOnlyDictionary<string, string> UserInfo { get; }
}
