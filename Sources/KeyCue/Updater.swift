import AppKit
import Security

/// アップデート。
///
/// 1. https://keycue.ic2.jp/version.json で新しい版があるか確かめる
/// 2. 新しい版の ZIP（公証済みの KeyCue.app）をダウンロードして展開する
/// 3. 展開したアプリが「このアプリと同じ開発チームの署名」で「Apple の公証を受けている」ことを確かめる
///    （確かめられなければ入れ替えない）
/// 4. KeyCue を終了させ、小さなスクリプトが今のアプリと入れ替えて起動し直す
final class Updater: NSObject {
    static let shared = Updater()

    struct Release: Decodable {
        let version: String
        let build: Int
        let url: URL            // ダウンロードページ用の DMG
        let zip: URL            // アップデート用の ZIP
        let page: URL?
        let date: String?
        let minimumSystemVersion: String?
        let notes: [String]?
    }

    enum UpdateError: LocalizedError {
        case download(String)
        case untrusted(String)
        case notWritable
        case other(String)

        var errorDescription: String? {
            switch self {
            case .download(let s): return "ダウンロードできませんでした（\(s)）"
            case .untrusted(let s): return "ダウンロードしたアプリの署名を確認できないため、入れ替えを中止しました（\(s)）"
            case .notWritable: return "KeyCue が置かれているフォルダに書き込めません"
            case .other(let s): return s
            }
        }
    }

    static var feedURL: URL {
        if let s = ProcessInfo.processInfo.environment["KEYCUE_UPDATE_URL"], let u = URL(string: s) { return u }
        return URL(string: "https://keycue.ic2.jp/version.json")!
    }

    static let autoCheckKey = "pref.autoCheckUpdates"
    private static let lastCheckKey = "update.lastCheck"
    private static let skippedKey = "update.skippedBuild"

