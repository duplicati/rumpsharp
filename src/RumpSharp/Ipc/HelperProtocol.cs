using System.Text.Json;
using System.Text.Json.Serialization;

namespace RumpSharp.Ipc;

/// <summary>
/// The newline-delimited JSON contract between the host and <c>rumpsharp-helper</c>.
/// </summary>
/// <remarks>
/// This is a hand-maintained mirror of <c>native/rumpsharp-helper/Sources/rumpsharp-helper/Protocol.swift</c>.
/// Changing a name on one side without the other silently breaks a message, so the round-trip tests
/// in <c>tests/RumpSharp.Tests</c> pin the exact wire names.
/// </remarks>
internal static class HelperProtocol
{
    /// <summary>Request types the helper understands.</summary>
    internal static class Requests
    {
        internal const string Configure = "configure";
        internal const string RequestAuthorization = "requestAuthorization";
        internal const string AuthorizationStatus = "authorizationStatus";
        internal const string Categories = "categories";
        internal const string Show = "show";
        internal const string Delivered = "delivered";
        internal const string RemoveDelivered = "removeDelivered";
        internal const string RemoveAllDelivered = "removeAllDelivered";
        internal const string RemoveAllPending = "removeAllPending";
        internal const string Shutdown = "shutdown";
    }

    /// <summary>Message types the helper sends.</summary>
    internal static class Messages
    {
        /// <summary>Sent once at startup, carrying the identity the helper ended up with.</summary>
        internal const string Ready = "ready";

        /// <summary>The answer to a request, correlated by <see cref="HelperMessage.RequestId"/>.</summary>
        internal const string Result = "result";

        /// <summary>Unsolicited: the user interacted with a notification.</summary>
        internal const string Response = "response";

        /// <summary>Unsolicited: something went wrong outside any single request.</summary>
        internal const string Error = "error";
    }

    /// <summary>Maps the helper's authorization status names onto <see cref="AuthorizationStatus"/>.</summary>
    internal static AuthorizationStatus ToAuthorizationStatus(string? status) => status switch
    {
        "notDetermined" => AuthorizationStatus.NotDetermined,
        "denied" => AuthorizationStatus.Denied,
        "authorized" => AuthorizationStatus.Authorized,
        "provisional" => AuthorizationStatus.Provisional,
        "ephemeral" => AuthorizationStatus.Ephemeral,
        _ => AuthorizationStatus.Unavailable,
    };

    /// <summary>Maps the helper's activation names onto <see cref="NotificationActivation"/>.</summary>
    internal static NotificationActivation ToActivation(string? activation) => activation switch
    {
        "dismissed" => NotificationActivation.Dismissed,
        "action" => NotificationActivation.Action,
        _ => NotificationActivation.Default,
    };
}

/// <summary>A message the host sends to the helper on stdin.</summary>
internal sealed class HelperRequest
{
    /// <summary>One of <see cref="HelperProtocol.Requests"/>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Correlation id, set on every request that expects an answer.</summary>
    public int? RequestId { get; set; }

    /// <summary><c>configure</c>: whether banners appear while the helper is in the foreground.</summary>
    public bool? PresentWhenForeground { get; set; }

    /// <summary><c>show</c>: the notification to post.</summary>
    public HelperShow? Show { get; set; }

    /// <summary><c>categories</c>: the complete set of action categories to register.</summary>
    public List<HelperCategory>? Categories { get; set; }

    /// <summary><c>removeDelivered</c>: the identifiers to remove.</summary>
    public List<string>? Ids { get; set; }
}

