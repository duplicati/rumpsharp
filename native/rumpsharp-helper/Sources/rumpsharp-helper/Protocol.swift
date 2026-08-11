import Foundation

// The wire format is newline-delimited JSON in both directions. Keep this file in step with
// src/RumpSharp/Ipc/HelperProtocol.cs - the two are a hand-maintained mirror of one contract, and
// the C# side is the one with the round-trip tests.

/// A message from the .NET host on stdin.
struct Incoming: Decodable {
    /// Discriminator; see `MessageType`.
    let type: String

    /// Correlation id. Present on every request that expects a `result` back.
    let requestId: Int?

    /// `configure`: whether banners are shown even while the helper is the foreground application.
    let presentWhenForeground: Bool?

    /// `show`: the notification to post.
    let show: ShowRequest?

    /// `categories`: the complete set of action categories to register.
    let categories: [CategorySpec]?

    /// `removeDelivered`: notification identifiers to remove.
    let ids: [String]?
}

/// Request types the helper understands.
enum MessageType {
    static let configure = "configure"
    static let requestAuthorization = "requestAuthorization"
    static let authorizationStatus = "authorizationStatus"
    static let categories = "categories"
    static let show = "show"
    static let delivered = "delivered"
    static let removeDelivered = "removeDelivered"
    static let removeAllDelivered = "removeAllDelivered"
    static let removeAllPending = "removeAllPending"
    static let shutdown = "shutdown"
}

/// A notification to post.
struct ShowRequest: Decodable {
    let id: String
    let title: String
    let subtitle: String?
    let body: String?
    let playSound: Bool
    let soundName: String?

    /// Path to an image file the helper attaches. The host has already copied it somewhere
    /// disposable, because macOS moves attachment files into its own store.
    let imagePath: String?

    let threadIdentifier: String?
    let badge: Int?

    /// Delay before delivery. Absent or non-positive means "deliver now".
    let delaySeconds: Double?

    let userInfo: [String: String]?

    /// Identifier of a category registered through a previous `categories` message.
    let categoryId: String?
}

/// One registered set of action buttons.
struct CategorySpec: Decodable {
    let id: String
    let actions: [ActionSpec]
}

/// One action button.
struct ActionSpec: Decodable {
    let id: String
    let title: String
    let destructive: Bool
    let foreground: Bool
    let authenticationRequired: Bool
    let textInput: TextInputSpec?
}

/// Turns an action button into a reply field.
struct TextInputSpec: Decodable {
    let buttonTitle: String
    let placeholder: String
}

/// A message to the .NET host on stdout. Unset fields are omitted by `JSONEncoder`.
struct Outgoing: Encodable {
    let type: String

    /// Echoes the `requestId` of the request this answers.
    var requestId: Int?

    /// Why the request failed, or `nil` when it succeeded.
    var error: String?

    // -- requestAuthorization / authorizationStatus
    var granted: Bool?
    var status: String?

    // -- delivered
    var ids: [String]?

    // -- ready
    var bundleId: String?
    var bundlePath: String?

    // -- response (unsolicited: the user interacted with a notification)
    var id: String?
    var action: String?
    var activation: String?
    var userText: String?
    var title: String?
    var subtitle: String?
    var body: String?
    var userInfo: [String: String]?

    // -- error (unsolicited)
    var context: String?
    var message: String?

    static func ready(bundleId: String?, bundlePath: String) -> Outgoing {
        var out = Outgoing(type: "ready")
        out.bundleId = bundleId
        out.bundlePath = bundlePath
        return out
    }

    static func result(_ requestId: Int?, error: String? = nil) -> Outgoing {
        var out = Outgoing(type: "result")
        out.requestId = requestId
        out.error = error
        return out
    }

    static func failure(context: String, message: String) -> Outgoing {
        var out = Outgoing(type: "error")
        out.context = context
        out.message = message
        return out
    }
}

/// Writes NDJSON to stdout, one message per `write(2)`, serialised across threads.
///
/// stdout carries nothing but protocol: every diagnostic goes to stderr, so the host can parse
/// each line without having to guess.
final class ProtocolWriter {
    static let shared = ProtocolWriter()

    private let queue = DispatchQueue(label: "dev.rumpsharp.helper.stdout")
    private let encoder = JSONEncoder()

    private init() {
        // Deterministic field order keeps the transcript diffable when debugging.
        encoder.outputFormatting = [.sortedKeys]
    }

    func send(_ message: Outgoing) {
        queue.async { [encoder] in
            guard var data = try? encoder.encode(message) else {
                log("failed to encode a \(message.type) message")
                return
            }

            data.append(0x0A)
            data.withUnsafeBytes { raw in
                var offset = 0
                while offset < raw.count {
                    let written = write(STDOUT_FILENO, raw.baseAddress!.advanced(by: offset), raw.count - offset)
                    if written <= 0 {
                        // The host is gone; there is nobody left to tell.
                        if errno == EINTR { continue }
                        return
                    }

                    offset += written
                }
            }
        }
    }
}

/// Writes a diagnostic to stderr, which the host drains but never parses.
func log(_ message: String) {
    FileHandle.standardError.write(Data("rumpsharp-helper: \(message)\n".utf8))
}
