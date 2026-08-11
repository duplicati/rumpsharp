using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using RumpSharp;

namespace RumpSharp.TrayIconSample;

/// <summary>
/// A menu-bar-only Avalonia application: no windows at all, just a tray icon whose menu posts
/// notifications through RumpSharp and reports what the user does with them.
/// </summary>
public sealed class App : Application
{
    private NotificationCenter? _center;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _statusItem;
    private int _interactions;

    /// <summary>Path of the generated menu-bar icon, set by <see cref="Program"/>.</summary>
    public static string TrayIconPath { get; set; } = string.Empty;

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        // Avalonia has created NSApplication by now, which is why the notification center is built
        // here rather than in Main: when this process has its own bundle identity, constructing it
        // installs the delegate that receives clicks and switches the process to an accessory
        // (menu-bar) application, and both of those want AppKit to exist already.
        _center = new NotificationCenter();
        _center.Activated += OnNotificationActivated;

        // The very first send triggers the permission prompt. Do it off the UI thread so the menu
        // bar stays responsive while the dialog is up.
        Task.Run(() =>
        {
            var granted = _center.RequestAuthorization();
            Dispatcher.UIThread.Post(() => SetStatus(granted
                ? "Ready - notifications allowed"
                : "Notifications are blocked - see Notification Settings"));
        });

        BuildTrayIcon();

        base.OnFrameworkInitializationCompleted();
    }

    private void BuildTrayIcon()
    {
        _statusItem = new NativeMenuItem("Requesting permission...") { IsEnabled = false };

        var plain = new NativeMenuItem("Send a notification");
        plain.Click += (_, _) => Send(new Notification(
            "RumpSharp",
            "Menu bar sample",
            "Sent from an Avalonia tray application."));

        var interactive = new NativeMenuItem("Ask to deploy (buttons + reply)");
        interactive.Click += (_, _) => Send(new Notification("Deploy to production?")
        {
            Subtitle = "release/v1.4.0",
            Body = "Expand this banner to see the buttons.",
            Identifier = "tray-deploy",
            ThreadIdentifier = "deployments",
            Actions =
            {
                new NotificationAction("deploy", "Deploy") { ActivatesApplication = true },
                new NotificationAction("cancel", "Cancel") { IsDestructive = true },
                NotificationAction.Reply("comment", "Comment", "Post", "Why?"),
            },
            UserInfo = { ["release"] = "v1.4.0" },
        });

        var delayed = new NativeMenuItem("Remind me in 5 seconds");
        delayed.Click += (_, _) => Send(new Notification("Reminder", body: "Five seconds are up.")
        {
            Delay = TimeSpan.FromSeconds(5),
        });

        var clear = new NativeMenuItem("Clear delivered notifications");
        clear.Click += (_, _) => Task.Run(() => _center?.RemoveAllDelivered());

        var settings = new NativeMenuItem("Notification Settings...");
        settings.Click += (_, _) => AppBundle.OpenNotificationSettings();

        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) =>
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
            {
                lifetime.Shutdown();
            }
        };

        var menu = new NativeMenu
        {
            Items =
            {
                _statusItem,
                new NativeMenuItemSeparator(),
                plain,
                interactive,
                delayed,
                new NativeMenuItemSeparator(),
                clear,
                settings,
                new NativeMenuItemSeparator(),
                quit,
            },
        };

        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(TrayIconPath),
            ToolTipText = "RumpSharp tray sample",
            IsVisible = true,
            Menu = menu,
        };

        TrayIcon.SetIcons(this, [_trayIcon]);
    }

    /// <summary>Posts a notification without blocking the UI thread.</summary>
    private void Send(Notification notification) => _ = SendAsync(notification);

    private async Task SendAsync(Notification notification)
    {
        try
        {
            await _center!.ShowAsync(notification);
            Console.WriteLine($"Sent \"{notification.Title}\"");
        }
        catch (NotificationException e)
        {
            Console.WriteLine($"Could not send \"{notification.Title}\": {e.Message}");
            SetStatus("Send failed - see console");
        }
    }

    /// <summary>
    /// Called when the user clicks a notification, presses one of its buttons, or dismisses it.
    /// </summary>
    private void OnNotificationActivated(object? sender, NotificationResponse response)
    {
        // Callbacks arrive on the run loop Avalonia is already pumping, but marshalling explicitly
        // keeps this correct no matter which thread the framework chooses.
        Dispatcher.UIThread.Post(() =>
        {
            _interactions++;

            var description = response.Activation switch
            {
                NotificationActivation.Default => "clicked",
                NotificationActivation.Dismissed => "dismissed",
                _ => $"pressed \"{response.ActionIdentifier}\"",
            };

            if (response.UserText is { Length: > 0 } reply)
            {
                description += $" with reply \"{reply}\"";
            }

            Console.WriteLine($"User {description} on \"{response.Title}\"");
            foreach (var (key, value) in response.UserInfo)
            {
                Console.WriteLine($"  data: {key} = {value}");
            }

            SetStatus($"Last: {description} ({_interactions} total)");

            if (_trayIcon is not null)
            {
                _trayIcon.ToolTipText = $"RumpSharp - {_interactions} interaction(s)";
            }
        });
    }

    private void SetStatus(string text)
    {
        if (_statusItem is not null)
        {
            _statusItem.Header = text;
        }
    }
}
