using Xunit;

namespace RumpSharp.Tests;

/// <summary>Covers the public model types.</summary>
public sealed class NotificationModelTests
{
    [Fact]
    public void DefaultsMatchWhatIsDocumented()
    {
        var notification = new Notification();

        Assert.Equal(string.Empty, notification.Title);
        Assert.Null(notification.Subtitle);
        Assert.Null(notification.Body);
        Assert.True(notification.PlaySound);
        Assert.Null(notification.SoundName);
        Assert.Null(notification.Delay);
        Assert.Null(notification.BadgeCount);
        Assert.Empty(notification.Actions);
        Assert.Empty(notification.UserInfo);
        Assert.False(string.IsNullOrWhiteSpace(notification.Identifier));
    }

    [Fact]
    public void IdentifiersAreUniquePerNotification() =>
        Assert.NotEqual(new Notification().Identifier, new Notification().Identifier);

    [Fact]
    public void ConstructorAssignsTheText()
    {
        var notification = new Notification("Build finished", "MyProject", "42 tests passed.");

        Assert.Equal("Build finished", notification.Title);
        Assert.Equal("MyProject", notification.Subtitle);
        Assert.Equal("42 tests passed.", notification.Body);
    }

    [Fact]
    public void ReplyBuildsATextInputAction()
    {
        var action = NotificationAction.Reply("comment", "Comment", "Post", "Why?");

        Assert.Equal("comment", action.Identifier);
        Assert.Equal("Comment", action.Title);
        Assert.NotNull(action.TextInput);
        Assert.Equal("Post", action.TextInput.SendButtonTitle);
        Assert.Equal("Why?", action.TextInput.Placeholder);
    }

    [Fact]
    public void ReplyHasUsableDefaults()
    {
        var action = NotificationAction.Reply("comment", "Comment");

        Assert.Equal("Send", action.TextInput!.SendButtonTitle);
        Assert.Equal(string.Empty, action.TextInput.Placeholder);
    }

    [Fact]
    public void PlainActionsHaveNoTextInputAndNoOptions()
    {
        var action = new NotificationAction("deploy", "Deploy");

        Assert.Null(action.TextInput);
        Assert.False(action.IsDestructive);
        Assert.False(action.ActivatesApplication);
        Assert.False(action.RequiresAuthentication);
    }

    [Fact]
    public void CollectionInitialisersPopulateActionsAndUserInfo()
    {
        var notification = new Notification("Deploy?")
        {
            Actions = { new NotificationAction("deploy", "Deploy") },
            UserInfo = { ["release"] = "v1.4.0" },
        };

        Assert.Single(notification.Actions);
        Assert.Equal("v1.4.0", notification.UserInfo["release"]);
    }

    [Fact]
    public void AuthorizationStatusMirrorsTheSystemEnum()
    {
        Assert.Equal(0, (int)AuthorizationStatus.NotDetermined);
        Assert.Equal(1, (int)AuthorizationStatus.Denied);
        Assert.Equal(2, (int)AuthorizationStatus.Authorized);
        Assert.Equal(3, (int)AuthorizationStatus.Provisional);
        Assert.Equal(4, (int)AuthorizationStatus.Ephemeral);
        Assert.Equal(-1, (int)AuthorizationStatus.Unavailable);
    }
}
