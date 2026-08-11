using Xunit;

namespace RumpSharp.Tests;

/// <summary>
/// Covers the identifier derived from a set of action buttons. macOS stores it on every notification it
/// delivers, so it has to be derived the same way in a later run of the application - otherwise the
/// buttons on notifications still sitting in Notification Center stop working.
/// </summary>
public sealed class CategoryIdentifierTests
{
    private static List<NotificationAction> Deploy() =>
    [
        new("deploy", "Deploy") { ActivatesApplication = true },
        new("cancel", "Cancel") { IsDestructive = true },
    ];

    [Fact]
    public void IsStableForEquivalentActions() =>
        Assert.Equal(
            NotificationCenter.CategoryIdentifier(Deploy()),
            NotificationCenter.CategoryIdentifier(Deploy()));

    [Fact]
    public void IsAnAsciiIdentifierWithAFixedShape()
    {
        var identifier = NotificationCenter.CategoryIdentifier(Deploy());

        Assert.StartsWith("rumpsharp.", identifier, StringComparison.Ordinal);
        Assert.Equal("rumpsharp.".Length + 32, identifier.Length);
        Assert.All(identifier, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c == '.'));
    }

    [Fact]
    public void ChangesWhenATitleChanges()
    {
        var renamed = Deploy();
        renamed[0].Title = "Deploy now";

        Assert.NotEqual(
            NotificationCenter.CategoryIdentifier(Deploy()),
            NotificationCenter.CategoryIdentifier(renamed));
    }

    [Fact]
    public void ChangesWhenAnOptionChanges()
    {
        var changed = Deploy();
        changed[0].RequiresAuthentication = true;

        Assert.NotEqual(
            NotificationCenter.CategoryIdentifier(Deploy()),
            NotificationCenter.CategoryIdentifier(changed));
    }

    [Fact]
    public void ChangesWhenATextInputIsAdded()
    {
        var withReply = Deploy();
        withReply.Add(NotificationAction.Reply("comment", "Comment", "Post", "Why?"));

        var withDifferentPlaceholder = Deploy();
        withDifferentPlaceholder.Add(NotificationAction.Reply("comment", "Comment", "Post", "Because"));

        Assert.NotEqual(
            NotificationCenter.CategoryIdentifier(withReply),
            NotificationCenter.CategoryIdentifier(withDifferentPlaceholder));
        Assert.NotEqual(
            NotificationCenter.CategoryIdentifier(Deploy()),
            NotificationCenter.CategoryIdentifier(withReply));
    }

    [Fact]
    public void ChangesWithTheOrderOfTheButtons()
    {
        var reversed = Deploy();
        reversed.Reverse();

        Assert.NotEqual(
            NotificationCenter.CategoryIdentifier(Deploy()),
            NotificationCenter.CategoryIdentifier(reversed));
    }

    [Fact]
    public void HandlesAnEmptyActionSet() =>
        Assert.StartsWith("rumpsharp.", NotificationCenter.CategoryIdentifier([]), StringComparison.Ordinal);
}
