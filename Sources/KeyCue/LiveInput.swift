import AVFoundation
import CoreMedia
import Darwin

/// キーに割り当てるライブ入力（Web カメラ・キャプチャ機器の映像、オーディオ入力）
struct LiveInput: Codable, Equatable {
    var videoID: String?
    var videoName: String?
    var audioID: String?
    var audioName: String?

    var name: String {
        switch (videoName, audioName) {
        case let (v?, a?) where v != a: return "\(v) ＋ \(a)"
        case let (v?, _): return v
        case let (nil, a?): return a
        default: return "ライブ入力"
        }
    }
}

/// つながっているカメラ・キャプチャ機器・音声入力
enum CaptureDevices {
    static func video() -> [AVCaptureDevice] {
        AVCaptureDevice.DiscoverySession(deviceTypes: [.builtInWideAngleCamera, .external, .continuityCamera, .deskViewCamera],
                                         mediaType: .video, position: .unspecified).devices
    }

    static func audio() -> [AVCaptureDevice] {
        AVCaptureDevice.DiscoverySession(deviceTypes: [.microphone, .external],
                                         mediaType: .audio, position: .unspecified).devices
    }

    static func device(_ id: String?) -> AVCaptureDevice? {
        guard let id else { return nil }
        return AVCaptureDevice(uniqueID: id)
    }

    /// 音声入力の ch 数
    static func channels(_ device: AVCaptureDevice) -> Int {
        guard let asbd = CMAudioFormatDescriptionGetStreamBasicDescription(device.activeFormat.formatDescription)?.pointee else { return 2 }
        return max(1, min(Int(asbd.mChannelsPerFrame), Mixer.maxChannels))
    }

    /// カメラ・マイクの使用許可（初回はダイアログが出る）
    static func requestAccess(_ type: AVMediaType, _ done: @escaping (Bool) -> Void) {
        switch AVCaptureDevice.authorizationStatus(for: type) {
        case .authorized: done(true)
        case .notDetermined: AVCaptureDevice.requestAccess(for: type) { ok in DispatchQueue.main.async { done(ok) } }
        default: done(false)
        }
    }
}

/// 入力の音をためておくリングバッファ。キャプチャ側が書き、オーディオスレッドが読む
final class LiveAudioSource {
    let channels: Int
    private let capacity: Int
    private let buffers: [UnsafeMutablePointer<Float>]
    /// 読み出し用の作業領域（オーディオスレッドで確保しない）
    let scratch: [UnsafeMutablePointer<Float>]
    static let maxRead = 8192
    private var readIndex = 0
    private var fill = 0
    private var primed = false
    private let target: Int
    private let maxFill: Int
    private let lock: UnsafeMutablePointer<os_unfair_lock>

    init(channels: Int, sampleRate: Double) {
        self.channels = channels
        capacity = Int(sampleRate)            // 1 秒
        target = Int(sampleRate * 0.03)       // 30ms ためてから出し始める
        maxFill = Int(sampleRate * 0.15)      // 150ms を超えたら（時計のずれ）古い分を捨てる
        buffers = (0..<channels).map { _ in
            let p = UnsafeMutablePointer<Float>.allocate(capacity: Int(sampleRate))
            p.initialize(repeating: 0, count: Int(sampleRate))
            return p
        }
        scratch = (0..<channels).map { _ in
            let p = UnsafeMutablePointer<Float>.allocate(capacity: LiveAudioSource.maxRead)
            p.initialize(repeating: 0, count: LiveAudioSource.maxRead)
            return p
        }
        lock = .allocate(capacity: 1)
        lock.initialize(to: os_unfair_lock())
    }

    deinit {
        (buffers + scratch).forEach { $0.deallocate() }
        lock.deallocate()
    }

    /// キャプチャ側：チャンネルごとの Float を書く
    func write(_ data: [UnsafePointer<Float>], frames: Int) {
        guard frames > 0 else { return }
        os_unfair_lock_lock(lock)
        defer { os_unfair_lock_unlock(lock) }
        let n = min(frames, capacity)
        var writeIndex = (readIndex + fill) % capacity
        for f in 0..<n {
            for c in 0..<channels { buffers[c][writeIndex] = c < data.count ? data[c][frames - n + f] : 0 }
            writeIndex = (writeIndex + 1) % capacity
        }
        fill += n
        if fill > capacity {
            readIndex = (readIndex + fill - capacity) % capacity
            fill = capacity
        }
        if fill > maxFill {
            // 入力の時計の方が速い：遅れがたまらないよう、目標の量まで捨てる
            readIndex = (readIndex + fill - target) % capacity
            fill = target
        }
        if !primed && fill >= target { primed = true }
    }

