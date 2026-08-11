using System.Runtime.Versioning;
using RumpSharp.Interop;

namespace RumpSharp.Backends;

/// <summary>
/// Talks to <c>UNUserNotificationCenter</c> in this process, for an application that already has a
/// <c>.app</c> bundle identity of its own.
/// </summary>
/// <remarks>
/// <para>
/// <c>UNUserNotificationCenter</c> is a process-wide singleton with a single delegate and a single
/// set of action categories, so this type keeps the corresponding state static: a second instance
/// takes ownership of the callbacks from the first, and <see cref="Dispose"/> only tears them down if
/// this instance is still the owner.
/// </para>
/// <para>
/// Callbacks are delivered to the main thread's run loop, which is why <see cref="ProcessEvents"/>
/// has to be pumped by whoever owns that thread.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed unsafe class ObjCBackend : INotificationBackend
{
    private const ulong AuthorizationBadgeSoundAlert = 1 | 2 | 4;

    /// <summary>
    /// How many distinct action sets stay registered at once. macOS wants the complete set of
    /// categories re-sent whenever one is added, so this has to be bounded: an application that
    /// builds action titles dynamically ("Retry (3 left)") would otherwise register a fresh category
    /// per notification and grow both the native set and the work per send without limit. Evicting
    /// the least recently used one only affects buttons on notifications delivered long ago that are
    /// still sitting in Notification Center.
    /// </summary>
    private const int MaxCategories = 64;

    /// <summary>
    /// Guards the state that mirrors the process-wide <c>UNUserNotificationCenter</c> singleton: the
    /// one delegate it accepts and the one set of categories it knows about.
    /// </summary>
    private static readonly Lock StaticGate = new();

    private static readonly HandleCache Categories = new(MaxCategories);

    /// <summary>The instance whose handlers are currently installed on the shared delegate.</summary>
    private static ObjCBackend? _installed;

    private readonly NotificationCenterOptions _options;
    private readonly IntPtr _center;
    private readonly Lock _gate = new();

    private bool _authorizationRequested;
    private bool _disposed;

    /// <summary>Connects to the notification center of this process.</summary>
    /// <param name="options">Behaviour settings.</param>
    /// <exception cref="PlatformNotSupportedException">macOS refused to hand out a notification center.</exception>
    internal ObjCBackend(NotificationCenterOptions options)
    {
        _options = options;

        using var pool = ObjC.Pool();

        if (_options.BecomeAccessoryApplication)
        {
            AppKitApplication.BecomeAccessory();
        }

        _center = ObjC.Send(Cls.UNUserNotificationCenter, Sel.CurrentNotificationCenter);
        if (_center == IntPtr.Zero)
        {
            throw new PlatformNotSupportedException("macOS did not provide a notification center for this process.");
        }

        InstallDelegate();
    }

    /// <inheritdoc />
    public Action<NotificationResponse>? Activated { get; set; }

    /// <inheritdoc />
    public string? LastAuthorizationError { get; private set; }

    /// <inheritdoc />
    /// <remarks>This backend posts as the application itself, so it is the application's own bundle.</remarks>
    public string? BundlePath => AppBundle.CurrentBundlePath;

    /// <inheritdoc />
    public AuthorizationStatus GetAuthorizationStatus()
    {
        using var pool = ObjC.Pool();
        var status = AuthorizationStatus.Unavailable;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var block = Block.Object(settings =>
        {
            if (settings != IntPtr.Zero)
            {
                status = (AuthorizationStatus)ObjC.SendLong(settings, Sel.AuthorizationStatus);
            }

            completed.TrySetResult();
        });

        return Await(
            block,
            completed.Task,
            _options.Timeout,
            handle => ObjC.SendVoid(_center, Sel.GetNotificationSettings, handle))
            ? status
            : AuthorizationStatus.Unavailable;
    }

    /// <inheritdoc />
    public bool RequestAuthorization(TimeSpan? timeout)
    {
        using var pool = ObjC.Pool();
        var granted = false;
        string? error = null;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var block = Block.BoolError((allowed, message) =>
        {
            granted = allowed;
            error = message;
            completed.TrySetResult();
        });

        if (!Await(
            block,
            completed.Task,
            timeout ?? System.Threading.Timeout.InfiniteTimeSpan,
            handle => ObjC.SendVoid(_center, Sel.RequestAuthorization, AuthorizationBadgeSoundAlert, handle)))
        {
            return false;
        }

        lock (_gate)
        {
            _authorizationRequested = true;
        }

        if (!granted && error is not null)
        {
            // A denial is a normal outcome, not an exception - but make the reason discoverable.
            LastAuthorizationError = error;
        }

        return granted;
    }

    /// <inheritdoc />
    /// <exception cref="NotificationException">macOS refused the notification.</exception>
    public void Show(Notification notification)
    {
        EnsureAuthorized();

        using var pool = ObjC.Pool();

        // UNMutableNotificationContent comes from alloc/init, so we own it and must release it even
        // if macOS rejects the request.
        var content = BuildContent(notification);
        try
        {
            var trigger = BuildTrigger(notification);
            var request = ObjC.Send(
                Cls.UNNotificationRequest,
                Sel.RequestWithIdentifier,
                ObjC.NSString(notification.Identifier),
                content,
                trigger);

            string? error = null;
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var block = Block.Error(message =>
            {
                error = message;
                completed.TrySetResult();
            });

            if (!Await(
                block,
                completed.Task,
                _options.Timeout,
                handle => ObjC.SendVoid(_center, Sel.AddNotificationRequest, request, handle)))
            {
                throw new NotificationException("macOS did not respond while delivering the notification.");
            }

            if (error is not null)
            {
                throw new NotificationException(error);
            }
        }
        finally
        {
            ObjC.SendVoid(content, Sel.Release);
        }
    }

    /// <inheritdoc />
    public bool ProcessEvents(TimeSpan timeout) => RunLoop.Pump(timeout);

    /// <inheritdoc />
    public IReadOnlyList<string> GetDeliveredIdentifiers()
    {
        using var pool = ObjC.Pool();

        // Published as a whole, so a late callback can never mutate a list the caller is reading.
        List<string>? delivered = null;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var block = Block.Object(array =>
        {
            var identifiers = new List<string>();
            var count = (ulong)ObjC.SendLong(array, Sel.Count);
            for (ulong i = 0; i < count; i++)
            {
                var notification = ObjC.SendULong(array, Sel.ObjectAtIndex, i);
                var request = ObjC.Send(notification, Sel.Request);
                if (ObjC.FromNSString(ObjC.Send(request, Sel.Identifier)) is { } identifier)
                {
                    identifiers.Add(identifier);
                }
            }

            delivered = identifiers;
            completed.TrySetResult();
        });

        return Await(block, completed.Task, _options.Timeout, handle => ObjC.SendVoid(_center, Sel.GetDelivered, handle))
            && delivered is { } result
            ? result
            : [];
    }

    /// <inheritdoc />
    public void RemoveDelivered(string[] identifiers)
    {
        using var pool = ObjC.Pool();
        ObjC.SendVoid(_center, Sel.RemoveDelivered, StringArray(identifiers));
    }

    /// <inheritdoc />
    public void RemoveAllDelivered()
    {
        using var pool = ObjC.Pool();
        ObjC.SendVoid(_center, Sel.RemoveAllDelivered);
    }

    /// <inheritdoc />
    public void RemoveAllPending()
    {
        using var pool = ObjC.Pool();
        ObjC.SendVoid(_center, Sel.RemoveAllPending);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Detaches this instance from the shared Objective-C delegate, but only if no later instance has
    /// taken it over in the meantime. Registered action categories are process-wide and deliberately
    /// left in place, so a notification that is still on screen keeps working.
    /// </remarks>
    public void Dispose()
    {
        lock (StaticGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_installed == this)
            {
                _installed = null;
                DelegateClass.DidReceiveResponse = null;
                DelegateClass.WillPresent = null;
            }
        }

        Activated = null;
    }

    // ------------------------------------------------------------------ internals

    /// <summary>
    /// Points the single Objective-C delegate macOS allows at this instance. Any previous owner stops
    /// receiving callbacks.
    /// </summary>
    private void InstallDelegate()
    {
        lock (StaticGate)
        {
            _installed = this;

            DelegateClass.WillPresent = _ => _options.PresentWhenForeground
                ? (ulong)(NotificationPresentationOptions.Banner | NotificationPresentationOptions.List | NotificationPresentationOptions.Sound)
                : (ulong)NotificationPresentationOptions.List;

            DelegateClass.DidReceiveResponse = HandleResponse;
        }

        var @delegate = DelegateClass.Instance();
        if (@delegate != IntPtr.Zero)
        {
            ObjC.SendVoid(_center, Sel.SetDelegate, @delegate);
        }
    }

    /// <summary>
    /// Hands a completion block to macOS and waits for the callback, giving up after
    /// <paramref name="timeout"/>.
    /// </summary>
    /// <remarks>
    /// The block is only released once the callback has actually arrived. When we give up, macOS may
    /// still invoke it later, so the literal and its captured delegate are deliberately leaked rather
    /// than freed underneath a pending call - 40 bytes on an abnormal path.
    /// </remarks>
    /// <returns><see langword="true"/> if the callback arrived in time.</returns>
    private static bool Await(Block.Scope block, Task completed, TimeSpan timeout, Action<IntPtr> send)
    {
        send(block.Handle);

        if (!completed.Wait(timeout))
        {
            return false;
        }

        block.Dispose();
        return true;
    }

    /// <summary>
    /// Asks for permission before the first send, when
    /// <see cref="NotificationCenterOptions.RequestAuthorizationOnDemand"/> is set.
    /// </summary>
    /// <remarks>
    /// Requesting is cheap after the first time: macOS only shows the prompt once per bundle identifier
    /// and answers from its database afterwards. The wait is bounded so an unattended process cannot
    /// hang on a dialog nobody is going to click - and the attempt is recorded <em>before</em> the call
    /// rather than on success, so a prompt that timed out costs that wait once instead of stalling
    /// every later send for the same two minutes.
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

    private IntPtr BuildContent(Notification notification)
    {
        var content = ObjC.New(Cls.UNMutableNotificationContent);

        ObjC.SendVoid(content, Sel.SetTitle, ObjC.NSString(notification.Title));
        if (notification.Subtitle is not null)
        {
            ObjC.SendVoid(content, Sel.SetSubtitle, ObjC.NSString(notification.Subtitle));
        }

        if (notification.Body is not null)
        {
            ObjC.SendVoid(content, Sel.SetBody, ObjC.NSString(notification.Body));
        }

        if (notification.PlaySound)
        {
            var sound = notification.SoundName is null
                ? ObjC.Send(Cls.UNNotificationSound, Sel.DefaultSound)
                : ObjC.Send(Cls.UNNotificationSound, Sel.SoundNamed, ObjC.NSString(notification.SoundName));
            ObjC.SendVoid(content, Sel.SetSound, sound);
        }

        if (notification.ThreadIdentifier is not null)
        {
            ObjC.SendVoid(content, Sel.SetThreadIdentifier, ObjC.NSString(notification.ThreadIdentifier));
        }

        if (notification.BadgeCount is { } badge)
        {
            ObjC.SendVoid(content, Sel.SetBadge, ObjC.SendInt(Cls.NSNumber, Sel.NumberWithInt, badge));
        }

        if (notification.UserInfo.Count > 0)
        {
            ObjC.SendVoid(content, Sel.SetUserInfo, ObjC.NSDictionary(new Dictionary<string, string>(notification.UserInfo)));
        }

        try
        {
            if (notification.ImagePath is { } imagePath)
            {
                AttachImage(content, imagePath);
            }

            if (notification.Actions.Count > 0)
            {
                ObjC.SendVoid(content, Sel.SetCategoryIdentifier, ObjC.NSString(RegisterCategory(notification.Actions)));
            }
        }
        catch
        {
            ObjC.SendVoid(content, Sel.Release);
            throw;
        }

        return content;
    }

    private static void AttachImage(IntPtr content, string imagePath)
    {
        var copy = NotificationImage.CopyForDelivery(imagePath);

        var url = ObjC.Send(Cls.NSURL, Sel.FileUrlWithPath, ObjC.NSString(copy));
        var error = IntPtr.Zero;
        var attachment = ObjC.Send(
            Cls.UNNotificationAttachment,
            Sel.AttachmentWithIdentifier,
            ObjC.NSString("image"),
            url,
            IntPtr.Zero,
            (IntPtr)(&error));

        if (attachment == IntPtr.Zero)
        {
            var message = error == IntPtr.Zero
                ? "unknown error"
                : ObjC.FromNSString(ObjC.Send(error, Sel.LocalizedDescription)) ?? "unknown error";
            throw new NotificationException($"macOS rejected the notification image '{imagePath}': {message}");
        }

        var attachments = ObjC.SendPtrCount(Cls.NSArray, Sel.ArrayWithObjects, (IntPtr)(&attachment), 1);
        ObjC.SendVoid(content, Sel.SetAttachments, attachments);
    }

    private static IntPtr BuildTrigger(Notification notification)
    {
        if (notification.Delay is not { } delay || delay <= TimeSpan.Zero)
        {
            return IntPtr.Zero;
        }

        return ObjC.SendDoubleBool(
            Cls.UNTimeIntervalNotificationTrigger,
            Sel.TriggerWithTimeInterval,
            delay.TotalSeconds,
            false);
    }

    /// <summary>
    /// Action buttons are only shown if their category was registered with the notification center
    /// first, so build one category per distinct set of actions and register the accumulated set.
    /// </summary>
    private string RegisterCategory(IList<NotificationAction> actions)
    {
        var identifier = NotificationCenter.CategoryIdentifier(actions);

        lock (StaticGate)
        {
            if (Categories.TryTouch(identifier))
            {
                return identifier;
            }

            var evicted = Categories.Add(identifier, CreateCategory(identifier, actions));
            SendCategories();

            // Released only once the replacement set is in place: doing it earlier could deallocate a
            // category macOS is still referencing through the set we sent last time.
            if (evicted != IntPtr.Zero)
            {
                ObjC.SendVoid(evicted, Sel.Release);
            }

            return identifier;
        }
    }

    /// <summary>Builds a retained <c>UNNotificationCategory</c> for a set of actions.</summary>
    private static IntPtr CreateCategory(string identifier, IList<NotificationAction> actions)
    {
        var handles = new IntPtr[actions.Count];
        for (var i = 0; i < actions.Count; i++)
        {
            handles[i] = BuildAction(actions[i]);
        }

        IntPtr category;
        fixed (IntPtr* items = handles)
        {
            var actionArray = ObjC.SendPtrCount(Cls.NSArray, Sel.ArrayWithObjects, (IntPtr)items, (nuint)handles.Length);
            category = ObjC.SendPtrPtrPtrULong(
                Cls.UNNotificationCategory,
                Sel.CategoryWithIdentifier,
                ObjC.NSString(identifier),
                actionArray,
                ObjC.Send(Cls.NSArray, Sel.Array),
                0);
        }

        // Retained for as long as it stays registered: the whole set has to be re-sent every time a
        // new category shows up.
        return ObjC.Send(category, Sel.Retain);
    }

    /// <summary>Re-sends the complete set of registered categories, which is what macOS expects.</summary>
    private void SendCategories()
    {
        var all = Categories.ToArray();
        fixed (IntPtr* items = all)
        {
            var array = ObjC.SendPtrCount(Cls.NSArray, Sel.ArrayWithObjects, (IntPtr)items, (nuint)all.Length);
            var set = ObjC.Send(Cls.NSSet, Sel.SetWithArray, array);
            ObjC.SendVoid(_center, Sel.SetCategories, set);
        }
    }

    private static IntPtr BuildAction(NotificationAction action)
    {
        // UNNotificationActionOptions: authenticationRequired(1), destructive(2), foreground(4)
        var options = 0UL;
        if (action.RequiresAuthentication)
        {
            options |= 1;
        }

        if (action.IsDestructive)
        {
            options |= 2;
        }

        if (action.ActivatesApplication)
        {
            options |= 4;
        }

        if (action.TextInput is { } input)
        {
            return ObjC.SendPtrPtrULongPtrPtr(
                Cls.UNTextInputNotificationAction,
                Sel.TextActionWithIdentifier,
                ObjC.NSString(action.Identifier),
                ObjC.NSString(action.Title),
                options,
                ObjC.NSString(input.SendButtonTitle),
                ObjC.NSString(input.Placeholder));
        }

        return ObjC.SendPtrPtrULong(
            Cls.UNNotificationAction,
            Sel.ActionWithIdentifier,
            ObjC.NSString(action.Identifier),
            ObjC.NSString(action.Title),
            options);
    }

    private void HandleResponse(IntPtr response)
    {
        var handler = Activated;
        if (_disposed || handler is null || response == IntPtr.Zero)
        {
            return;
        }

        using var pool = ObjC.Pool();

        var actionIdentifier = ObjC.FromNSString(ObjC.Send(response, Sel.ActionIdentifier)) ?? string.Empty;
        var notification = ObjC.Send(response, Sel.Notification);
        var request = ObjC.Send(notification, Sel.Request);
        var content = ObjC.Send(request, Sel.Content);

        var activation = actionIdentifier == ActionIdentifiers.Default
            ? NotificationActivation.Default
            : actionIdentifier == ActionIdentifiers.Dismiss
                ? NotificationActivation.Dismissed
                : NotificationActivation.Action;

        // userText only exists on UNTextInputNotificationResponse, i.e. for reply actions.
        var userText = ObjC.RespondsTo(response, Sel.UserText)
            ? ObjC.FromNSString(ObjC.Send(response, Sel.UserText))
            : null;

        handler(new NotificationResponse(
            ObjC.FromNSString(ObjC.Send(request, Sel.Identifier)) ?? string.Empty,
            actionIdentifier,
            activation,
            ObjC.FromNSString(ObjC.Send(content, Sel.Title)) ?? string.Empty,
            ObjC.FromNSString(ObjC.Send(content, Sel.Subtitle)),
            ObjC.FromNSString(ObjC.Send(content, Sel.Body)),
            userText,
            ReadUserInfo(ObjC.Send(content, Sel.UserInfo))));
    }

    private static IReadOnlyDictionary<string, string> ReadUserInfo(IntPtr dictionary)
    {
        var result = new Dictionary<string, string>();
        if (dictionary == IntPtr.Zero)
        {
            return result;
        }

        var keys = ObjC.Send(dictionary, Sel.AllKeys);
        var count = (ulong)ObjC.SendLong(keys, Sel.Count);
        for (ulong i = 0; i < count; i++)
        {
            var key = ObjC.SendULong(keys, Sel.ObjectAtIndex, i);
            if (ObjC.FromNSString(key) is not { } name)
            {
                continue;
            }

            if (ObjC.FromNSString(ObjC.Send(dictionary, Sel.ObjectForKey, key)) is { } value)
            {
                result[name] = value;
            }
        }

        return result;
    }

    private static IntPtr StringArray(string[] values)
    {
        var handles = new IntPtr[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            handles[i] = ObjC.NSString(values[i]);
        }

        fixed (IntPtr* items = handles)
        {
            return ObjC.SendPtrCount(Cls.NSArray, Sel.ArrayWithObjects, (IntPtr)items, (nuint)handles.Length);
        }
    }

}
