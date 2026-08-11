using System.Diagnostics;
using System.Runtime.Versioning;
using RumpSharp.Interop;
using RumpSharp.Ipc;

namespace RumpSharp.Backends;

/// <summary>
/// Posts notifications through the <c>rumpsharp-helper</c> process inside a generated <c>.app</c>
/// bundle: the path for a host with no bundle identity of its own, and for one that asked for
/// <see cref="NotificationTransport.Helper"/> to post under a different name.
/// </summary>
/// <remarks>
/// <para>
/// The helper is a long-lived child process: it owns <c>UNUserNotificationCenter</c>, runs the main
/// run loop that notification callbacks need, and is the application macOS attributes the
/// notifications to. This type is the client half - it keeps the bundle up to date, keeps the action
/// categories in step, and turns the helper's <c>response</c> messages back into
/// <see cref="NotificationResponse"/> objects.
/// </para>
/// <para>
/// The helper dying is not fatal: the next request restarts it once and replays the state it needs
/// (the presentation setting and the registered categories). A second failure is reported to the
/// caller as a <see cref="NotificationException"/>.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class HelperBackend : INotificationBackend
{
    /// <summary>
    /// How many distinct action sets stay registered at once, matching the in-process backend. macOS
    /// replaces the whole set every time, so an application that builds action titles dynamically
    /// ("Retry (3 left)") must not be able to grow it without bound.
    /// </summary>
    private const int MaxCategories = 64;

    private readonly NotificationCenterOptions _options;
    private readonly string _bundlePath;

    /// <summary>The registered categories, oldest first, and the payload to re-send with each change.</summary>
    private readonly Dictionary<string, HelperCategory> _categories = new(StringComparer.Ordinal);
    private readonly List<string> _categoryOrder = [];
    private readonly Lock _categoryGate = new();

    private readonly Lock _gate = new();

    /// <summary>Counts responses so <see cref="ProcessEvents"/> can report that one arrived.</summary>
    private readonly SemaphoreSlim _responses = new(0);

    private HelperProcess _helper;
    private bool _authorizationRequested;
    private bool _restarted;
    private bool _disposed;

    /// <summary>Creates the bundle if needed and starts the helper inside it.</summary>
    /// <param name="options">Behaviour settings.</param>
    /// <exception cref="NotificationException">The helper could not be started.</exception>
    internal HelperBackend(NotificationCenterOptions options)
    {
        _options = options;

        // An explicit Bundle wins, which is how a caller that asked for this backend on purpose names
        // the notifications. Otherwise AppBundle.PrepareIfNeeded has usually built one already, with the
        // caller's name and icon, and the defaults cover forgetting to call it at all.
        _bundlePath = options.Bundle is { } bundle
            ? AppBundle.Create(bundle)
            : AppBundle.PrepareDefault();

        // Notifications do not need this - the helper is the application macOS sees - but a menu-bar
        // application does: without a bundle of its own, a UI framework will have put this process in
        // the Dock and the app switcher. Nothing happens in a plain console process, which has no
        // NSApplication and must not be given one.
        //
        // An application that has its own bundle is left alone. It only reaches this backend by asking
        // for NotificationTransport.Helper, and taking away a real application's Dock icon is not
        // something that choosing where notifications come from should do - its activation policy is
        // settled by its Info.plist and its UI framework, and is none of RumpSharp's business.
        if (_options.BecomeAccessoryApplication && !AppBundle.IsBundled)
        {
            AppKitApplication.BecomeAccessoryIfPresent();
        }

        _helper = Launch();
    }

    /// <inheritdoc />
    public Action<NotificationResponse>? Activated { get; set; }

    /// <inheritdoc />
    public string? LastAuthorizationError { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    /// The generated bundle the helper runs from, which is not the host application's own bundle even
    /// when it has one.
    /// </remarks>
    public string? BundlePath => _bundlePath;

    /// <summary>The identity macOS gave the helper, as reported by its <c>ready</c> message.</summary>
    internal string? BundleIdentifier { get; private set; }

    /// <inheritdoc />
    public AuthorizationStatus GetAuthorizationStatus()
    {
        var answer = Exchange(
            new HelperRequest { Type = HelperProtocol.Requests.AuthorizationStatus },
            _options.Timeout);

        return answer is null ? AuthorizationStatus.Unavailable : HelperProtocol.ToAuthorizationStatus(answer.Status);
    }

    /// <inheritdoc />
    public bool RequestAuthorization(TimeSpan? timeout)
    {
        var answer = Exchange(
            new HelperRequest { Type = HelperProtocol.Requests.RequestAuthorization },
            timeout ?? System.Threading.Timeout.InfiniteTimeSpan);

        if (answer is null)
        {
            return false;
        }

        lock (_gate)
        {
            _authorizationRequested = true;
        }

        var granted = answer.Granted ?? false;
        if (!granted)
        {
            // A denial is a normal outcome, not an exception - but make the reason discoverable.
            LastAuthorizationError = answer.Error
                ?? $"macOS reports the notification permission as '{HelperProtocol.ToAuthorizationStatus(answer.Status)}'.";
        }

        return granted;
    }

    /// <inheritdoc />
    /// <exception cref="NotificationException">macOS refused the notification.</exception>
    public void Show(Notification notification)
    {
        EnsureAuthorized();

        var request = new HelperRequest
        {
            Type = HelperProtocol.Requests.Show,
            Show = BuildShow(notification),
        };

        var answer = Exchange(request, _options.Timeout)
            ?? throw new NotificationException("The notification helper did not respond while delivering the notification.");

        if (answer.Error is { } error)
        {
            throw new NotificationException(error);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Unlike the in-process backend this does not pump a run loop: the helper owns the run loop, and
    /// responses arrive over the pipe. The wait is what makes a polling loop behave the same way.
    /// </remarks>
    public bool ProcessEvents(TimeSpan timeout) => _responses.Wait(timeout);

    /// <inheritdoc />
    public IReadOnlyList<string> GetDeliveredIdentifiers()
    {
        var answer = Exchange(new HelperRequest { Type = HelperProtocol.Requests.Delivered }, _options.Timeout);
        return answer?.Ids ?? (IReadOnlyList<string>)[];
    }

    /// <inheritdoc />
    public void RemoveDelivered(string[] identifiers) =>
        Exchange(
            new HelperRequest { Type = HelperProtocol.Requests.RemoveDelivered, Ids = [.. identifiers] },
            _options.Timeout);

    /// <inheritdoc />
    public void RemoveAllDelivered() =>
        Exchange(new HelperRequest { Type = HelperProtocol.Requests.RemoveAllDelivered }, _options.Timeout);

    /// <inheritdoc />
    public void RemoveAllPending() =>
        Exchange(new HelperRequest { Type = HelperProtocol.Requests.RemoveAllPending }, _options.Timeout);

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Current().Dispose();
        _responses.Dispose();
        Activated = null;
    }

    // ------------------------------------------------------------------ helper lifecycle

    /// <summary>Starts a helper and brings it up to date with the state it has to know about.</summary>
    private HelperProcess Launch()
    {
        var helper = new HelperProcess(AppBundle.HelperPath(_bundlePath), Report);
        helper.ResponseReceived += OnResponse;

        var ready = helper.Start();
        BundleIdentifier = ready.BundleId;

        if (ready.BundleId is null)
        {
            helper.Dispose();
            throw new NotificationException(
                $"The notification helper started from '{_bundlePath}' but macOS gave it no bundle identity, "
                + "so it cannot post notifications.");
        }

        helper.Post(new HelperRequest
        {
            Type = HelperProtocol.Requests.Configure,
            PresentWhenForeground = _options.PresentWhenForeground,
        });

        return helper;
    }

    /// <summary>Sends a request, restarting a dead helper once before giving up.</summary>
    /// <returns>The answer, or <see langword="null"/> if it did not arrive in time.</returns>
    /// <exception cref="NotificationException">The helper is gone and could not be replaced.</exception>
    private HelperMessage? Exchange(HelperRequest request, TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            return Current().Request(request, timeout);
        }
        catch (NotificationException)
        {
            if (!Restart())
            {
                throw;
            }

            return Current().Request(request, timeout);
        }
    }

    /// <summary>Replaces a helper that has died, at most once for the life of this instance.</summary>
    /// <returns>Whether a fresh helper is now running.</returns>
    /// <exception cref="NotificationException">The replacement could not be started either.</exception>
    private bool Restart()
    {
        HelperProcess previous;
        lock (_gate)
        {
            // A helper that is still running did not cause the failure, and a second crash is the
            // caller's problem: retrying forever would just hide a helper that cannot run at all.
            if (_disposed || _restarted || _helper.IsRunning)
            {
                return false;
            }

            _restarted = true;
            previous = _helper;
        }

        Report("the notification helper is gone; starting a replacement");

        previous.ResponseReceived -= OnResponse;
        previous.Dispose();

        var replacement = Launch();
        lock (_gate)
        {
            _helper = replacement;
        }

        ResendCategories();
        return true;
    }

    /// <summary>The helper to talk to right now, which a restart may have replaced.</summary>
    private HelperProcess Current()
    {
        lock (_gate)
        {
            return _helper;
        }
    }

    private void OnResponse(HelperMessage message)
    {
        var handler = Activated;
        if (handler is not null)
        {
            handler(new NotificationResponse(
                message.Id ?? string.Empty,
                message.Action ?? string.Empty,
                HelperProtocol.ToActivation(message.Activation),
                message.Title ?? string.Empty,
                message.Subtitle,
                message.Body,
                message.UserText,
                message.UserInfo ?? []));
        }

        // Published after the handler has run, so ProcessEvents reporting "one was handled" is true.
        try
        {
            _responses.Release();
        }
        catch (ObjectDisposedException)
        {
            // Disposed while a response was in flight.
        }
    }

    // ------------------------------------------------------------------ requests

    /// <summary>
    /// Asks for permission before the first send, when
    /// <see cref="NotificationCenterOptions.RequestAuthorizationOnDemand"/> is set.
    /// </summary>
    /// <remarks>
    /// The attempt is recorded <em>before</em> the call rather than on success, so a prompt that timed
    /// out costs that wait once instead of stalling every later send for the same two minutes.
    /// </remarks>
    private void EnsureAuthorized()
    {
        lock (_gate)
        {
            if (!_options.RequestAuthorizationOnDemand || _authorizationRequested)
            {
                return;
            }

            _authorizationRequested = true;
        }

        RequestAuthorization(_options.AuthorizationPromptTimeout);
    }

    private HelperShow BuildShow(Notification notification) => new()
    {
        Id = notification.Identifier,
        Title = notification.Title,
        Subtitle = notification.Subtitle,
        Body = notification.Body,
        PlaySound = notification.PlaySound,
        SoundName = notification.SoundName,

        // macOS moves attachment files into its own store, so it never sees the caller's original.
        ImagePath = notification.ImagePath is { } image ? NotificationImage.CopyForDelivery(image) : null,
        ThreadIdentifier = notification.ThreadIdentifier,
        Badge = notification.BadgeCount,
        DelaySeconds = notification.Delay is { } delay && delay > TimeSpan.Zero ? delay.TotalSeconds : null,
        UserInfo = notification.UserInfo.Count > 0 ? new Dictionary<string, string>(notification.UserInfo) : null,
        CategoryId = notification.Actions.Count > 0 ? RegisterCategory(notification.Actions) : null,
    };

    /// <summary>
    /// Makes sure the helper knows the category for a set of action buttons, and returns its
    /// identifier.
    /// </summary>
    /// <remarks>
    /// The identifier is derived from the buttons themselves, so a later run of the application
    /// derives the same one and the buttons on notifications still sitting in Notification Center keep
    /// working. The set is bounded and least-recently-used, and the complete set is re-sent whenever
    /// it changes because that is the only thing macOS accepts.
    /// </remarks>
    private string RegisterCategory(IList<NotificationAction> actions)
    {
        var identifier = NotificationCenter.CategoryIdentifier(actions);

        lock (_categoryGate)
        {
            if (_categories.ContainsKey(identifier))
            {
                _categoryOrder.Remove(identifier);
                _categoryOrder.Add(identifier);
                return identifier;
            }

            if (_categories.Count >= MaxCategories)
            {
                _categories.Remove(_categoryOrder[0]);
                _categoryOrder.RemoveAt(0);
            }

            _categories[identifier] = new HelperCategory
            {
                Id = identifier,
                Actions = [.. actions.Select(ToHelperAction)],
            };

            _categoryOrder.Add(identifier);
        }

        ResendCategories();
        return identifier;
    }

    private void ResendCategories()
    {
        List<HelperCategory> categories;
        lock (_categoryGate)
        {
            if (_categories.Count == 0)
            {
                return;
            }

            categories = [.. _categories.Values];
        }

        // A failure here would surface again on the send that follows, which is the one the caller is
        // waiting on; buttons missing is better than an exception from a bookkeeping call.
        var answer = Exchange(
            new HelperRequest { Type = HelperProtocol.Requests.Categories, Categories = categories },
            _options.Timeout);

        if (answer is null)
        {
            Report("the helper did not confirm the action categories in time");
        }
    }

    private static HelperAction ToHelperAction(NotificationAction action) => new()
    {
        Id = action.Identifier,
        Title = action.Title,
        Destructive = action.IsDestructive,
        Foreground = action.ActivatesApplication,
        AuthenticationRequired = action.RequiresAuthentication,
        TextInput = action.TextInput is { } input
            ? new HelperTextInput { ButtonTitle = input.SendButtonTitle, Placeholder = input.Placeholder }
            : null,
    };

    /// <summary>
    /// Reports something the helper said, or something that went wrong with it.
    /// </summary>
    /// <remarks>
    /// Trace rather than the console: a library has no business writing to a console application's
    /// output, and these messages are diagnostics for whoever is debugging the helper.
    /// </remarks>
    private static void Report(string message) => Trace.WriteLine($"RumpSharp: {message}");
}
