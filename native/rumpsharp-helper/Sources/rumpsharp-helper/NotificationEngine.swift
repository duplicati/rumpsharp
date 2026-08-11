import AppKit
import Foundation
import UserNotifications

/// Owns `UNUserNotificationCenter` for the host process and translates protocol messages into it.
///
/// Everything here runs on the main thread: `main.swift` dispatches decoded messages there, because
/// AppKit and the notification delegate callbacks both require it.
final class NotificationEngine: NSObject, UNUserNotificationCenterDelegate {
    private let center = UNUserNotificationCenter.current()
    private var presentWhenForeground = true

    override init() {
        super.init()
        center.delegate = self
    }

    // ------------------------------------------------------------------ requests

    func handle(_ message: Incoming) {
        switch message.type {
        case MessageType.configure:
            presentWhenForeground = message.presentWhenForeground ?? true
            ProtocolWriter.shared.send(.result(message.requestId))

        case MessageType.requestAuthorization:
            requestAuthorization(message.requestId)

        case MessageType.authorizationStatus:
            authorizationStatus(message.requestId)

        case MessageType.categories:
            setCategories(message.categories ?? [], requestId: message.requestId)

        case MessageType.show:
            guard let request = message.show else {
                ProtocolWriter.shared.send(.result(message.requestId, error: "The 'show' message had no payload."))
                return
            }

            show(request, requestId: message.requestId)

        case MessageType.delivered:
            delivered(message.requestId)

        case MessageType.removeDelivered:
            center.removeDeliveredNotifications(withIdentifiers: message.ids ?? [])
            ProtocolWriter.shared.send(.result(message.requestId))

        case MessageType.removeAllDelivered:
            center.removeAllDeliveredNotifications()
            ProtocolWriter.shared.send(.result(message.requestId))

        case MessageType.removeAllPending:
            center.removeAllPendingNotificationRequests()
            ProtocolWriter.shared.send(.result(message.requestId))

        case MessageType.shutdown:
            exit(0)

        default:
            ProtocolWriter.shared.send(.failure(context: message.type, message: "Unknown message type."))
        }
    }

    private func requestAuthorization(_ requestId: Int?) {
        center.requestAuthorization(options: [.alert, .sound, .badge]) { [center] granted, error in
            // Report the resulting status alongside the grant, so the host can tell "denied" from
            // "the prompt never appeared".
            center.getNotificationSettings { settings in
                var out = Outgoing.result(requestId, error: error?.localizedDescription)
                out.granted = granted
                out.status = Self.name(of: settings.authorizationStatus)
                ProtocolWriter.shared.send(out)
            }
        }
    }

    private func authorizationStatus(_ requestId: Int?) {
        center.getNotificationSettings { settings in
            var out = Outgoing.result(requestId)
            out.status = Self.name(of: settings.authorizationStatus)
            ProtocolWriter.shared.send(out)
        }
    }

    /// Replaces the registered categories with `specs`.
    ///
    /// macOS keeps one set per process and replaces it wholesale, so the host always sends the
    /// complete set. That also makes the call idempotent, which is what lets the host replay it
    /// after the helper has been restarted.
    private func setCategories(_ specs: [CategorySpec], requestId: Int?) {
        let categories = specs.map { spec in
            UNNotificationCategory(
                identifier: spec.id,
                actions: spec.actions.map(Self.action(from:)),
                intentIdentifiers: [],
                options: [])
        }

        center.setNotificationCategories(Set(categories))
        ProtocolWriter.shared.send(.result(requestId))
    }