    /// オーディオスレッド：frames 分を scratch に読み出す（足りない分は無音）
    func read(frames: Int) {
        let n = min(frames, LiveAudioSource.maxRead)
        os_unfair_lock_lock(lock)
        defer { os_unfair_lock_unlock(lock) }
        let available = primed ? min(fill, n) : 0
        for c in 0..<channels {
            var idx = readIndex
            for f in 0..<available {
                scratch[c][f] = buffers[c][idx]
                idx = (idx + 1) % capacity
            }
            if available < n { for f in available..<n { scratch[c][f] = 0 } }
        }
        readIndex = (readIndex + available) % capacity
        fill -= available
        if fill == 0 && available < n { primed = false }   // 途切れたら、またためてから出す
    }
}

/// 音声入力 1 つ分のキャプチャ
final class AudioCapture: NSObject, AVCaptureAudioDataOutputSampleBufferDelegate {
    let source: LiveAudioSource
    private let session = AVCaptureSession()
    private let queue = DispatchQueue(label: "KeyCue.AudioCapture", qos: .userInteractive)

    init?(device: AVCaptureDevice, sampleRate: Double) {
        let channels = CaptureDevices.channels(device)
        source = LiveAudioSource(channels: channels, sampleRate: sampleRate)
        super.init()
        guard let input = try? AVCaptureDeviceInput(device: device), session.canAddInput(input) else { return nil }
        session.addInput(input)
        let output = AVCaptureAudioDataOutput()
        // オーディオエンジンと同じサンプルレートの、チャンネル別の Float にしてもらう
        var settings: [String: Any] = [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVLinearPCMBitDepthKey: 32,
            AVLinearPCMIsFloatKey: true,
            AVLinearPCMIsNonInterleaved: true,
            AVLinearPCMIsBigEndianKey: false,
            AVSampleRateKey: sampleRate,
            AVNumberOfChannelsKey: channels,
        ]
        if channels > 2 {
            var layout = AudioChannelLayout()
            layout.mChannelLayoutTag = kAudioChannelLayoutTag_DiscreteInOrder | UInt32(channels)
            settings[AVChannelLayoutKey] = Data(bytes: &layout, count: MemoryLayout<AudioChannelLayout>.size)
        }
        output.audioSettings = settings
        output.setSampleBufferDelegate(self, queue: queue)
        guard session.canAddOutput(output) else { return nil }
        session.addOutput(output)
    }

    func start() { queue.async { self.session.startRunning() } }
    func stop() { queue.async { self.session.stopRunning() } }

    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) {
        let frames = CMSampleBufferGetNumSamples(sampleBuffer)
        guard frames > 0 else { return }
        var block: CMBlockBuffer?
        var sizeNeeded = 0
        CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(sampleBuffer, bufferListSizeNeededOut: &sizeNeeded, bufferListOut: nil,
                                                                bufferListSize: 0, blockBufferAllocator: nil, blockBufferMemoryAllocator: nil,
                                                                flags: 0, blockBufferOut: nil)
        let raw = UnsafeMutableRawPointer.allocate(byteCount: sizeNeeded, alignment: MemoryLayout<AudioBufferList>.alignment)
        defer { raw.deallocate() }
        let abl = raw.assumingMemoryBound(to: AudioBufferList.self)
        guard CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(sampleBuffer, bufferListSizeNeededOut: nil, bufferListOut: abl,
                                                                      bufferListSize: sizeNeeded, blockBufferAllocator: nil,
                                                                      blockBufferMemoryAllocator: nil,
                                                                      flags: kCMSampleBufferFlag_AudioBufferList_Assure16ByteAlignment,
                                                                      blockBufferOut: &block) == noErr else { return }
        let list = UnsafeMutableAudioBufferListPointer(abl)
        let channels = list.compactMap { b -> UnsafePointer<Float>? in
            b.mData.map { UnsafePointer($0.assumingMemoryBound(to: Float.self)) }
        }
        source.write(channels, frames: frames)
    }
}

