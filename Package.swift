// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "KeyCue",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "KeyCue",
            path: "Sources/KeyCue"
        )
    ],
    swiftLanguageModes: [.v5]
)