    private func show(_ request: ShowRequest, requestId: Int?) {
        let content = UNMutableNotificationContent()
        content.title = request.title
        if let subtitle = request.subtitle {
            content.subtitle = subtitle
        }

        if let body = request.body {
            content.body = body
        }

        if request.playSound {
            content.sound = request.soundName.map { UNNotificationSound(named: UNNotificationSoundName($0)) }
                ?? UNNotificationSound.default
        }

        if let thread = request.threadIdentifier {
            content.threadIdentifier = thread
        }

        if let badge = request.badge {
            content.badge = NSNumber(value: badge)
        }

        if let userInfo = request.userInfo, !userInfo.isEmpty {
            content.userInfo = userInfo
        }

        if let category = request.categoryId {
            content.categoryIdentifier = category
        }

        if let imagePath = request.imagePath {
            do {
                content.attachments = [try UNNotificationAttachment(
                    identifier: "image",
                    url: URL(fileURLWithPath: imagePath),
                    options: nil)]
            } catch {
                ProtocolWriter.shared.send(.result(
                    requestId,
                    error: "macOS rejected the notification image '\(imagePath)': \(error.localizedDescription)"))
                return
            }
        }

        var trigger: UNNotificationTrigger?
        if let delay = request.delaySeconds, delay > 0 {
            trigger = UNTimeIntervalNotificationTrigger(timeInterval: delay, repeats: false)
        }

        center.add(UNNotificationRequest(identifier: request.id, content: content, trigger: trigger)) { error in
            ProtocolWriter.shared.send(.result(requestId, error: error?.localizedDescription))
        }
    }

    private func delivered(_ requestId: Int?) {
        center.getDeliveredNotifications { notifications in
            var out = Outgoing.result(requestId)
            out.ids = notifications.map { $0.request.identifier }
            ProtocolWriter.shared.send(out)
        }
    }

    // ------------------------------------------------------------------ delegate

    func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        willPresent notification: UNNotification,
        withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void
    ) {
        // macOS suppresses banners for the foreground application unless the delegate asks for them.
        completionHandler(presentWhenForeground ? [.banner, .list, .sound] : [.list])
    }

    func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        didReceive response: UNNotificationResponse,
        withCompletionHandler completionHandler: @escaping () -> Void
    ) {
        let content = response.notification.request.content

        var out = Outgoing(type: "response")
        out.id = response.notification.request.identifier
        out.action = response.actionIdentifier
        out.activation = switch response.actionIdentifier {
        case UNNotificationDefaultActionIdentifier: "default"
        case UNNotificationDismissActionIdentifier: "dismissed"
        default: "action"
        }

        out.userText = (response as? UNTextInputNotificationResponse)?.userText
        out.title = content.title
        out.subtitle = content.subtitle.isEmpty ? nil : content.subtitle
        out.body = content.body.isEmpty ? nil : content.body
        out.userInfo = content.userInfo.reduce(into: [String: String]()) { result, entry in
            if let key = entry.key as? String, let value = entry.value as? String {
                result[key] = value
            }
        }

        ProtocolWriter.shared.send(out)

        // The host is not waiting for us, and macOS wants this called promptly.
        completionHandler()
    }

    // ------------------------------------------------------------------ mapping

    private static func action(from spec: ActionSpec) -> UNNotificationAction {
        var options: UNNotificationActionOptions = []
        if spec.authenticationRequired {
            options.insert(.authenticationRequired)
        }

        if spec.destructive {
            options.insert(.destructive)
        }

        if spec.foreground {
            options.insert(.foreground)
        }

        guard let input = spec.textInput else {
            return UNNotificationAction(identifier: spec.id, title: spec.title, options: options)
        }

        return UNTextInputNotificationAction(
            identifier: spec.id,
            title: spec.title,
            options: options,
            textInputButtonTitle: input.buttonTitle,
            textInputPlaceholder: input.placeholder)
    }

    /// Mirrors `RumpSharp.AuthorizationStatus`.
    private static func name(of status: UNAuthorizationStatus) -> String {
        switch status {
        case .notDetermined: return "notDetermined"
        case .denied: return "denied"
        case .authorized: return "authorized"
        case .provisional: return "provisional"
        case .ephemeral: return "ephemeral"
        @unknown default: return "unavailable"
        }
    }
}
