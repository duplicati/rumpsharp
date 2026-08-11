# RumpSharp

Native **macOS Notification Center** notifications for .NET 10 — with custom icons, action buttons,
reply fields and click callbacks.

No Xamarin, no `net10.0-macos` target, no workload install. RumpSharp is a plain `net10.0` library
that talks to the Objective-C runtime and `UserNotifications.framework` directly, the same way the
Python [rumps](https://github.com/jaredks/rumps) package does through PyObjC. For non-bundle
applications it ships a native helper - 88 KB embedded, 320 KB once unpacked - that supplies the
`.app` identity macOS insists on.

```csharp
using RumpSharp;

// Give the console app the .app bundle identity macOS requires (see below).
AppBundle.PrepareIfNeeded(new AppBundleOptions
{
    Name = "My Tool",
    BundleIdentifier = "com.example.mytool",
    IconPath = "icon.png",
});

using var center = new NotificationCenter();
center.Show("Build finished", "MyProject", "42 tests passed in 1.8 seconds.");
```

## Features

- Title, subtitle and body text
- Per-notification thumbnail image, plus a real app icon on the banner
- Action buttons, including destructive buttons and inline reply fields
- Click / action / dismiss callbacks delivered to a C# event
- Round-trip `UserInfo` data, exactly like the `data` argument of `rumps.notification`
- Scheduled delivery, notification grouping, Dock badge counts, custom sounds
- Query, update and clear delivered notifications
- Your process is never relaunched: it keeps its console, stdin, arguments and exit code
- Choose whether notifications come from your app or from a helper with its own name and icon
- Trimming- and AOT-compatible, no NuGet dependencies

## Installing

```sh
dotnet add package RumpSharp
```

Requires .NET 10 and macOS 11 or later, which is what the generated bundle declares as its
`LSMinimumSystemVersion`.

## The two macOS rules you cannot avoid

Everything awkward about macOS notifications comes down to two OS-level rules. RumpSharp handles the
first one for you and makes the second one a one-time event.

### 1. Only applications with a bundle identity may post notifications

A bare console executable cannot show a notification: `UNUserNotificationCenter` needs a
`CFBundleIdentifier`, and it reads that from the bundle the _running executable_ sits in. This is the
same restriction that forces rumps users to build with `py2app`.

If your app already ships as a real `.app` bundle, there is nothing to do — RumpSharp posts from
your process, in-process, and `PrepareIfNeeded` returns `false` having done nothing.

Otherwise `AppBundle.PrepareIfNeeded()` returns `true` and builds (or refreshes)
`~/Library/Application Support/RumpSharp/<Name>.app`, generating `Info.plist`, converting your PNG
into a multi-resolution `.icns` and ad-hoc signing the result with `codesign`. The bundle's
executable is RumpSharp's own notification helper — **your application is not copied into it** — so
the whole thing is about 400 KB no matter how large your app is. `NotificationCenter` then starts
that helper as a child process and drives it over a pipe: the helper owns the notifications, and
your process stays exactly what it was, with its console, stdin, arguments and exit code intact.

The bundle location and identifier are stable across rebuilds, which is what keeps rule 2 a one-time
event.

Consequences worth knowing:

- The name, icon and Dock behaviour the user sees are the _helper's_, from `AppBundleOptions`. Give
  the bundle the name you want to appear in System Settings › Notifications.
- Notification clicks only reach you while your process is running, because the helper stops with it.
  A click after that just brings up the (empty) helper application, which exits immediately.
- `Show` is a round trip to another process. It is still fast, but use `ShowAsync` from UI threads.
- How your app is launched no longer matters: `dotnet run`, the apphost, `dotnet YourApp.dll`, a
  single-file or a NativeAOT build all work, because the identity comes from the bundled helper rather
  than from where your own executable happens to live.

### 2. The user must grant permission, once per bundle identifier

There is no API that bypasses this, on any recent macOS — the modern `UNUserNotificationCenter` and
the deprecated `NSUserNotification` both refuse to display anything until the user agrees. RumpSharp
therefore aims for _exactly one_ prompt for the lifetime of your app:

- keep `BundleIdentifier` stable — changing it makes macOS treat your app as brand new and prompt
  again;
- the first `Show` triggers the prompt automatically (set
  `NotificationCenterOptions.RequestAuthorizationOnDemand = false` to control the timing yourself);
- inspect the state with `GetAuthorizationStatus()`, and send the user to the right settings pane
  with `AppBundle.OpenNotificationSettings()` if they said no.

```csharp
if (center.GetAuthorizationStatus() is AuthorizationStatus.Denied)
{
    Console.WriteLine("Notifications are disabled for this app.");
    AppBundle.OpenNotificationSettings();
}
```

## Choosing how notifications are posted

By default RumpSharp decides for itself: in-process if your application has a bundle identity, through
the helper if it does not. `NotificationCenterOptions.Transport` overrides that.

| `NotificationTransport` | Behaviour |
| --- | --- |
| `Automatic` (default) | In-process when the process has its own bundle, through the helper otherwise |
| `InProcess` | Always in-process. Throws `PlatformNotSupportedException` if the process has no bundle identity, rather than silently falling back |
| `Helper` | Always through the helper, even for an application that could post itself |

### Posting under a different name

`Helper` exists mainly so that the notifications do not have to carry your application's identity. The
helper posts from its own bundle, so its `Name` and `IconPath` are what the user sees — on the banner
and in System Settings › Notifications — no matter what the host application is called:

```csharp
// A backup tool whose notifications should say "Backup", not "Acme Suite Helper Service".
using var center = new NotificationCenter(new NotificationCenterOptions
{
    Transport = NotificationTransport.Helper,
    Bundle = new AppBundleOptions
    {
        Name = "Backup",
        BundleIdentifier = "com.example.acme.backup",
        IconPath = "backup.png",
    },
});

Console.WriteLine(center.BundlePath);   // ~/Library/Application Support/RumpSharp/Backup.app
```

The other reason to force it: the helper runs its own run loop, so `Activated` fires without your
process pumping one. In-process callbacks need the **main** thread's run loop, which a background
service — or anything whose main thread belongs to someone else — may never be able to provide.

Two things to know before reaching for it:

- **macOS sees a different application,** because the helper cannot run from your application's bundle
  (that bundle's `CFBundleExecutable` is your application). Permission is per bundle identifier, so
  unless you set `BundleIdentifier` to your own, the user is asked to allow notifications again — and
  the new name appears as a separate entry in System Settings.
- **`center.BundlePath` is the authoritative answer** to "where do my notifications come from".
  `AppBundle.NotificationBundlePath` reports the *default* for the process and does not know about an
  explicit `Transport`, so for a bundled application forcing the helper the two disagree.

Your own process is otherwise untouched: an application with a bundle of its own keeps the activation
policy it chose, so forcing the helper never moves it out of the Dock or the app switcher, whatever
`BecomeAccessoryApplication` says.

## Icons

There are two separate images on a macOS notification, and RumpSharp exposes both:

| Image                               | Set with                    | Notes                                                                      |
| ----------------------------------- | --------------------------- | -------------------------------------------------------------------------- |
| Small app icon in the banner corner | `AppBundleOptions.IconPath` | `.icns`, or a square `.png` that RumpSharp converts with `sips`/`iconutil` |
| Large thumbnail on the right        | `Notification.ImagePath`    | PNG, JPEG or GIF, per notification                                         |

```csharp
center.Show(new Notification("Deploy finished")
{
    Body = "release/v1.4.0 is live.",
    ImagePath = "screenshot.png",
});
```

macOS takes ownership of attachment files and moves them into its own store, so RumpSharp always
hands it a throwaway copy and leaves your file where it is.

## Buttons, replies and callbacks

Action buttons appear when the banner is expanded (hover, or pull down on the notification).

```csharp
using var center = new NotificationCenter();

center.Activated += (_, response) =>
{
    switch (response.Activation)
    {
        case NotificationActivation.Default:
            Console.WriteLine($"clicked {response.Identifier}");
            break;
        case NotificationActivation.Action:
            Console.WriteLine($"pressed {response.ActionIdentifier} on release {response.UserInfo["release"]}");
            break;
        case NotificationActivation.Dismissed:
            Console.WriteLine("dismissed");
            break;
    }

    if (response.UserText is { } reply)
    {
        Console.WriteLine($"user typed: {reply}");
    }
};

center.Show(new Notification("Deploy to production?")
{
    Subtitle = "release/v1.4.0",
    Actions =
    {
        new NotificationAction("deploy", "Deploy") { ActivatesApplication = true },
        new NotificationAction("cancel", "Cancel") { IsDestructive = true },
        NotificationAction.Reply("comment", "Comment", "Post", "Why?"),
    },
    UserInfo = { ["release"] = "v1.4.0" },
});

// Keep the process alive so the callbacks above can arrive.
center.RunEventLoop(cancellationToken);
```

Create the `NotificationCenter` before sending anything: constructing it is what puts the machinery
that receives responses in place.

Where the handler runs depends on which of the two rule-1 cases you are in:

|                                  | Console app (through the helper)              | App with its own `.app` bundle                |
| -------------------------------- | --------------------------------------------- | --------------------------------------------- |
| Handler thread                   | the thread reading from the helper            | the **main** thread                           |
| Fires when                       | the user acts, always                         | only while the main run loop is pumped        |
| `RunEventLoop` / `ProcessEvents` | keeps your process alive and reports arrivals | required, unless a UI framework pumps for you |

So a console app must not exit while it still wants callbacks (`RunEventLoop`), and a bundled app has
to have its main run loop running (Avalonia and friends already do — see below).

`UNUserNotificationCenter` is a process-wide singleton with a single delegate, so use one
`NotificationCenter` (or `NotificationCenter.Default`) per process. Constructing a second one takes
the callbacks over from the first.

## Scheduling, grouping and housekeeping

```csharp
center.Show(new Notification("Stand-up in 5 minutes") { Delay = TimeSpan.FromMinutes(5) });
center.Show(new Notification("Step 2 of 3") { Identifier = "progress" });   // replaces same id
center.Show(new Notification("Deployed") { ThreadIdentifier = "deploys" }); // groups in NC

center.GetDeliveredIdentifiers();
center.RemoveDelivered("progress");
center.RemoveAllDelivered();
center.RemoveAllPending();      // cancels scheduled notifications
```

## Use with Avalonia (tray-icon-only app)

A menu-bar-only Avalonia app is the closest .NET equivalent of a rumps app. An Avalonia app normally
ships as a real `.app`, which is rule 1 already satisfied: there is **one required step**, and it is
creating the center in the right place.

```csharp
public override void OnFrameworkInitializationCompleted()
{
    // Create the center once Avalonia has initialised, on the UI thread: it works with the
    // NSApplication Avalonia created rather than making one of its own.
    _center = new NotificationCenter();
    _center.Activated += (_, response) => Dispatcher.UIThread.Post(() => Handle(response));

    // Never block the UI thread on the permission prompt.
    Task.Run(() => _center.RequestAuthorization());
}
```

**Do not call `RunEventLoop` or `ProcessEvents`.** Avalonia already keeps the process alive and pumps
the macOS run loop. Those two methods exist only for plain console apps.

Nothing else is required, and in particular `AppBundle.PrepareIfNeeded` is not: once your app runs from
its own `.app` it returns `false` immediately and **every `AppBundleOptions` value is ignored**. The
name, icon and Dock behaviour the user sees come from your own `Info.plist` at that point. Call it only
if you also run the bare executable during development, where it names the helper's bundle — and the
return value tells you which of the two you are:

```csharp
public static int Main(string[] args)
{
    // false once packaged as a .app: nothing was needed. true straight out of bin/, where it gives
    // the helper's bundle a name and icon instead of ones derived from the assembly name.
    var viaHelper = AppBundle.PrepareIfNeeded(new AppBundleOptions
    {
        Name = "My Tray App",
        BundleIdentifier = "com.example.traytool",
        IconPath = "app-icon.png",
        ShowInDock = false,
    });

    return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
}
```

Other things worth knowing:

- **RumpSharp makes your process an accessory application by default** — no Dock icon, no app switcher
  entry — which is what a menu-bar app wants. If yours has windows, set
  `NotificationCenterOptions.BecomeAccessoryApplication` to `false` and RumpSharp leaves your
  activation policy alone. Leave it on for a plain console app: there, creating an `NSApplication` and
  making it an accessory is what turns the process into something macOS can route a click back to.
- Use `ShowAsync` from UI event handlers; `Show` blocks until the notification has been acknowledged.
- Use `ShutdownMode.OnExplicitShutdown` — a tray-only app has no window whose closing could end it.
- Both shapes work and look identical to the user: packaged as a `.app` it posts in-process with no
  helper, and straight from `bin/` it posts through the helper. `samples/RumpSharp.TrayIcon.Sample`
  prints which one it is using, and its `package.sh` builds the `.app`.

## Running the samples

```sh
dotnet run --project samples/RumpSharp.Sample          # console
dotnet run --project samples/RumpSharp.TrayIcon.Sample # Avalonia menu-bar app
```

The console sample generates its own icon artwork, sends a plain notification, an interactive one
with a thumbnail and three buttons, and a delayed one, then listens 45 seconds and prints whatever
you do with them.

The Avalonia sample adds a menu-bar icon whose menu sends the same notifications, updates its own
menu and tooltip from the callbacks, and quits on demand — no windows involved.

Both print which bundle their notifications come from and whether they are posting in-process, so you
can see which of the two paths is in use. Run either one like this and it is the helper path.

To exercise the other path, package the Avalonia sample as a real `.app` — the shape a menu-bar app
actually ships in:

```sh
samples/RumpSharp.TrayIcon.Sample/package.sh --run
```

That publishes the app, assembles `artifacts/RumpSharp Tray.app`, converts the sample's artwork into
an `.icns`, ad-hoc signs the bundle and verifies the signature. The packaged app reports
`in-process: True` and starts no helper. Running the executable _inside_ the bundle keeps your console
attached and still gives the process its bundle identity, which `open` would not.

## How it works

`NotificationCenter` is a facade over two interchangeable backends, chosen by `NotificationTransport`
and, by default, by whether the process has a bundle identity of its own:

| Piece                       | What it does                                                                                                                |
| --------------------------- | --------------------------------------------------------------------------------------------------------------------------- |
| `NotificationCenter.cs`     | the public API; picks a backend and re-raises its callbacks                                                                 |
| `Backends/INotificationBackend.cs` | the seam between the two, so which one is in use stays invisible through the public API                              |
| `Backends/ObjCBackend.cs`   | **in-process path**: `UNUserNotificationCenter` straight from .NET, for an app that already runs from a `.app` bundle       |
| `Backends/HelperBackend.cs` | **helper path**: keeps the bundle current, runs the helper, keeps action categories in step, restarts a crashed helper once |
| `Ipc/HelperProcess.cs`      | spawns the helper, writes NDJSON, reads stdout line by line, drains stderr into `Trace`                                     |
| `Ipc/HelperProtocol.cs`     | the C# half of the wire contract, with source-generated JSON for AOT                                                        |
| `AppBundle.cs`              | bundle generation, helper injection, icon conversion, ad-hoc signing                                                        |
| `Interop/ObjC.cs`           | `objc_msgSend` bindings, `NSString`/`NSDictionary` conversion, autorelease pools                                            |
| `Interop/Block.cs`          | Objective-C block ABI with copy/dispose helpers, so async completion handlers can call managed code safely                  |
| `Interop/DelegateClass.cs`  | builds a `UNUserNotificationCenterDelegate` class at runtime with `objc_allocateClassPair`                                  |
| `Interop/RunLoop.cs`        | `CFRunLoopRunInMode` pumping, the console-app equivalent of rumps' `NSApplication` loop                                     |
| `native/rumpsharp-helper/`  | the Swift helper: `NSApplication` accessory, `UNUserNotificationCenter` owner, NDJSON loop                                  |

### The helper

`~/Library/Application Support/RumpSharp/<Name>.app` contains exactly two files: `Info.plist` (plus
your `.icns`) and `Contents/MacOS/rumpsharp-helper`, a 320 KB universal (arm64 + x86_64) Swift binary
embedded in `RumpSharp.dll` as a gzipped manifest resource and unpacked on demand. It is only
rewritten when the bytes differ, because rewriting it would invalidate the code signature and the
LaunchServices registration that the notification permission is attached to.

Compressed, it adds 88 KB to `RumpSharp.dll` rather than 320 KB. That matters most for the case that
never touches it: an application which already ships as a `.app` posts in-process and never unpacks
the helper at all, so it should not have to carry it around at full size.

The host starts it **directly** — `Process.Start` on the binary inside the bundle — rather than
through `open`. That is enough for macOS to give the child the bundle's identity, and unlike
LaunchServices it leaves stdio connected, which is the whole point. The helper refuses to run unless
stdin is a pipe, so a notification click that makes LaunchServices launch the bundle after the host
is gone cannot leave an invisible process behind. It also exits when stdin reaches EOF, so it dies
with the host even if the host is killed.

The protocol is newline-delimited JSON, one object per line, requests correlated by `requestId`;
stdout is protocol only, stderr is diagnostics only.

```jsonc
// host → helper
{"type":"configure","requestId":1,"presentWhenForeground":true}
{"type":"categories","requestId":2,"categories":[{"id":"rumpsharp.6F…","actions":[{"id":"retry","title":"Retry","destructive":false,"foreground":true,"authenticationRequired":false}]}]}
{"type":"show","requestId":3,"show":{"id":"build-42","title":"Build finished","body":"42 tests passed","playSound":true,"categoryId":"rumpsharp.6F…"}}
{"type":"delivered","requestId":4}
{"type":"shutdown"}

// helper → host
{"type":"ready","bundleId":"com.example.mytool","bundlePath":"/…/My Tool.app"}
{"type":"result","requestId":3}
{"type":"result","requestId":4,"ids":["build-42"]}
{"type":"response","id":"build-42","action":"retry","activation":"action","title":"Build finished","userInfo":{}}
{"type":"error","context":"decode","message":"…"}
```

Category identifiers are an MD5 digest of the action buttons, derived identically on both paths, so
the buttons on notifications still sitting in Notification Center keep working across runs — and
across a move between backends.

### Rebuilding the helper

Only needed if you change the Swift sources; consumers of the NuGet package never build it.

```sh
native/rumpsharp-helper/build.sh   # needs the Xcode command line tools
```

That produces the universal release binary, strips it, ad-hoc signs it and copies it to
`src/RumpSharp/runtimes/osx/native/rumpsharp-helper`, which is committed. Keep `Protocol.swift` and
`Ipc/HelperProtocol.cs` in step: nothing at build time checks that they agree, which is why the wire
names are pinned by tests.

The build is tuned for size, because every byte lands in `RumpSharp.dll`:

| Step                                               | Size      |
| -------------------------------------------------- | --------- |
| `-O`, unstripped — the obvious build               | 556 KB    |
| `-Osize`, `-dead_strip`                            | 510 KB    |
| `strip` — an executable needs no symbol table      | 320 KB    |
| `gzip -9`, which is what is committed and embedded | **88 KB** |

`-Osize` costs nothing measurable for a process that spends its life blocked on a pipe, and
`__LINKEDIT` was larger than all the code put together. The Swift runtime is not in there at all:
macOS has shipped it in `/usr/lib/swift` since 10.14.4, so the helper links against it dynamically.

The committed artefact is the compressed one, so the bytes in the assembly are the bytes in the
repository, with no build step in between. To look at it:

```sh
gunzip -c src/RumpSharp/runtimes/osx/native/rumpsharp-helper.gz | file -
```

### Two details that cost real debugging time

- Blocks handed to asynchronous UserNotifications APIs **must** provide copy/dispose helpers, and the
  descriptor holding those helpers has to outlive every copy. `Block_copy` duplicates the block
  _literal_ but keeps the `descriptor` pointer verbatim, so `Block_release` reads the dispose helper
  back out of it long after the call returned — freeing the descriptor with the literal corrupts the
  heap on a dispatch worker thread. RumpSharp shares one immortal descriptor per signature.
- A bundle inside the per-user temporary directory (`/var/folders/…`) is refused outright: no prompt,
  and no way to grant permission afterwards. Keep `AppBundleOptions.Location` out of temporary
  storage — the default, `~/Library/Application Support/RumpSharp`, is fine.

## License

MIT
