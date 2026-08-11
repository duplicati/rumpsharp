using System.Text.Json;
using RumpSharp.Ipc;
using Xunit;

namespace RumpSharp.Tests;

/// <summary>
/// Pins the wire format of the helper protocol. The Swift side
/// (<c>native/rumpsharp-helper/Sources/rumpsharp-helper/Protocol.swift</c>) decodes these exact
/// names, and nothing at build time checks that the two agree - so a renamed property has to fail
/// here or it fails silently at runtime.
/// </summary>
public sealed class HelperProtocolTests
{
    [Fact]
    public void RequestsUseCamelCaseNamesAndOmitUnsetFields()
    {
        var json = HelperJson.Serialize(new HelperRequest
        {
            Type = "configure",
            RequestId = 7,
            PresentWhenForeground = true,
        });

        Assert.Equal("""{"type":"configure","requestId":7,"presentWhenForeground":true}""", json);
    }

    [Fact]
    public void ARequestIsASingleLine()
    {
        var json = HelperJson.Serialize(new HelperRequest
        {
            Type = "show",
            Show = new HelperShow { Id = "a\nb", Title = "line\r\nbreak", Body = "tab\there" },
        });

        Assert.DoesNotContain('\n', json);
        Assert.DoesNotContain('\r', json);
    }

    [Fact]
    public void AShowRequestCarriesEveryNotificationField()
    {
        var json = HelperJson.Serialize(new HelperRequest
        {
            Type = "show",
            RequestId = 1,
            Show = new HelperShow
            {
                Id = "build-42",
                Title = "Build finished",
                Subtitle = "main",
                Body = "42 tests passed",
                PlaySound = true,
                SoundName = "Ping.aiff",
                ImagePath = "/tmp/thumb.png",
                ThreadIdentifier = "builds",
                Badge = 3,
                DelaySeconds = 5,
                UserInfo = new Dictionary<string, string> { ["commit"] = "9f2ac41" },
                CategoryId = "rumpsharp.ABC",
            },
        });

        using var document = JsonDocument.Parse(json);
        var show = document.RootElement.GetProperty("show");

        Assert.Equal("build-42", show.GetProperty("id").GetString());
        Assert.Equal("Build finished", show.GetProperty("title").GetString());
        Assert.Equal("main", show.GetProperty("subtitle").GetString());
        Assert.Equal("42 tests passed", show.GetProperty("body").GetString());
        Assert.True(show.GetProperty("playSound").GetBoolean());
        Assert.Equal("Ping.aiff", show.GetProperty("soundName").GetString());
        Assert.Equal("/tmp/thumb.png", show.GetProperty("imagePath").GetString());
        Assert.Equal("builds", show.GetProperty("threadIdentifier").GetString());
        Assert.Equal(3, show.GetProperty("badge").GetInt32());
        Assert.Equal(5, show.GetProperty("delaySeconds").GetDouble());
        Assert.Equal("9f2ac41", show.GetProperty("userInfo").GetProperty("commit").GetString());
        Assert.Equal("rumpsharp.ABC", show.GetProperty("categoryId").GetString());
    }

    /// <summary>
    /// The Swift decoder declares these three as non-optional <c>Bool</c>, so leaving them out of the
    /// JSON makes the whole message undecodable.
    /// </summary>
    [Fact]
    public void ActionFlagsAreAlwaysWrittenEvenWhenFalse()
    {
        var json = HelperJson.Serialize(new HelperRequest
        {
            Type = "categories",
            Categories =
            [
                new HelperCategory
                {
                    Id = "rumpsharp.ABC",
                    Actions = [new HelperAction { Id = "retry", Title = "Retry" }],
                },
            ],
        });

        using var document = JsonDocument.Parse(json);
        var action = document.RootElement
            .GetProperty("categories")[0]
            .GetProperty("actions")[0];

        Assert.False(action.GetProperty("destructive").GetBoolean());
        Assert.False(action.GetProperty("foreground").GetBoolean());
        Assert.False(action.GetProperty("authenticationRequired").GetBoolean());
        Assert.False(action.TryGetProperty("textInput", out _));
    }