    var currentVersion: String { Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0" }
    var currentBuild: Int { Int(Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "0") ?? 0 }

    private var checking = false
    private var progressWindow: NSWindow?
    private var progressBar: NSProgressIndicator?
    private var progressLabel: NSTextField?
    private var downloadTask: Task<Void, Never>?
    /// 終了したあとに入れ替えるもの
    private var pending: (newApp: URL, target: URL, work: URL)?

    // MARK: - 確認

    /// 起動時：1 日 1 回まで、何もなければ黙っている
    func checkOnLaunch() {
        UserDefaults.standard.register(defaults: [Updater.autoCheckKey: true])
        guard UserDefaults.standard.bool(forKey: Updater.autoCheckKey) else { return }
        let last = UserDefaults.standard.double(forKey: Updater.lastCheckKey)
        guard Date().timeIntervalSince1970 - last > 20 * 3600 else { return }
        DispatchQueue.main.asyncAfter(deadline: .now() + 3) { self.check(userInitiated: false) }
    }

    /// メニューの「アップデートを確認…」から
    @objc func checkNow(_ sender: Any?) { check(userInitiated: true) }

    func check(userInitiated: Bool) {
        guard !checking, pending == nil else { return }
        checking = true
        Task {
            defer { DispatchQueue.main.async { self.checking = false } }
            do {
                var request = URLRequest(url: Updater.feedURL, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 20)
                request.setValue("KeyCue/\(currentVersion)", forHTTPHeaderField: "User-Agent")
                let (data, response) = try await URLSession.shared.data(for: request)
                if let http = response as? HTTPURLResponse, http.statusCode != 200 {
                    throw UpdateError.download("サーバーの応答 \(http.statusCode)")
                }
                let release = try JSONDecoder().decode(Release.self, from: data)
                UserDefaults.standard.set(Date().timeIntervalSince1970, forKey: Updater.lastCheckKey)
                await MainActor.run { self.handle(release, userInitiated: userInitiated) }
            } catch {
                guard userInitiated else { return }
                await MainActor.run {
                    let alert = NSAlert()
                    alert.messageText = "アップデートを確認できませんでした"
                    alert.informativeText = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
                    alert.runModal()
                }
            }
        }
    }

    private func isNewer(_ r: Release) -> Bool {
        if r.build != currentBuild { return r.build > currentBuild }
        return r.version.compare(currentVersion, options: .numeric) == .orderedDescending
    }

    private func handle(_ release: Release, userInitiated: Bool) {
        guard isNewer(release) else {
            if userInitiated {
                let alert = NSAlert()
                alert.messageText = "KeyCue は最新です"
                alert.informativeText = "いまの版: \(currentVersion)（build \(currentBuild)）"
                alert.runModal()
            }
            return
        }
        if !userInitiated, UserDefaults.standard.integer(forKey: Updater.skippedKey) == release.build { return }
        if let min = release.minimumSystemVersion {
            let os = ProcessInfo.processInfo.operatingSystemVersion
            let current = "\(os.majorVersion).\(os.minorVersion)"
            if min.compare(current, options: .numeric) == .orderedDescending {
                if userInitiated {
                    let alert = NSAlert()
                    alert.messageText = "KeyCue \(release.version) は macOS \(min) 以降が必要です"
                    alert.runModal()
                }
                return
            }
        }

        let alert = NSAlert()
        alert.messageText = "KeyCue \(release.version) があります"
        var info = "いまの版: \(currentVersion)"
        if let notes = release.notes, !notes.isEmpty {
            info += "\n\n" + notes.map { "・\($0)" }.joined(separator: "\n")
        }
        info += "\n\nアップデートすると KeyCue をいったん終了して、新しい版で起動し直します。"
        alert.informativeText = info
        alert.addButton(withTitle: "アップデート")
        alert.addButton(withTitle: "あとで")
        if !userInitiated { alert.addButton(withTitle: "この版はスキップ") }
        NSApp.activate()
        switch alert.runModal() {
        case .alertFirstButtonReturn: install(release)
        case .alertThirdButtonReturn: UserDefaults.standard.set(release.build, forKey: Updater.skippedKey)
        default: break
        }
    }

    // MARK: - インストール

    private func install(_ release: Release) {
        let target = Bundle.main.bundleURL
        // ディスクイメージから直接起動している、または置き場所に書き込めないときはダウンロードページへ
        if target.path.hasPrefix("/Volumes/") || !FileManager.default.isWritableFile(atPath: target.deletingLastPathComponent().path) {
            let alert = NSAlert()
            alert.messageText = "この場所の KeyCue は自動で入れ替えられません"
            alert.informativeText = target.path.hasPrefix("/Volumes/")
                ? "ディスクイメージから起動しています。KeyCue を「アプリケーション」フォルダにコピーしてから起動してください。"
                : "置かれているフォルダ（\(target.deletingLastPathComponent().path)）に書き込めません。ダウンロードページから新しい版を入れてください。"
            alert.addButton(withTitle: "ダウンロードページを開く")
            alert.addButton(withTitle: "閉じる")
            if alert.runModal() == .alertFirstButtonReturn { NSWorkspace.shared.open(release.page ?? release.url) }
            return
        }
        showProgress("ダウンロード中…")
        downloadTask = Task {
            let work = FileManager.default.temporaryDirectory.appendingPathComponent("KeyCueUpdate-\(UUID().uuidString)")
            do {
                try FileManager.default.createDirectory(at: work, withIntermediateDirectories: true)
                let zip = try await download(release.zip, to: work)
                try Task.checkCancellation()
                await MainActor.run { self.setProgress(nil, "確認中…") }
                let app = try Updater.unpack(zip, into: work)
                try Updater.verify(app)
                try Task.checkCancellation()
                await MainActor.run {
                    self.closeProgress()
                    self.pending = (app, target, work)
                    // 終了してから入れ替える（未保存のキーマップがあれば保存の確認が出る）
                    NSApp.terminate(nil)
                    // ここに来たら終了がキャンセルされた
                    if self.pending != nil {
                        self.pending = nil
                        try? FileManager.default.removeItem(at: work)
                        (NSApp.delegate as? AppDelegate)?.setStatus("アップデートを中止しました")
                    }
                }
            } catch is CancellationError {
                try? FileManager.default.removeItem(at: work)
                await MainActor.run { self.closeProgress() }
            } catch {
                try? FileManager.default.removeItem(at: work)
                await MainActor.run {
                    self.closeProgress()
                    let alert = NSAlert()
                    alert.alertStyle = .warning
                    alert.messageText = "アップデートできませんでした"
                    alert.informativeText = ((error as? LocalizedError)?.errorDescription ?? error.localizedDescription)
                        + "\n\nホームページから新しい版をダウンロードして入れ替えることもできます。"
                    alert.addButton(withTitle: "ダウンロードページを開く")
                    alert.addButton(withTitle: "閉じる")
                    if alert.runModal() == .alertFirstButtonReturn { NSWorkspace.shared.open(release.page ?? release.url) }
                }
            }
        }
    }

    /// KeyCue が終了するときに呼ぶ。入れ替えのスクリプトを走らせる
    func applicationWillTerminate() {
        guard let p = pending else { return }
        pending = nil
        let script = p.work.appendingPathComponent("swap.sh")
        let body = """
        #!/bin/sh
        # KeyCue の終了を待って、新しい版と入れ替えて起動し直す
        PID="$1"; NEW="$2"; TARGET="$3"; WORK="$4"
        i=0
        while kill -0 "$PID" 2>/dev/null; do sleep 0.2; i=$((i+1)); [ $i -gt 600 ] && exit 1; done
        mv "$TARGET" "$WORK/old.app" || exit 1
        if mv "$NEW" "$TARGET"; then rm -rf "$WORK/old.app"; else mv "$WORK/old.app" "$TARGET"; fi
        open "$TARGET"
        rm -rf "$WORK"
        """
        do {
            try body.write(to: script, atomically: true, encoding: .utf8)
            let task = Process()
            task.executableURL = URL(fileURLWithPath: "/bin/sh")
            task.arguments = [script.path, String(ProcessInfo.processInfo.processIdentifier), p.newApp.path, p.target.path, p.work.path]
            try task.run()
        } catch {
            NSLog("KeyCue update: could not start swap script: \(error)")
        }
    }

    private func download(_ url: URL, to directory: URL) async throws -> URL {
        let destination = directory.appendingPathComponent("KeyCue.zip")
        let delegate = DownloadProgress { [weak self] fraction in
            DispatchQueue.main.async { self?.setProgress(fraction, "ダウンロード中… \(Int(fraction * 100))%") }
        }
        let session = URLSession(configuration: .ephemeral, delegate: delegate, delegateQueue: nil)
        defer { session.finishTasksAndInvalidate() }
        do {
            let (temporary, response) = try await session.download(from: url)
            if let http = response as? HTTPURLResponse, http.statusCode != 200 {
                throw UpdateError.download("サーバーの応答 \(http.statusCode)")
            }
            try FileManager.default.moveItem(at: temporary, to: destination)
            return destination
        } catch let e as UpdateError {
            throw e
        } catch {
            if (error as? URLError)?.code == .cancelled { throw CancellationError() }
            throw UpdateError.download(error.localizedDescription)
        }
    }

    private final class DownloadProgress: NSObject, URLSessionDownloadDelegate {
        let onProgress: (Double) -> Void
        init(onProgress: @escaping (Double) -> Void) { self.onProgress = onProgress }

        func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData bytesWritten: Int64,
                        totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64) {
            guard totalBytesExpectedToWrite > 0 else { return }
            onProgress(Double(totalBytesWritten) / Double(totalBytesExpectedToWrite))
        }

        func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {}
    }

