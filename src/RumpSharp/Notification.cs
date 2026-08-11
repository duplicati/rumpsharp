namespace RumpSharp;

/// <summary>A notification to display in the macOS Notification Center.</summary>
public sealed class Notification
{
    /// <summary>Creates an empty notification.</summary>
    public Notification()
    {
    }

    /// <summary>Creates a notification with the given text.</summary>
    /// <param name="title">Bold headline text.</param>
    /// <param name="subtitle">Smaller text below the title.</param>
    /// <param name="body">Body text below the subtitle.</param>
    public Notification(string title, string? subtitle = null, string? body = null)
    {
        Title = title;
        Subtitle = subtitle;
        Body = body;
    }

    /// <summary>Bold headline text.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Smaller text shown below the title.</summary>
    public string? Subtitle { get; set; }

    /// <summary>Body text shown below the subtitle.</summary>
    public string? Body { get; set; }

    /// <summary>
    /// Unique identifier. Reusing an identifier replaces the earlier notification instead of adding
    /// a second one. Defaults to a generated GUID.
    /// </summary>
    public string Identifier { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Whether macOS plays a sound when the notification arrives.</summary>
    public bool PlaySound { get; set; } = true;

    /// <summary>
    /// Name of a custom sound file (for example <c>"Ping.aiff"</c>) placed in the app bundle's
    /// <c>Contents/Resources</c> or in <c>~/Library/Sounds</c>. When <see langword="null"/> the
    /// default notification sound is used.
    /// </summary>
    public string? SoundName { get; set; }

    /// <summary>
    /// Path to an image (PNG, JPEG or GIF) shown as the notification's thumbnail on the right hand
    /// side of the banner.
    /// </summary>
    /// <remarks>
    /// This is the per-notification image. The small badge icon in the corner of the banner is the
    /// icon of the owning application - set that through <see cref="AppBundleOptions.IconPath"/>.
    /// The file is copied before being handed to macOS, because the system takes ownership of
    /// attachment files and would otherwise move the original out of place.
    /// </remarks>
    public string? ImagePath { get; set; }

    /// <summary>Buttons shown on the notification when the user expands or hovers over it.</summary>
    public IList<NotificationAction> Actions { get; } = [];

    /// <summary>
    /// Arbitrary string data echoed back to <see cref="NotificationCenter.Activated"/> when the user
    /// interacts with this notification. Equivalent to the <c>data</c> parameter of
    /// <c>rumps.notification</c>. Keep it small - Apple documents a limit of roughly 1 KB.
    /// </summary>
    public IDictionary<string, string> UserInfo { get; } = new Dictionary<string, string>();

    /// <summary>
    /// Delay before the notification is delivered. When <see langword="null"/> (the default) it is
    /// delivered immediately.
    /// </summary>
    public TimeSpan? Delay { get; set; }

    /// <summary>Groups related notifications together in Notification Center.</summary>
    public string? ThreadIdentifier { get; set; }

    /// <summary>Number to show on the application's Dock icon badge, if any.</summary>
    public int? BadgeCount { get; set; }
}
