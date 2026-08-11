// swift-tools-version:5.9
import PackageDescription

// The helper is the process that owns UNUserNotificationCenter on behalf of an unbundled .NET host.
// It has to be tiny and dependency-free: the release build is committed to the repository and
// embedded in RumpSharp.dll, so every byte ships with the NuGet package.
let package = Package(
    name: "rumpsharp-helper",
    platforms: [.macOS(.v11)],
    targets: [
        .executableTarget(
            name: "rumpsharp-helper",
            path: "Sources/rumpsharp-helper")
    ]
)
