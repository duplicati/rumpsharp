using RumpSharp;
using RumpSharp.Samples;

// Assets live outside the build output so regenerating them does not invalidate the bundle.
var assets = Path.Combine(Path.GetTempPath(), "rumpsharp-sample");
var appIcon = IconFactory.CreateAppIcon(Path.Combine(assets, "app-icon.png"));
var thumbnail = IconFactory.CreateThumbnail(Path.Combine(assets, "thumbnail.png"), 236, 88, 120);

// STEP 1: macOS only shows notifications for apps that have a bundle identity, so build a small
// .app bundle - containing RumpSharp's notification helper, not this application - and name it.
// This process is not relaunched: it keeps its console, its arguments and its exit code. The result
// says whether that was needed: false means the app already has a bundle of its own and posts
// notifications itself, with no helper involved.
var viaHelper = AppBundle.PrepareIfNeeded(new AppBundleOptions
{
    Name = "RumpSharp Sample",
    BundleIdentifier = "dev.rumpsharp.sample",
    IconPath = appIcon,
});

Console.WriteLine("RumpSharp sample");
Console.WriteLine($"  process   : {Environment.ProcessPath}");
Console.WriteLine($"  bundle    : {AppBundle.NotificationBundlePath}");
Console.WriteLine($"  posting   : {(viaHelper ? "through the bundled helper" : "in-process")}");
Console.WriteLine($"  arguments : {(args.Length == 0 ? "(none)" : string.Join(' ', args))}");
Console.WriteLine();

// STEP 2: create the center early - this is what starts the helper that receives clicks.
using var center = new NotificationCenter();

Console.WriteLine($"Authorization: {center.GetAuthorizationStatus()}");
if (!center.RequestAuthorization())
{
    Console.WriteLine($"Notifications are not allowed: {center.LastAuthorizationError}");
    Console.WriteLine("Opening System Settings so you can enable them for \"RumpSharp Sample\".");
    AppBundle.OpenNotificationSettings();
    return 1;
}

// STEP 3: report what the user does with our notifications.
center.Activated += (_, response) =>
{
    Console.WriteLine();
    Console.WriteLine($"-> {response.Activation} on \"{response.Title}\" ({response.Identifier})");
    if (response.Activation is NotificationActivation.Action)
    {
        Console.WriteLine($"   action: {response.ActionIdentifier}");
    }

    if (response.UserText is { } text)
    {
        Console.WriteLine($"   reply : {text}");
    }

    foreach (var (key, value) in response.UserInfo)
    {
        Console.WriteLine($"   data  : {key} = {value}");
    }
};

// A plain notification.
center.Show(new Notification(
    "Build finished",
    "RumpSharp.Sample",
    "42 tests passed in 1.8 seconds."));
Console.WriteLine("Sent: plain notification");

// A notification with a thumbnail, buttons, a reply field and round-tripped data.
var interactive = new Notification("Deploy to production?")
{
    Subtitle = "release/v1.4.0",
    Body = "Click the notification, or use one of the buttons below.",
    Identifier = "deploy-prompt",
    ImagePath = thumbnail,
    ThreadIdentifier = "deployments",
    SoundName = null,
    Actions =
    {
        new NotificationAction("deploy", "Deploy") { ActivatesApplication = true },
        new NotificationAction("cancel", "Cancel") { IsDestructive = true },
        NotificationAction.Reply("comment", "Comment", "Post", "Why?"),
    },
    UserInfo = { ["release"] = "v1.4.0", ["commit"] = "9f2ac41" },
};

center.Show(interactive);
Console.WriteLine("Sent: interactive notification (icon + buttons + reply)");

// A scheduled notification.
center.Show(new Notification("Scheduled reminder", body: "Delivered five seconds later.")
{
    Delay = TimeSpan.FromSeconds(5),
});
Console.WriteLine("Sent: scheduled notification (5s delay)");

Console.WriteLine();
Console.WriteLine("Expand the \"Deploy to production?\" banner to see the buttons.");
Console.WriteLine("Listening for interactions for 45 seconds...");

// STEP 4: wait for interactions. In a console app the helper delivers them over a pipe; in a bundled
// app this pumps the macOS run loop that the callbacks arrive on. Either way, keep the process alive.
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
center.RunEventLoop(timeout.Token);

Console.WriteLine();
Console.WriteLine($"Still in Notification Center: {string.Join(", ", center.GetDeliveredIdentifiers())}");
center.RemoveAllDelivered();
Console.WriteLine("Cleared delivered notifications. Done.");
return 0;
