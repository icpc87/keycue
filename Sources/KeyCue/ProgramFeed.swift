import AppKit
import CoreImage
import CoreVideo

/// プログラム（出力）の絵に重ねる 1 枚
struct ProgramLayer {
    let image: CIImage?
    /// 単色で覆う（黒・白を経由する切り替え）
    let color: CIColor?
    let opacity: Float

    /// 動画の向き（スマートフォンの縦動画など）を、コマにかける変換に
    static func orient(_ t: CGAffineTransform, _ pb: CVPixelBuffer) -> CGAffineTransform {
        let deg = Int((atan2(t.b, t.a) * 180 / .pi).rounded())
        let o: CGImagePropertyOrientation = deg == 90 ? .right : deg == -90 ? .left : abs(deg) == 180 ? .down : .up
        return CIImage(cvPixelBuffer: pb).orientationTransform(for: o)
    }
}

/// プログラムの絵と音を作り、録画・配信（いくつでも同時に）と仮想カメラに渡す
final class ProgramFeed: ObservableObject {
    private weak var model: AppModel?
    @Published private(set) var outputs: [FFmpegOutput] = [] {
        didSet { snapshotLock.lock(); snapshot = outputs; snapshotLock.unlock() }
    }
    /// オーディオのスレッドから見る写し（メインを待たない）
    private let snapshotLock = NSLock()
    private var snapshot: [FFmpegOutput] = []
    /// 仮想カメラなど、録画・配信以外にもコマが要るとき
    var extraConsumer = false { didSet { if extraConsumer { ensureRunning() } else { stopIfIdle() } } }
    /// コマができるたび（仮想カメラ）。合成のキューから呼ぶ
    var onFrame: ((CVPixelBuffer) -> Void)?

    private let queue = DispatchQueue(label: "KeyCue.ProgramFeed", qos: .userInteractive)
    private var timer: DispatchSourceTimer?
    private let context = CIContext(options: [.workingColorSpace: CGColorSpace(name: CGColorSpace.sRGB)!, .cacheIntermediates: false])
    private var pool: CVPixelBufferPool?
    private var started = Date()

    init(model: AppModel) { self.model = model }

    static var width: Int { max(Int(UserDefaults.standard.double(forKey: Prefs.programWidthKey)), 320) }
    static var height: Int { max(Int(UserDefaults.standard.double(forKey: Prefs.programHeightKey)), 180) }
    static var fps: Double { max(UserDefaults.standard.double(forKey: Prefs.programFpsKey), 1) }

    // MARK: 出力の設定（全インスタンス共通）

    static var specs: [OutputSpec] {
        get {
            if let data = UserDefaults.standard.data(forKey: Prefs.outputsKey),
               let list = try? JSONDecoder().decode([OutputSpec].self, from: data) { return list }
            return [
                OutputSpec(name: "録画（H.264・MP4）", kind: .record, folder: OutputSpec.defaultFolder, videoBitrateKbps: 20000),
                OutputSpec(name: "YouTube", kind: .stream, enabled: false, url: OutputSpec.services[0].url, videoBitrateKbps: 6000),
            ]
        }
        set { UserDefaults.standard.set(try? JSONEncoder().encode(newValue), forKey: Prefs.outputsKey) }
    }

    func isActive(_ kind: OutputSpec.Kind) -> Bool { outputs.contains { $0.spec.kind == kind && ($0.state == .running) } }

    // MARK: 始める・止める

    /// その種類のオンにしてある出力をすべて始める。始められなかったものの理由を返す
    @discardableResult
    func startAll(_ kind: OutputSpec.Kind) -> [String] {
        var errors: [String] = []
        let list = ProgramFeed.specs.filter { $0.kind == kind && $0.enabled }
        if list.isEmpty {
            errors.append(kind == .record ? "オンにした録画の出力がありません（設定 › 録画・配信）" : "オンにした配信の出力がありません（設定 › 録画・配信）")
        }
        for spec in list {
            do { try start(spec) } catch { errors.append("\(spec.name): \(error.localizedDescription)") }
        }
        return errors
    }

    func start(_ spec: OutputSpec) throws {
        guard let model else { return }
        if spec.kind == .stream && spec.url.isEmpty {
            throw NSError(domain: "KeyCue", code: 3, userInfo: [NSLocalizedDescriptionKey: "配信先の URL を入れてください"])
        }
        let target = spec.kind == .record ? spec.recordingURL().path : spec.streamTarget
        let o = FFmpegOutput(spec: spec, target: target, width: ProgramFeed.width, height: ProgramFeed.height,
                             fps: ProgramFeed.fps, sampleRate: Int(model.audio.sampleRate))
        o.onChange = { [weak self] in self?.objectWillChange.send() }
        try o.start()
        outputs.append(o)
        ensureRunning()
    }

    func stopAll(_ kind: OutputSpec.Kind) {
        for o in outputs where o.spec.kind == kind { stop(o) }
    }

    /// 書き終えるまで待つので、別のスレッドで止める
    func stop(_ o: FFmpegOutput) {
        DispatchQueue.global(qos: .userInitiated).async {
            o.stop()
            DispatchQueue.main.async {
                self.outputs.removeAll { $0 === o }
                self.stopIfIdle()
            }
        }
    }

    func stopEverything() {
        for o in outputs { o.stop(wait: 5) }
        outputs = []
        extraConsumer = false
        stopIfIdle()
    }

