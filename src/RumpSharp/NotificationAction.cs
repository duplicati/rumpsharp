namespace RumpSharp;

/// <summary>A button displayed on a notification.</summary>
public sealed class NotificationAction
{
    /// <summary>Creates an action button.</summary>
    /// <param name="identifier">Identifier reported back in <see cref="NotificationResponse.ActionIdentifier"/>.</param>
    /// <param name="title">Button label.</param>
    public NotificationAction(string identifier, string title)
    {
        Identifier = identifier;
        Title = title;
    }

    /// <summary>Identifier reported back in <see cref="NotificationResponse.ActionIdentifier"/>.</summary>
    public string Identifier { get; }

    /// <summary>Button label.</summary>
    public string Title { get; set; }

    /// <summary>Draws the button in red to indicate a destructive operation.</summary>
    public bool IsDestructive { get; set; }

    /// <summary>Brings the application to the foreground when the button is pressed.</summary>
    public bool ActivatesApplication { get; set; }

    /// <summary>Requires the device to be unlocked before the action runs.</summary>
    public bool RequiresAuthentication { get; set; }

    /// <summary>
    /// When set, the button opens a text field instead of firing immediately, and the typed text is
    /// reported in <see cref="NotificationResponse.UserText"/>.
    /// </summary>
    public NotificationTextInput? TextInput { get; set; }

    /// <summary>Creates an action that lets the user type a reply.</summary>
    /// <param name="identifier">Identifier reported back in the response.</param>
    /// <param name="title">Button label.</param>
    /// <param name="sendButtonTitle">Label of the button that submits the text.</param>
    /// <param name="placeholder">Placeholder text for the input field.</param>
    public static NotificationAction Reply(
        string identifier,
        string title,
        string sendButtonTitle = "Send",
        string placeholder = "") =>
        new(identifier, title) { TextInput = new NotificationTextInput(sendButtonTitle, placeholder) };
}

/// <summary>Text field configuration for a reply-style <see cref="NotificationAction"/>.</summary>
/// <param name="SendButtonTitle">Label of the button that submits the text.</param>
/// <param name="Placeholder">Placeholder text shown in the empty field.</param>
public sealed record NotificationTextInput(string SendButtonTitle = "Send", string Placeholder = "");