    // MARK: - 展開と検証

    private static func run(_ tool: String, _ args: [String]) throws -> (status: Int32, output: String) {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: tool)
        p.arguments = args
        let pipe = Pipe()
        p.standardOutput = pipe
        p.standardError = pipe
        try p.run()
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        return (p.terminationStatus, String(data: data, encoding: .utf8) ?? "")
    }

    private static func unpack(_ zip: URL, into directory: URL) throws -> URL {
        let out = directory.appendingPathComponent("unpacked")
        let r = try run("/usr/bin/ditto", ["-x", "-k", zip.path, out.path])
        guard r.status == 0 else { throw UpdateError.other("ZIP を展開できません: \(r.output)") }
        let app = out.appendingPathComponent("KeyCue.app")
        guard FileManager.default.fileExists(atPath: app.path) else { throw UpdateError.other("ZIP の中に KeyCue.app がありません") }
        return app
    }

    /// 署名が壊れていない・このアプリと同じ開発チーム・Apple の公証済み、のすべてを満たすか
    static func verify(_ app: URL) throws {
        let check = try run("/usr/bin/codesign", ["--verify", "--deep", "--strict", app.path])
        guard check.status == 0 else { throw UpdateError.untrusted("署名が正しくありません") }
        guard let mine = teamIdentifier(of: Bundle.main.bundleURL) else {
            throw UpdateError.untrusted("いま動いている KeyCue に開発元の署名がありません")
        }
        guard teamIdentifier(of: app) == mine else { throw UpdateError.untrusted("開発元がこのアプリと一致しません") }
        let gatekeeper = try run("/usr/sbin/spctl", ["--assess", "--type", "execute", "-vv", app.path])
        guard gatekeeper.status == 0, gatekeeper.output.contains("Notarized Developer ID") else {
            throw UpdateError.untrusted("Apple の公証を確認できません")
        }
    }

    static func teamIdentifier(of url: URL) -> String? {
        var code: SecStaticCode?
        guard SecStaticCodeCreateWithPath(url as CFURL, [], &code) == errSecSuccess, let code else { return nil }
        var info: CFDictionary?
        guard SecCodeCopySigningInformation(code, SecCSFlags(rawValue: kSecCSSigningInformation), &info) == errSecSuccess,
              let dict = info as? [String: Any] else { return nil }
        return dict[kSecCodeInfoTeamIdentifier as String] as? String
    }

    // MARK: - 進み具合の小さなウィンドウ

    private func showProgress(_ text: String) {
        let w = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 360, height: 110), styleMask: [.titled], backing: .buffered, defer: false)
        w.title = "KeyCue のアップデート"
        let label = NSTextField(labelWithString: text)
        label.frame = NSRect(x: 20, y: 68, width: 320, height: 20)
        let bar = NSProgressIndicator(frame: NSRect(x: 20, y: 44, width: 320, height: 16))
        bar.isIndeterminate = false
        bar.minValue = 0
        bar.maxValue = 1
        let cancel = NSButton(title: "キャンセル", target: self, action: #selector(cancelDownload))
        cancel.frame = NSRect(x: 250, y: 8, width: 96, height: 30)
        w.contentView?.addSubview(label)
        w.contentView?.addSubview(bar)
        w.contentView?.addSubview(cancel)
        w.isReleasedWhenClosed = false
        w.center()
        w.makeKeyAndOrderFront(nil)
        progressWindow = w
        progressBar = bar
        progressLabel = label
    }

    private func setProgress(_ value: Double?, _ text: String) {
        progressLabel?.stringValue = text
        if let value {
            progressBar?.isIndeterminate = false
            progressBar?.doubleValue = value
        } else {
            progressBar?.isIndeterminate = true
            progressBar?.startAnimation(nil)
        }
    }

    private func closeProgress() {
        progressWindow?.orderOut(nil)
        progressWindow = nil
    }

    @objc private func cancelDownload() {
        downloadTask?.cancel()
        closeProgress()
    }
}
