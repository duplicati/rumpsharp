import AppKit
import Foundation

// rumpsharp-helper is the CFBundleExecutable of the .app bundle RumpSharp generates for an
// unbundled .NET host. macOS derives notification identity from the executable's location, so this
// process - not the host - is the one allowed to talk to UNUserNotificationCenter. It is driven
// over newline-delimited JSON on stdin and answers on stdout.

/// Refuses to run unless stdin is a pipe.
///
/// Clicking a notification after the host has exited makes LaunchServices launch this bundle on its
/// own, with stdin wired to /dev/null. There is no host to talk to in that case, and `app.run()`
/// would keep an invisible accessory application alive forever, so bail out instead. A pipe from
/// the host reports S_IFIFO; a LaunchServices launch reports S_IFCHR.
private func hostIsAttached() -> Bool {
    var info = stat()
    guard fstat(STDIN_FILENO, &info) == 0 else {
        return false
    }

    return info.st_mode & S_IFMT == S_IFIFO
}

guard hostIsAttached() else {
    log("no host attached to stdin (launched by LaunchServices?); exiting")
    exit(0)
}

// Identity comes from the bundle this binary sits in. Without it UNUserNotificationCenter raises an
// Objective-C exception, which would abort the process, so fail with a readable message instead.
guard let bundleIdentifier = Bundle.main.bundleIdentifier else {
    log("this binary is not running from inside an .app bundle, so it cannot post notifications")
    exit(78) // EX_CONFIG
}

let app = NSApplication.shared

// Accessory: no Dock icon and no app switcher entry, but still a full application as far as
// LaunchServices is concerned, which is what lets notification clicks come back to us. The bundle's
// LSUIElement is what AppBundleOptions.ShowInDock writes, so honour it rather than overriding it.
let uiElement = Bundle.main.object(forInfoDictionaryKey: "LSUIElement")
let isAccessory = (uiElement as? Bool) ?? ((uiElement as? String) == "1")
app.setActivationPolicy(isAccessory ? .accessory : .regular)

let engine = NotificationEngine()
let decoder = JSONDecoder()

/// Reads NDJSON from stdin until EOF, handing each complete line to the main thread.
private func readStdin() {
    var pending = Data()
    var buffer = [UInt8](repeating: 0, count: 16 * 1024)

    while true {
        let count = read(STDIN_FILENO, &buffer, buffer.count)
        if count < 0 {
            if errno == EINTR {
                continue
            }

            log("stdin read failed: \(String(cString: strerror(errno)))")
            break
        }

        if count == 0 {
            break // EOF: the host has gone away.
        }

        pending.append(contentsOf: buffer[0..<count])

        while let newline = pending.firstIndex(of: 0x0A) {
            let line = pending[pending.startIndex..<newline]
            pending = pending[pending.index(after: newline)...]

            guard !line.isEmpty else {
                continue
            }

            guard let message = try? decoder.decode(Incoming.self, from: line) else {
                // Never kill the loop over one bad line; the host logs and carries on too.
                ProtocolWriter.shared.send(.failure(
                    context: "decode",
                    message: "Could not decode a message of \(line.count) bytes."))
                continue
            }

            // AppKit and the UNUserNotificationCenter delegate are main-thread only.
            DispatchQueue.main.async {
                engine.handle(message)
            }
        }
    }

    // The host died or asked us to stop by closing the pipe: take the notification process with it,
    // so nothing is left running that the user cannot see.
    DispatchQueue.main.async {
        exit(0)
    }
}

let reader = Thread {
    readStdin()
}
reader.name = "dev.rumpsharp.helper.stdin"
reader.start()

ProtocolWriter.shared.send(.ready(bundleId: bundleIdentifier, bundlePath: Bundle.main.bundlePath))

// The main run loop is not optional: `didReceive` is only delivered while it runs.
app.run()