    [Fact]
    public void AReplyActionCarriesItsTextInput()
    {
        var json = HelperJson.Serialize(new HelperRequest
        {
            Type = "categories",
            Categories =
            [
                new HelperCategory
                {
                    Id = "rumpsharp.ABC",
                    Actions =
                    [
                        new HelperAction
                        {
                            Id = "comment",
                            Title = "Comment",
                            TextInput = new HelperTextInput { ButtonTitle = "Post", Placeholder = "Why?" },
                        },
                    ],
                },
            ],
        });

        using var document = JsonDocument.Parse(json);
        var input = document.RootElement
            .GetProperty("categories")[0]
            .GetProperty("actions")[0]
            .GetProperty("textInput");

        Assert.Equal("Post", input.GetProperty("buttonTitle").GetString());
        Assert.Equal("Why?", input.GetProperty("placeholder").GetString());
    }

    [Fact]
    public void ReadyIsParsed()
    {
        var message = HelperJson.Deserialize(
            """{"type":"ready","bundleId":"dev.rumpsharp.sample","bundlePath":"/Apps/Sample.app"}""");

        Assert.NotNull(message);
        Assert.Equal(HelperProtocol.Messages.Ready, message.Type);
        Assert.Equal("dev.rumpsharp.sample", message.BundleId);
        Assert.Equal("/Apps/Sample.app", message.BundlePath);
    }

    [Fact]
    public void AResultCarriesItsCorrelationIdAndPayload()
    {
        var message = HelperJson.Deserialize(
            """{"type":"result","requestId":4,"granted":true,"status":"authorized","ids":["a","b"]}""");

        Assert.NotNull(message);
        Assert.Equal(4, message.RequestId);
        Assert.True(message.Granted);
        Assert.Equal(AuthorizationStatus.Authorized, HelperProtocol.ToAuthorizationStatus(message.Status));
        Assert.Equal(["a", "b"], message.Ids);
        Assert.Null(message.Error);
    }

    [Fact]
    public void AResponseCarriesEverythingNotificationResponseExposes()
    {
        var message = HelperJson.Deserialize(
            """
            {"type":"response","id":"deploy-prompt","action":"comment","activation":"action",
             "userText":"because","title":"Deploy?","subtitle":"v1.4.0","body":"Pick one",
             "userInfo":{"release":"v1.4.0"}}
            """.ReplaceLineEndings(string.Empty));

        Assert.NotNull(message);
        Assert.Equal(HelperProtocol.Messages.Response, message.Type);
        Assert.Equal("deploy-prompt", message.Id);
        Assert.Equal("comment", message.Action);
        Assert.Equal(NotificationActivation.Action, HelperProtocol.ToActivation(message.Activation));
        Assert.Equal("because", message.UserText);
        Assert.Equal("Deploy?", message.Title);
        Assert.Equal("v1.4.0", message.Subtitle);
        Assert.Equal("Pick one", message.Body);
        Assert.NotNull(message.UserInfo);
        Assert.Equal("v1.4.0", Assert.Contains("release", message.UserInfo));
    }

    [Fact]
    public void AnErrorMessageIsParsed()
    {
        var message = HelperJson.Deserialize("""{"type":"error","context":"show","message":"nope"}""");

        Assert.NotNull(message);
        Assert.Equal(HelperProtocol.Messages.Error, message.Type);
        Assert.Equal("show", message.Context);
        Assert.Equal("nope", message.Message);
    }

    [Theory]
    [InlineData("notDetermined", AuthorizationStatus.NotDetermined)]
    [InlineData("denied", AuthorizationStatus.Denied)]
    [InlineData("authorized", AuthorizationStatus.Authorized)]
    [InlineData("provisional", AuthorizationStatus.Provisional)]
    [InlineData("ephemeral", AuthorizationStatus.Ephemeral)]
    [InlineData("something new", AuthorizationStatus.Unavailable)]
    [InlineData(null, AuthorizationStatus.Unavailable)]
    public void AuthorizationStatusNamesAreMapped(string? name, AuthorizationStatus expected) =>
        Assert.Equal(expected, HelperProtocol.ToAuthorizationStatus(name));

    [Theory]
    [InlineData("default", NotificationActivation.Default)]
    [InlineData("dismissed", NotificationActivation.Dismissed)]
    [InlineData("action", NotificationActivation.Action)]
    [InlineData(null, NotificationActivation.Default)]
    public void ActivationNamesAreMapped(string? name, NotificationActivation expected) =>
        Assert.Equal(expected, HelperProtocol.ToActivation(name));

    /// <summary>A line the helper never produces must not take the reader loop down.</summary>
    [Theory]
    [InlineData("this is not json")]
    [InlineData("{\"type\":\"result\",")]
    [InlineData("[]")]
    public void UnparsableLinesAreRejectedWithoutThrowing(string line) =>
        Assert.Null(HelperJson.Deserialize(line));
}
