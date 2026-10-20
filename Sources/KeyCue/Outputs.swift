import Foundation
import Security

/// 録画・配信の出力 1 つ分の設定（いくつでも同時に動かせる）。Windows 版と同じ中身
struct OutputSpec: Codable, Identifiable, Equatable {
    enum Kind: String, Codable { case record, stream }

    var id = UUID().uuidString
    var name = ""
    var kind = Kind.record
    var enabled = true
    // 録画
    var folder = ""
    /// {date} {time} {name} が入れられる
    var fileName = "KeyCue_{date}_{time}"
    /// mp4 / mov / mkv
    var container = "mp4"
    // 配信
    /// rtmp(s)://…（SRT は Windows 版のみ）
    var url = ""
    // エンコード
    /// h264 / hevc / prores
    var videoCodec = "h264"
    var videoBitrateKbps = 6000
    var keyframeSeconds = 2.0
    /// aac / pcm
    var audioCodec = "aac"
    var audioBitrateKbps = 192
    var audioChannels = 2

    /// 配信キーはキーチェーンに入れる（設定ファイルに書かない）
    var streamKey: String {
        get { Keychain.get("stream-key-\(id)") ?? "" }
        nonmutating set { Keychain.set("stream-key-\(id)", newValue) }
    }

    /// 録画のファイル（日時を入れて、すでにあれば番号を付ける）
    func recordingURL(_ now: Date = Date()) -> URL {
        let d = DateFormatter()
        d.dateFormat = "yyyyMMdd"
        let t = DateFormatter()
        t.dateFormat = "HHmmss"
        var name = fileName.replacingOccurrences(of: "{date}", with: d.string(from: now))
            .replacingOccurrences(of: "{time}", with: t.string(from: now))
            .replacingOccurrences(of: "{name}", with: self.name)
        name = name.replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: ":", with: "_")
        let ext = videoCodec == "prores" && container == "mp4" ? "mov" : container
        let dir = URL(fileURLWithPath: (folder as NSString).expandingTildeInPath)
        var url = dir.appendingPathComponent("\(name).\(ext)")
        var i = 2
        while FileManager.default.fileExists(atPath: url.path) {
            url = dir.appendingPathComponent("\(name)_\(i).\(ext)")
            i += 1
        }
        return url
    }

    /// 配信先（URL の後ろにキー）
    var streamTarget: String {
        let key = streamKey
        guard !key.isEmpty else { return url }
        return (url.hasSuffix("/") ? String(url.dropLast()) : url) + "/" + key
    }

    static let services: [(name: String, url: String)] = [
        ("YouTube", "rtmp://a.rtmp.youtube.com/live2"),
        ("YouTube（RTMPS）", "rtmps://a.rtmps.youtube.com:443/live2"),
        ("Twitch", "rtmp://live.twitch.tv/app"),
        ("Facebook", "rtmps://live-api-s.facebook.com:443/rtmp"),
        ("X（Twitter）", "rtmps://jp.pscp.tv:443/x"),
    ]

    static var defaultFolder: String {
        (FileManager.default.urls(for: .moviesDirectory, in: .userDomainMask).first?.appendingPathComponent("KeyCue").path) ?? "~/Movies/KeyCue"
    }

    // MARK: ffmpeg の引数

    /// 映像は標準入力から BGRA、音は名前付きパイプから Float。エンコードは VideoToolbox / AudioToolbox
    func ffmpegArguments(width: Int, height: Int, fps: Double, sampleRate: Int, audioPipe: String, target: String) -> [String] {
        var a = ["-hide_banner", "-loglevel", "error", "-nostats", "-progress", "pipe:2", "-y"]
        let rate = String(format: "%.3f", fps)
        a += ["-f", "rawvideo", "-pix_fmt", "bgra", "-s", "\(width)x\(height)", "-r", rate, "-thread_queue_size", "64", "-i", "pipe:0"]
        a += ["-f", "f32le", "-ar", "\(sampleRate)", "-ac", "\(audioChannels)", "-thread_queue_size", "256", "-i", audioPipe]
        a += ["-map", "0:v", "-map", "1:a"]
        // 色：HD の標準（BT.709）で YUV にして、そう印を付ける（印がないと QuickTime は BT.709、ffmpeg は BT.601 と見なして色がずれる）
        a += ["-vf", "scale=out_color_matrix=bt709:out_range=tv", "-color_range", "tv", "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709"]
        let gop = "\(max(1, Int((fps * max(keyframeSeconds, 0.5)).rounded())))"
        let kbps = max(videoBitrateKbps, 100)
        switch videoCodec {
        case "prores":
            a += ["-c:v", "prores_videotoolbox", "-profile:v", "hq"]
        case "hevc":
            a += ["-c:v", "hevc_videotoolbox", "-realtime", "1", "-allow_sw", "1", "-g", gop, "-pix_fmt", "nv12", "-tag:v", "hvc1",
                  "-b:v", "\(kbps)k", "-maxrate", "\(kbps)k", "-bufsize", "\(kbps * 2)k"]
        default:
            a += ["-c:v", "h264_videotoolbox", "-realtime", "1", "-allow_sw", "1", "-g", gop, "-pix_fmt", "nv12", "-profile:v", "high",
                  "-b:v", "\(kbps)k", "-maxrate", "\(kbps)k", "-bufsize", "\(kbps * 2)k"]
        }
        if audioCodec == "pcm" && kind == .record && container != "mp4" {
            a += ["-c:a", "pcm_s24le"]
        } else {
            a += ["-c:a", "aac_at", "-b:a", "\(max(audioBitrateKbps, 32))k"]
        }
        if kind == .record {
            if container == "mp4" || container == "mov" { a += ["-movflags", "+faststart"] }
            a.append(target)
        } else {
            a += ["-f", "flv", "-flvflags", "no_duration_filesize", target]
        }
        return a
    }
}