/// <summary>The notification payload of a <c>show</c> request.</summary>
internal sealed class HelperShow
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Subtitle { get; set; }

    public string? Body { get; set; }

    public bool PlaySound { get; set; }

    public string? SoundName { get; set; }

    /// <summary>A disposable copy of the caller's image; macOS moves attachments into its own store.</summary>
    public string? ImagePath { get; set; }

    public string? ThreadIdentifier { get; set; }

    public int? Badge { get; set; }

    /// <summary>Delay before delivery. Absent means "deliver now".</summary>
    public double? DelaySeconds { get; set; }

    public Dictionary<string, string>? UserInfo { get; set; }

    /// <summary>Identifier of a category sent in a previous <c>categories</c> request.</summary>
    public string? CategoryId { get; set; }
}

/// <summary>One registered set of action buttons.</summary>
internal sealed class HelperCategory
{
    public string Id { get; set; } = string.Empty;

    public List<HelperAction> Actions { get; set; } = [];
}

/// <summary>One action button.</summary>
internal sealed class HelperAction
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public bool Destructive { get; set; }

    public bool Foreground { get; set; }

    public bool AuthenticationRequired { get; set; }

    public HelperTextInput? TextInput { get; set; }
}

/// <summary>Turns an action button into a reply field.</summary>
internal sealed class HelperTextInput
{
    public string ButtonTitle { get; set; } = "Send";

    public string Placeholder { get; set; } = string.Empty;
}

/// <summary>
/// A message the helper sends on stdout. One flat type covers every message; which fields are set
/// depends on <see cref="Type"/>.
/// </summary>
internal sealed class HelperMessage
{
    /// <summary>One of <see cref="HelperProtocol.Messages"/>.</summary>
    public string? Type { get; set; }

    /// <summary>The <see cref="HelperRequest.RequestId"/> this answers, for a <c>result</c>.</summary>
    public int? RequestId { get; set; }

    /// <summary>Why the request failed, or <see langword="null"/> when it succeeded.</summary>
    public string? Error { get; set; }

    /// <summary>Whether the user granted permission, for <c>requestAuthorization</c>.</summary>
    public bool? Granted { get; set; }

    /// <summary>The permission state, as one of the <c>UNAuthorizationStatus</c> names.</summary>
    public string? Status { get; set; }

    /// <summary>Delivered notification identifiers, for <c>delivered</c>.</summary>
    public List<string>? Ids { get; set; }

    /// <summary>The identity the helper is running with, on <c>ready</c>.</summary>
    public string? BundleId { get; set; }

    /// <summary>The bundle the helper is running from, on <c>ready</c>.</summary>
    public string? BundlePath { get; set; }

    // -- response

    /// <summary>Identifier of the notification the user interacted with.</summary>
    public string? Id { get; set; }

    /// <summary>Identifier of the action the user chose.</summary>
    public string? Action { get; set; }

    /// <summary><c>default</c>, <c>dismissed</c> or <c>action</c>.</summary>
    public string? Activation { get; set; }

    /// <summary>What the user typed into a reply field.</summary>
    public string? UserText { get; set; }

    public string? Title { get; set; }

    public string? Subtitle { get; set; }

    public string? Body { get; set; }

    public Dictionary<string, string>? UserInfo { get; set; }

    // -- error

    /// <summary>What the helper was doing when an unsolicited error happened.</summary>
    public string? Context { get; set; }

    /// <summary>The unsolicited error's description.</summary>
    public string? Message { get; set; }
}

/// <summary>
/// Source-generated serialisation for the helper protocol, so the IPC path stays trim- and
/// AOT-safe.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HelperRequest))]
[JsonSerializable(typeof(HelperMessage))]
internal sealed partial class HelperJson : JsonSerializerContext
{
    /// <summary>Serialises a request as one NDJSON line, without the newline.</summary>
    internal static string Serialize(HelperRequest request) =>
        JsonSerializer.Serialize(request, Default.HelperRequest);

    /// <summary>Parses one NDJSON line from the helper.</summary>
    /// <returns>The message, or <see langword="null"/> if the line was not usable.</returns>
    internal static HelperMessage? Deserialize(string line)
    {
        try
        {
            return JsonSerializer.Deserialize(line, Default.HelperMessage);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