/// 映像入力。同じ機器を複数のキーで使っても、セッションは 1 つにまとめる
final class VideoCaptureHub {
    private var sessions: [String: (session: AVCaptureSession, users: Int)] = [:]
    private let queue = DispatchQueue(label: "KeyCue.VideoCapture")
    /// 録画・配信用に、いまのコマを受け取る口（機器ごと）
    private var taps: [String: (output: AVCaptureVideoDataOutput, sink: FrameSink)] = [:]
    private let tapQueue = DispatchQueue(label: "KeyCue.VideoCapture.Frames")
    private(set) var tapping = false

    /// いまのコマ（録画・配信をしているときだけ）
    func latestFrame(_ deviceID: String) -> CVPixelBuffer? { taps[deviceID]?.sink.latest }

    func setFrameTap(_ on: Bool) {
        tapping = on
        for (id, entry) in sessions {
            if on { addTap(id, entry.session) } else { removeTap(id, entry.session) }
        }
    }

    private func addTap(_ id: String, _ session: AVCaptureSession) {
        guard taps[id] == nil else { return }
        let output = AVCaptureVideoDataOutput()
        output.videoSettings = [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA]
        output.alwaysDiscardsLateVideoFrames = true
        let sink = FrameSink()
        output.setSampleBufferDelegate(sink, queue: tapQueue)
        session.beginConfiguration()
        if session.canAddOutput(output) { session.addOutput(output) }
        session.commitConfiguration()
        taps[id] = (output, sink)
    }

    private func removeTap(_ id: String, _ session: AVCaptureSession) {
        guard let t = taps.removeValue(forKey: id) else { return }
        session.beginConfiguration()
        session.removeOutput(t.output)
        session.commitConfiguration()
    }

    func acquire(_ device: AVCaptureDevice) -> AVCaptureSession? {
        if var entry = sessions[device.uniqueID] {
            entry.users += 1
            sessions[device.uniqueID] = entry
            return entry.session
        }
        let session = AVCaptureSession()
        session.beginConfiguration()
        guard let input = try? AVCaptureDeviceInput(device: device), session.canAddInput(input) else {
            session.commitConfiguration()
            return nil
        }
        session.addInput(input)
        // 機器のいちばん大きい解像度（同じなら高いフレームレート）で受ける
        if let best = device.formats.max(by: { a, b in
            let da = CMVideoFormatDescriptionGetDimensions(a.formatDescription), db = CMVideoFormatDescriptionGetDimensions(b.formatDescription)
            let pa = Int(da.width) * Int(da.height), pb = Int(db.width) * Int(db.height)
            if pa != pb { return pa < pb }
            return (a.videoSupportedFrameRateRanges.map(\.maxFrameRate).max() ?? 0) < (b.videoSupportedFrameRateRanges.map(\.maxFrameRate).max() ?? 0)
        }), (try? device.lockForConfiguration()) != nil {
            device.activeFormat = best
            device.unlockForConfiguration()
        }
        session.commitConfiguration()
        sessions[device.uniqueID] = (session, 1)
        if tapping { addTap(device.uniqueID, session) }
        queue.async { session.startRunning() }
        return session
    }

    func release(_ deviceID: String) {
        guard var entry = sessions[deviceID] else { return }
        entry.users -= 1
        if entry.users <= 0 {
            sessions[deviceID] = nil
            taps[deviceID] = nil
            let s = entry.session
            queue.async { s.stopRunning() }
        } else {
            sessions[deviceID] = entry
        }
    }
}

/// カメラ・キャプチャ機器の、いちばん新しいコマを持っておく
final class FrameSink: NSObject, AVCaptureVideoDataOutputSampleBufferDelegate {
    private let lock = NSLock()
    private var buffer: CVPixelBuffer?

    var latest: CVPixelBuffer? {
        lock.lock(); defer { lock.unlock() }
        return buffer
    }

    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) {
        guard let pb = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        lock.lock(); buffer = pb; lock.unlock()
    }
}