/// ffmpeg を動かして、映像（標準入力）と音（名前付きパイプ）を流し込む。
/// 映像のコマ数は音の量に合わせて足したり減らしたりする（長く回しても音と映像がずれないように）
final class FFmpegOutput {
    enum State: Equatable { case running, stopping, stopped, failed }

    let spec: OutputSpec
    let target: String
    private(set) var state = State.running
    private(set) var error: String?
    private(set) var bitrateKbps = 0.0
    private(set) var encodeFps = 0.0
    private(set) var droppedFrames = 0
    let startedAt = Date()
    var onChange: (() -> Void)?

    private let width: Int, height: Int, fps: Double, sampleRate: Int
    private let process = Process()
    private let stdin = Pipe()
    private let stderr = Pipe()
    private let audioPipe: String
    private let videoQueue = DispatchQueue(label: "KeyCue.Output.Video")
    private let audioQueue = DispatchQueue(label: "KeyCue.Output.Audio")
    private let lock = NSLock()
    private var audioFrames = 0
    private var videoFrames = 0
    private var pendingVideo = 0
    private var pendingAudio = 0
    private var audioHandle: FileHandle?
    private var stopping = false
    private var log: [String] = []

    static var ffmpegURL: URL { Bundle.main.bundleURL.appendingPathComponent("Contents/MacOS/ffmpeg") }

    init(spec: OutputSpec, target: String, width: Int, height: Int, fps: Double, sampleRate: Int) {
        self.spec = spec
        self.target = target
        self.width = width
        self.height = height
        self.fps = fps
        self.sampleRate = sampleRate
        audioPipe = NSTemporaryDirectory() + "keycue-audio-\(UUID().uuidString)"
    }

    func start() throws {
        guard FileManager.default.isExecutableFile(atPath: FFmpegOutput.ffmpegURL.path) else {
            throw NSError(domain: "KeyCue", code: 1, userInfo: [NSLocalizedDescriptionKey: "ffmpeg が見つかりません（KeyCue を入れ直してください）"])
        }
        if spec.kind == .record {
            try FileManager.default.createDirectory(at: URL(fileURLWithPath: target).deletingLastPathComponent(), withIntermediateDirectories: true)
        }
        guard mkfifo(audioPipe, 0o600) == 0 else {
            throw NSError(domain: "KeyCue", code: 2, userInfo: [NSLocalizedDescriptionKey: "音のパイプを作れません"])
        }
        process.executableURL = FFmpegOutput.ffmpegURL
        process.arguments = spec.ffmpegArguments(width: width, height: height, fps: fps, sampleRate: sampleRate, audioPipe: audioPipe, target: target)
        process.standardInput = stdin
        process.standardError = stderr
        process.standardOutput = FileHandle.nullDevice
        process.terminationHandler = { [weak self] p in self?.exited(code: p.terminationStatus) }
        try process.run()
        // パイプが閉じられても落ちないように（書けなければエラーとして扱う）
        signal(SIGPIPE, SIG_IGN)
        stderr.fileHandleForReading.readabilityHandler = { [weak self] h in self?.read(h.availableData) }
        let path = audioPipe
        audioQueue.async { [weak self] in
            // 読む側（ffmpeg）が開くまで待つ
            let h = FileHandle(forWritingAtPath: path)
            self?.lock.lock()
            self?.audioHandle = h
            self?.lock.unlock()
        }
    }

    /// プログラムの音（出力の 1 ch 目から、interleave の Float）。オーディオのスレッドから
    func pushAudio(_ block: [Float], frames: Int) {
        lock.lock()
        guard state == .running, !stopping, pendingAudio < 400 else { lock.unlock(); return }
        pendingAudio += 1
        audioFrames += frames
        lock.unlock()
        audioQueue.async { [weak self] in
            guard let self else { return }
            defer { self.lock.lock(); self.pendingAudio -= 1; self.lock.unlock() }
            guard let h = self.audioHandle else { return }
            let data = block.withUnsafeBufferPointer { Data(buffer: $0) }
            do { try h.write(contentsOf: data) } catch { self.fail("音を送れません: \(error.localizedDescription)") }
        }
    }