    func clearFailed() {
        outputs.removeAll { $0.state == .failed || $0.state == .stopped }
        stopIfIdle()
    }

    private func ensureRunning() {
        guard let model else { return }
        let channels = max(2, outputs.map(\.spec.audioChannels).max() ?? 2)
        model.audio.mixer.setTap(channels: channels) { [weak self] block, frames in self?.onAudio(block, frames: frames, channels: channels) }
        model.video.captureFrames = true
        guard timer == nil else { return }
        started = Date()
        pool = nil
        let t = DispatchSource.makeTimerSource(flags: .strict, queue: queue)
        t.schedule(deadline: .now(), repeating: 1 / ProgramFeed.fps, leeway: .milliseconds(1))
        t.setEventHandler { [weak self] in self?.tick() }
        t.resume()
        timer = t
    }

    private func stopIfIdle() {
        guard outputs.isEmpty, !extraConsumer else { return }
        timer?.cancel()
        timer = nil
        model?.audio.mixer.setTap(channels: 2, nil)
        model?.video.captureFrames = false
    }

    // MARK: 音と絵

    private func onAudio(_ block: [Float], frames: Int, channels: Int) {
        snapshotLock.lock()
        let list = snapshot
        snapshotLock.unlock()
        for o in list {
            let n = o.spec.audioChannels
            if n == channels { o.pushAudio(block, frames: frames); continue }
            var b = [Float](repeating: 0, count: frames * n)
            for f in 0..<frames { for c in 0..<min(n, channels) { b[f * n + c] = block[f * channels + c] } }
            o.pushAudio(b, frames: frames)
        }
    }

    private func tick() {
        guard let model else { return }
        let w = ProgramFeed.width, h = ProgramFeed.height
        let (layers, list, fill, white) = DispatchQueue.main.sync {
            (model.video.programLayers(), outputs, Prefs.videoFill, Prefs.videoBackground == "white")
        }
        guard let pb = render(layers, width: w, height: h, fill: fill, white: white) else { return }
        onFrame?(pb)
        guard !list.isEmpty else { return }
        CVPixelBufferLockBaseAddress(pb, .readOnly)
        defer { CVPixelBufferUnlockBaseAddress(pb, .readOnly) }
        guard let base = CVPixelBufferGetBaseAddress(pb) else { return }
        let bpr = CVPixelBufferGetBytesPerRow(pb)
        var data = Data(count: w * h * 4)
        data.withUnsafeMutableBytes { dst in
            if bpr == w * 4 {
                memcpy(dst.baseAddress!, base, w * h * 4)
            } else {
                for y in 0..<h { memcpy(dst.baseAddress! + y * w * 4, base + y * bpr, w * 4) }
            }
        }
        let wall = Date().timeIntervalSince(started)
        for o in list { o.pushFrame(data, wallSeconds: wall) }
    }

    /// レイヤーを重ねて 1 枚に（GPU）
    func render(_ layers: [ProgramLayer], width w: Int, height h: Int, fill: String, white: Bool) -> CVPixelBuffer? {
        if pool == nil || CVPixelBufferPoolGetPixelBufferAttributes(pool!).flatMap({ ($0 as NSDictionary)[kCVPixelBufferWidthKey] as? Int }) != w {
            let attrs: [String: Any] = [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
                                        kCVPixelBufferWidthKey as String: w, kCVPixelBufferHeightKey as String: h,
                                        kCVPixelBufferIOSurfacePropertiesKey as String: [:]]
            CVPixelBufferPoolCreate(nil, nil, attrs as CFDictionary, &pool)
        }
        guard let pool else { return nil }
        var out: CVPixelBuffer?
        CVPixelBufferPoolCreatePixelBuffer(nil, pool, &out)
        guard let out else { return nil }
        let rect = CGRect(x: 0, y: 0, width: w, height: h)
        var result = CIImage(color: white ? .white : .black).cropped(to: rect)
        for layer in layers where layer.opacity > 0.002 {
            if let c = layer.color {
                let overlay = CIImage(color: CIColor(red: c.red, green: c.green, blue: c.blue, alpha: CGFloat(layer.opacity))).cropped(to: rect)
                result = overlay.composited(over: result)
                continue
            }
            guard let image = layer.image else { continue }
            let e = image.extent
            guard e.width > 0, e.height > 0 else { continue }
            var sx = CGFloat(w) / e.width, sy = CGFloat(h) / e.height
            if fill == "fit" { sx = min(sx, sy); sy = sx } else if fill == "fill" { sx = max(sx, sy); sy = sx }
            let tw = e.width * sx, th = e.height * sy
            let placed = image
                .transformed(by: CGAffineTransform(translationX: -e.minX, y: -e.minY))
                .transformed(by: CGAffineTransform(scaleX: sx, y: sy))
                .transformed(by: CGAffineTransform(translationX: (CGFloat(w) - tw) / 2, y: (CGFloat(h) - th) / 2))
            let faded = layer.opacity >= 0.998 ? placed : placed.applyingFilter("CIColorMatrix", parameters: [
                "inputAVector": CIVector(x: 0, y: 0, z: 0, w: CGFloat(layer.opacity)),
            ])
            result = faded.cropped(to: rect).composited(over: result)
        }
        context.render(result, to: out, bounds: rect, colorSpace: CGColorSpace(name: CGColorSpace.sRGB))
        return out
    }
}
