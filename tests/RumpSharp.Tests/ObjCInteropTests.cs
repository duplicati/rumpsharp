using RumpSharp.Interop;
using Xunit;

namespace RumpSharp.Tests;

/// <summary>Covers the hand-rolled Objective-C runtime bindings.</summary>
public sealed class ObjCInteropTests
{
    [Theory]
    [InlineData("plain ascii")]
    [InlineData("größenwahnsinnig — ünïcödé ✓ 🎉")]
    [InlineData("   ")]
    [InlineData("a")]
    public void StringsRoundTripThroughNSString(string value)
    {
        using var pool = ObjC.Pool();

        var handle = ObjC.NSString(value);

        Assert.NotEqual(IntPtr.Zero, handle);
        Assert.Equal(value, ObjC.FromNSString(handle));
    }

    /// <summary>
    /// An empty string is not a corner case: it is the default reply placeholder, and it reaches here
    /// from any empty body or subtitle. <c>fixed</c> over an empty array yields a null pointer, and
    /// <c>stringWithBytes:length:encoding:</c> answers a null one with an Objective-C exception - which
    /// aborts the process instead of surfacing as a .NET exception.
    /// </summary>
    [Fact]
    public void EmptyStringsDoNotAbortTheProcess()
    {
        using var pool = ObjC.Pool();

        var handle = ObjC.NSString(string.Empty);

        Assert.NotEqual(IntPtr.Zero, handle);
        Assert.Equal(string.Empty, ObjC.FromNSString(handle));
    }

    [Fact]
    public void EveryDefaultReplyPlaceholderIsSurvivable()
    {
        using var pool = ObjC.Pool();
        var action = NotificationAction.Reply("comment", "Comment");

        Assert.Equal(string.Empty, action.TextInput!.Placeholder);
        Assert.NotEqual(IntPtr.Zero, ObjC.NSString(action.TextInput.Placeholder));
    }

    /// <summary>
    /// Encoding tolerates an embedded NUL, but reading back goes through <c>UTF8String</c> and stops
    /// there. Documented rather than fixed: notification text does not contain NULs.
    /// </summary>
    [Fact]
    public void EmbeddedNulsAreTruncatedOnTheWayBack()
    {
        using var pool = ObjC.Pool();

        var handle = ObjC.NSString("embedded \0 nul");

        Assert.NotEqual(IntPtr.Zero, handle);
        Assert.Equal("embedded ", ObjC.FromNSString(handle));
    }

    [Fact]
    public void NullStringsMapToNil()
    {
        using var pool = ObjC.Pool();

        Assert.Equal(IntPtr.Zero, ObjC.NSString(null));
        Assert.Null(ObjC.FromNSString(IntPtr.Zero));
    }

    [Fact]
    public void DictionariesRoundTrip()
    {
        using var pool = ObjC.Pool();

        var dictionary = ObjC.NSDictionary(new Dictionary<string, string>
        {
            ["release"] = "v1.4.0",
            ["commit"] = "9f2ac41",
        });

        Assert.NotEqual(IntPtr.Zero, dictionary);
        Assert.Equal(2, (int)ObjC.SendLong(dictionary, Sel.Count));
        Assert.Equal("v1.4.0", ObjC.FromNSString(ObjC.Send(dictionary, Sel.ObjectForKey, ObjC.NSString("release"))));
        Assert.Equal("9f2ac41", ObjC.FromNSString(ObjC.Send(dictionary, Sel.ObjectForKey, ObjC.NSString("commit"))));
    }

    [Fact]
    public void RespondsToDistinguishesImplementedSelectors()
    {
        using var pool = ObjC.Pool();

        var text = ObjC.NSString("hello");

        Assert.True(ObjC.RespondsTo(text, Sel.UTF8String));
        Assert.False(ObjC.RespondsTo(text, Sel.UserText));
        Assert.False(ObjC.RespondsTo(IntPtr.Zero, Sel.UTF8String));
    }

    [Fact]
    public void EmptyArraysCanBeBuilt()
    {
        using var pool = ObjC.Pool();

        var empty = ObjC.Send(Cls.NSArray, Sel.Array);

        Assert.NotEqual(IntPtr.Zero, empty);
        Assert.Equal(0, (int)ObjC.SendLong(empty, Sel.Count));
    }

    /// <summary>
    /// The runtime-built delegate class is what receives clicks, action presses and dismissals.
    /// </summary>
    [Fact]
    public void DelegateClassIsBuiltOnceAndImplementsBothCallbacks()
    {
        var instance = DelegateClass.Instance();

        Assert.NotEqual(IntPtr.Zero, instance);
        Assert.Equal(instance, DelegateClass.Instance());
        Assert.NotEqual(IntPtr.Zero, ObjC.LookUpClass("RumpSharpNotificationDelegate"));
        Assert.True(ObjC.RespondsTo(instance, Sel.WillPresent));
        Assert.True(ObjC.RespondsTo(instance, Sel.DidReceiveResponse));
    }

    [Fact]
    public void RequiredRuntimeSymbolsAreResolvable()
    {
        Assert.NotEqual(IntPtr.Zero, Native.DlSym(Native.RtldDefault, "objc_msgSend"));
        Assert.NotEqual(IntPtr.Zero, Native.DlSym(Native.RtldDefault, "_NSConcreteStackBlock"));
    }

    /// <summary>
    /// The click and dismiss identifiers have to come from the framework's exported constants. Reading
    /// them in a static field initializer on <see cref="NotificationCenter"/> ran before the framework
    /// had been loaded, so the lookup always failed and fell back to a hard-coded literal - which
    /// happened to be correct, and would have gone on being silently wrong if it ever stopped being.
    /// </summary>
    [Fact]
    public void ActionIdentifiersComeFromTheFrameworkConstants()
    {
        using var pool = ObjC.Pool();

        Assert.True(Cls.HasUserNotifications);

        var expectedDefault = Native.StringConstant("UNNotificationDefaultActionIdentifier");
        var expectedDismiss = Native.StringConstant("UNNotificationDismissActionIdentifier");

        Assert.NotNull(expectedDefault);
        Assert.NotNull(expectedDismiss);
        Assert.Equal(expectedDefault, ActionIdentifiers.Default);
        Assert.Equal(expectedDismiss, ActionIdentifiers.Dismiss);
        Assert.NotEqual(ActionIdentifiers.Default, ActionIdentifiers.Dismiss);
    }

    [Fact]
    public void ActionIdentifiersAreCachedAfterTheFirstRead()
    {
        Assert.Same(ActionIdentifiers.Default, ActionIdentifiers.Default);
        Assert.Same(ActionIdentifiers.Dismiss, ActionIdentifiers.Dismiss);
    }

    /// <summary>
    /// <c>dlopen</c> is reference counted, so AppKit has to be loaded once and cached rather than on
    /// every access.
    /// </summary>
    [Fact]
    public void AppKitIsResolvedOnceAndCached()
    {
        var first = Cls.NSApplication;

        Assert.NotEqual(IntPtr.Zero, first);
        Assert.Equal(first, Cls.NSApplication);
        Assert.Equal(first, ObjC.GetClass("NSApplication"));
    }
}