    /// 新しいプログラムの絵。音の量に合うだけコマを送る（足りなければ同じ絵を繰り返す）
    func pushFrame(_ frame: Data, wallSeconds: Double) {
        lock.lock()
        guard state == .running, !stopping else { lock.unlock(); return }
        let target = audioFrames > 0 ? Int(Double(audioFrames) * fps / Double(sampleRate)) : Int(wallSeconds * fps)
        var send = 0
        while videoFrames < target && send < 10 {
            if pendingVideo + send >= 6 { droppedFrames += 1; videoFrames = target; break }
            send += 1
            videoFrames += 1
        }
        pendingVideo += send
        lock.unlock()
        guard send > 0 else { return }
        let handle = stdin.fileHandleForWriting
        videoQueue.async { [weak self] in
            for _ in 0..<send {
                do { try handle.write(contentsOf: frame) } catch { self?.fail("映像を送れません: \(error.localizedDescription)"); break }
            }
            self?.lock.lock(); self?.pendingVideo -= send; self?.lock.unlock()
        }
    }

    private func read(_ data: Data) {
        guard !data.isEmpty, let text = String(data: data, encoding: .utf8) else { return }
        var changed = false
        for line in text.split(separator: "\n") {
            let s = String(line)
            if let eq = s.firstIndex(of: "=") {
                let key = String(s[..<eq]), value = s[s.index(after: eq)...].trimmingCharacters(in: .whitespaces)
                if key == "bitrate", let b = Double(value.replacingOccurrences(of: "kbits/s", with: "")) { bitrateKbps = b }
                else if key == "fps", let f = Double(value) { encodeFps = f }
                else if key == "progress" { changed = true }
                continue
            }
            log.append(s)
            if log.count > 30 { log.removeFirst() }
        }
        if changed { DispatchQueue.main.async { self.onChange?() } }
    }

    var lastLog: String { log.suffix(6).joined(separator: "\n") }

    private func fail(_ message: String) {
        lock.lock()
        guard state != .failed, !stopping else { lock.unlock(); return }
        state = .failed
        error = message + (lastLog.isEmpty ? "" : "\n" + lastLog)
        lock.unlock()
        DispatchQueue.main.async { self.onChange?() }
    }

    private func exited(code: Int32) {
        stderr.fileHandleForReading.readabilityHandler = nil
        try? FileManager.default.removeItem(atPath: audioPipe)
        lock.lock()
        let wasStopping = stopping
        lock.unlock()
        if !wasStopping { fail("ffmpeg が止まりました（\(code)）") }
        lock.lock()
        if state != .failed { state = .stopped }
        lock.unlock()
        DispatchQueue.main.async { self.onChange?() }
    }

    /// 止める：入力を閉じて、ffmpeg にファイルを書き終えさせる（終わるまで待つ）
    func stop(wait: TimeInterval = 15) {
        lock.lock()
        if stopping { lock.unlock(); return }
        stopping = true
        if state == .running { state = .stopping }
        lock.unlock()
        DispatchQueue.main.async { self.onChange?() }
        lock.lock(); let opened = audioHandle != nil; lock.unlock()
        if !opened {
            // ffmpeg がまだ音のパイプを開いていない：こちらで読む側を開いて閉じ、書く側の待ちを解く
            let fd = open(audioPipe, O_RDONLY | O_NONBLOCK)
            if fd >= 0 { close(fd) }
        }
        videoQueue.sync {}
        audioQueue.sync {}
        try? stdin.fileHandleForWriting.close()
        lock.lock(); let h = audioHandle; lock.unlock()
        try? h?.close()
        let deadline = Date().addingTimeInterval(wait)
        while process.isRunning && Date() < deadline { Thread.sleep(forTimeInterval: 0.05) }
        if process.isRunning { process.terminate() }
        lock.lock()
        if state != .failed { state = .stopped }
        lock.unlock()
        DispatchQueue.main.async { self.onChange?() }
    }
}

/// 配信キーをキーチェーンに入れる
enum Keychain {
    private static let service = "jp.ic2.keycue"

    static func get(_ account: String) -> String? {
        let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                kSecAttrAccount as String: account, kSecReturnData as String: true, kSecMatchLimit as String: kSecMatchLimitOne]
        var out: AnyObject?
        guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess, let data = out as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    static func set(_ account: String, _ value: String) {
        let base: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: account]
        SecItemDelete(base as CFDictionary)
        guard !value.isEmpty else { return }
        var add = base
        add[kSecValueData as String] = Data(value.utf8)
        SecItemAdd(add as CFDictionary, nil)
    }
}
