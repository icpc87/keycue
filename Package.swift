// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "KeyCue",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "KeyCue",
            path: "Sources/KeyCue"
        ),
        // 仮想カメラ（Camera Extension）。build.sh が KeyCue.app/Contents/Library/SystemExtensions に入れる
        .executableTarget(
            name: "KeyCueCamera",
            path: "Sources/KeyCueCamera"
        ),
    ],
    swiftLanguageModes: [.v5]
)
