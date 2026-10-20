import AppKit
import AVFoundation
import CoreMedia
import QuartzCore

enum VideoTarget: Hashable {
    case window
    case display(String)

    var stored: String {
        switch self {
        case .window: return "window"
        case .display(let name): return "display:" + name
        }
    }

    init(stored: String) {
        self = stored.hasPrefix("display:") ? .display(String(stored.dropFirst(8))) : .window
    }
}

/// 黒背景に AVPlayerLayer を重ねる面（出力ウィンドウ用とアプリ内モニター用）
final class VideoSurfaceView: NSView {
    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        applyAppearance()
    }

    required init?(coder: NSCoder) { fatalError() }

    static var videoGravity: AVLayerVideoGravity {
        switch Prefs.videoFill {
        case "fill": return .resizeAspectFill
        case "stretch": return .resize
        default: return .resizeAspect
        }
    }

    static var contentsGravity: CALayerContentsGravity {
        switch Prefs.videoFill {
        case "fill": return .resizeAspectFill
        case "stretch": return .resize
        default: return .resizeAspect
        }
    }

    static var backgroundColor: CGColor { Prefs.videoBackground == "white" ? NSColor.white.cgColor : NSColor.black.cgColor }

    /// 設定（映り方・背景色）を今ある層にも当てる
    func applyAppearance() {
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        layer?.backgroundColor = VideoSurfaceView.backgroundColor
        layer?.masksToBounds = true
        for l in layer?.sublayers ?? [] {
            if let c = l as? AVCaptureVideoPreviewLayer { c.videoGravity = VideoSurfaceView.videoGravity; continue }
            if let p = l as? AVPlayerLayer { p.videoGravity = VideoSurfaceView.videoGravity } else { l.contentsGravity = VideoSurfaceView.contentsGravity }
        }
        CATransaction.commit()
    }

    func addPlayerLayer(for player: AVPlayer, opacity: Float, z: CGFloat) -> AVPlayerLayer {
        let l = AVPlayerLayer(player: player)
        l.videoGravity = VideoSurfaceView.videoGravity
        l.zPosition = z
        l.frame = bounds
        l.autoresizingMask = [.layerWidthSizable, .layerHeightSizable]
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        l.opacity = opacity
        layer?.addSublayer(l)
        CATransaction.commit()
        return l
    }

    /// カメラ・キャプチャ機器の映像
    func addPreviewLayer(for session: AVCaptureSession, opacity: Float, z: CGFloat) -> AVCaptureVideoPreviewLayer {
        let l = AVCaptureVideoPreviewLayer(session: session)
        l.videoGravity = VideoSurfaceView.videoGravity
        l.zPosition = z
        l.frame = bounds
        l.autoresizingMask = [.layerWidthSizable, .layerHeightSizable]
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        l.opacity = opacity
        layer?.addSublayer(l)
        CATransaction.commit()
        return l
    }

    func addImageLayer(opacity: Float, z: CGFloat) -> CALayer {
        let l = CALayer()
        l.contentsGravity = VideoSurfaceView.contentsGravity
        l.zPosition = z
        l.frame = bounds
        l.autoresizingMask = [.layerWidthSizable, .layerHeightSizable]
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        l.opacity = opacity
        layer?.addSublayer(l)
        CATransaction.commit()
        return l
    }
}

/// 出力に出しているライブ入力の映像
final class LiveVideoCue {
    let session: AVCaptureSession
    let deviceID: String
    var layers: [AVCaptureVideoPreviewLayer] = []
    var stopping = false
    var finished = false
    var outputs: Set<String>?

    init(session: AVCaptureSession, deviceID: String) {
        self.session = session
        self.deviceID = deviceID
    }
}

/// 出力に出している 1 枚の画像（スライド素材）
final class ImageCue {
    var layers: [CALayer] = []
    var stopping = false
    var finished = false
    /// 出す出力（nil = すべての出力）
    var outputs: Set<String>?
}

/// 全画面出力用。キー入力はメインウィンドウに残す
private final class BorderlessOutputWindow: NSWindow {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

final class VideoCue {
    let player: AVPlayer
    let loop: Bool
    let usesPlayerAudio: Bool
    var volume: Float
    var layers: [AVPlayerLayer] = []
    var started = false
    var stopping = false
    var finished = false
    /// 出す出力（nil = すべての出力）
    var outputs: Set<String>?
    var observers: [NSObjectProtocol] = []
    var statusObservation: NSKeyValueObservation?
    var volumeTimer: Timer?
    /// 録画・配信用：いまのコマを取り出す口と、映像の向き
    var frameOutput: AVPlayerItemVideoOutput?
    var lastBuffer: CVPixelBuffer?
    var transform: CGAffineTransform = .identity

    init(player: AVPlayer, loop: Bool, usesPlayerAudio: Bool, volume: Float) {
        self.player = player
        self.loop = loop
        self.usesPlayerAudio = usesPlayerAudio
        self.volume = volume
    }

    var progress: Double {
        let d = duration
        guard d > 0 else { return 0 }
        return min(max(player.currentTime().seconds / d, 0), 1)
    }

    var duration: Double {
        guard let d = player.currentItem?.duration.seconds, d.isFinite, d > 0 else { return 0 }
        return d
    }
}

/// 出力 1 つ分（ウィンドウ、またはディスプレイ全画面）
private final class OutputSurface {
    let id: String
    let window: NSWindow
    let view: VideoSurfaceView

    init(id: String, window: NSWindow, view: VideoSurfaceView) {
        self.id = id
        self.window = window
        self.view = view
    }
}

/// 映像出力（ウィンドウ・複数のディスプレイ全画面に同時に出せる）とアプリ内モニター
final class VideoOutput: NSObject, ObservableObject, NSWindowDelegate {
    /// ウィンドウ出力の ID。ディスプレイは screenNames のラベルを ID に使う
    static let windowID = "window"

    /// 出す先（"window" とディスプレイのラベル）。複数選べる
    @Published var outputs: [String] = [VideoOutput.windowID] {
        didSet { if oldValue != outputs { rebuildOutputs() } }
    }
    @Published private(set) var isShowing = false
    @Published private(set) var screenNames: [String] = []

    let monitorView = VideoSurfaceView(frame: NSRect(x: 0, y: 0, width: 320, height: 180))
    /// レイヤー表示の上に出すプログラム（ライブ）のモニター
    let programView = VideoSurfaceView(frame: NSRect(x: 0, y: 0, width: 320, height: 180))
    private var surfaces: [OutputSurface] = []
    private var cues: [VideoCue] = []
    private var imageCues: [ImageCue] = []
    private var liveCues: [LiveVideoCue] = []
    /// 録画・配信をしているあいだは、各映像のいまのコマを取り出す
    var captureFrames = false {
        didSet {
            guard oldValue != captureFrames else { return }
            if captureFrames { cues.forEach(attachFrameOutput) }
            captureHub.setFrameTap(captureFrames)
        }
    }

    private func attachFrameOutput(_ cue: VideoCue) {
        guard cue.frameOutput == nil, let item = cue.player.currentItem else { return }
        let out = AVPlayerItemVideoOutput(pixelBufferAttributes: [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA])
        item.add(out)
        cue.frameOutput = out
        cue.transform = item.tracks.first { $0.assetTrack?.mediaType == .video }?.assetTrack?.preferredTransform ?? .identity
    }

    /// いまのプログラムに重なっているもの（奥から順）。録画・配信の絵を作るのに使う。メインスレッドから
    func programLayers() -> [ProgramLayer] {
        let now = CACurrentMediaTime()
        var out: [(z: CGFloat, layer: ProgramLayer)] = []
        func opacity(_ l: CALayer?) -> Float { l?.presentation()?.opacity ?? l?.opacity ?? 0 }
        for cue in cues where !cue.finished {
            if let o = cue.frameOutput {
                let t = o.itemTime(forHostTime: now)
                if o.hasNewPixelBuffer(forItemTime: t), let pb = o.copyPixelBuffer(forItemTime: t, itemTimeForDisplay: nil) { cue.lastBuffer = pb }
            }
            guard let pb = cue.lastBuffer else { continue }
            out.append((cue.layers.first?.zPosition ?? 0, ProgramLayer(image: CIImage(cvPixelBuffer: pb).transformed(by: cue.transform.isIdentity ? .identity : ProgramLayer.orient(cue.transform, pb)), color: nil, opacity: opacity(cue.layers.first))))
        }
        for cue in imageCues where !cue.finished {
            let l = cue.layers.first
            if let bg = l?.backgroundColor, l?.contents == nil {
                out.append((l?.zPosition ?? 0, ProgramLayer(image: nil, color: CIColor(cgColor: bg), opacity: opacity(l))))
            } else if let contents = l?.contents, CFGetTypeID(contents as CFTypeRef) == CGImage.typeID {
                out.append((l?.zPosition ?? 0, ProgramLayer(image: CIImage(cgImage: contents as! CGImage), color: nil, opacity: opacity(l))))
            }
        }
        for cue in liveCues where !cue.finished {
            guard let pb = captureHub.latestFrame(cue.deviceID) else { continue }
            out.append((cue.layers.first?.zPosition ?? 0, ProgramLayer(image: CIImage(cvPixelBuffer: pb), color: nil, opacity: opacity(cue.layers.first))))
        }
        return out.sorted { $0.z < $1.z }.map(\.layer)
    }
    /// 映像入力のセッション（同じ機器はまとめる）
    let captureHub = VideoCaptureHub()
    private var prepared: [String: AVPlayer] = [:]

    var instanceNumber = 1
    var audioDeviceUID: String?
    var audioClock: CMClock?

    /// 最初の出力の面（テストや確認用）。出力がなければモニター
    var outputView: VideoSurfaceView { surfaces.first?.view ?? monitorView }
    /// いま開いている出力の窓
    var windows: [NSWindow] { surfaces.map(\.window) }
    var window: NSWindow? { surfaces.first { $0.id == VideoOutput.windowID }?.window }

    override init() {
        super.init()
        refreshScreens()
        NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification,
                                               object: nil, queue: .main) { [weak self] _ in
            guard let self else { return }
            self.refreshScreens()
            // 抜き差しや解像度変更：全画面の窓を作り直す
            for s in self.surfaces where s.id != VideoOutput.windowID { self.removeSurface(s) }
            self.rebuildOutputs()
        }
    }

    // MARK: - 出力先

    static func screenLabel(_ screen: NSScreen, index: Int) -> String {
        let size = screen.frame.size
        return "\(index + 1): \(screen.localizedName)（\(Int(size.width))×\(Int(size.height))）"
    }

    func refreshScreens() {
        screenNames = NSScreen.screens.enumerated().map { VideoOutput.screenLabel($1, index: $0) }
    }

    func screen(named label: String) -> NSScreen? {
        let screens = NSScreen.screens
        if let i = screenNames.firstIndex(of: label), i < screens.count { return screens[i] }
        // 並びが変わっていたら名前だけで探す
        let bare = label.split(separator: ":", maxSplits: 1).last.map { $0.trimmingCharacters(in: .whitespaces) } ?? label
        return screens.first { bare.hasPrefix($0.localizedName) }
    }

    /// このアプリのメインウィンドウがある画面か（そこを全画面にすると操作画面が隠れる）
    func isControlScreen(_ label: String) -> Bool {
        guard let screen = screen(named: label) else { return false }
        return NSApp.windows.contains { $0.title.hasPrefix("KeyCue") && $0.isVisible && $0.screen == screen }
    }

    func isOn(_ id: String) -> Bool { outputs.contains(id) }

    func setOutput(_ id: String, on: Bool) {
        if on, !outputs.contains(id) { outputs.append(id) }
        if !on { outputs.removeAll { $0 == id } }
        if on && !isShowing { show() }
    }

    /// 選んでいるのに今はつながっていないディスプレイ
    var missingOutputs: [String] {
        outputs.filter { $0 != VideoOutput.windowID && screen(named: $0) == nil }
    }

    func show() {
        if outputs.isEmpty { outputs = [VideoOutput.windowID] }
        isShowing = true
        rebuildOutputs()
    }

    func hide() {
        isShowing = false
        rebuildOutputs()
    }

    /// 選んでいる出力に合わせて窓を作る・閉じる。作った面には再生中の素材の層も足す
    private func rebuildOutputs() {
        let wanted = isShowing ? outputs.filter { $0 == VideoOutput.windowID || screen(named: $0) != nil } : []
        for s in surfaces where !wanted.contains(s.id) { removeSurface(s) }
        for id in wanted where !surfaces.contains(where: { $0.id == id }) { addSurface(id) }
    }

    private func addSurface(_ id: String) {
        let view = VideoSurfaceView(frame: NSRect(x: 0, y: 0, width: 960, height: 540))
        let w: NSWindow
        if id == VideoOutput.windowID {
            w = NSWindow(contentRect: NSRect(x: 200, y: 200, width: 960, height: 540),
                         styleMask: [.titled, .closable, .miniaturizable, .resizable],
                         backing: .buffered, defer: false)
            w.title = "映像出力 #\(instanceNumber)"
            w.collectionBehavior = [.fullScreenPrimary]
            w.setFrameAutosaveName("KeyCueVideo-\(instanceNumber)")
        } else {
            guard let screen = screen(named: id) else { return }
            w = BorderlessOutputWindow(contentRect: screen.frame, styleMask: [.borderless], backing: .buffered, defer: false)
            w.setFrame(screen.frame, display: true)
            w.level = .screenSaver
            w.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
            w.hidesOnDeactivate = false
        }
        w.isReleasedWhenClosed = false
        w.backgroundColor = Prefs.videoBackground == "white" ? .white : .black
        w.contentView = view
        w.delegate = self
        let surface = OutputSurface(id: id, window: w, view: view)
        surfaces.append(surface)

        // 再生中の素材をこの面にも出す
        for cue in cues where !cue.finished && VideoOutput.wants(cue.outputs, id) {
            let ref = cue.layers.first
            let l = view.addPlayerLayer(for: cue.player, opacity: ref?.opacity ?? 1, z: ref?.zPosition ?? 0)
            cue.layers.append(l)
        }
        for cue in imageCues where !cue.finished && VideoOutput.wants(cue.outputs, id) {
            let ref = cue.layers.first
            let l = view.addImageLayer(opacity: ref?.opacity ?? 1, z: ref?.zPosition ?? 0)
            l.contents = ref?.contents
            l.backgroundColor = ref?.backgroundColor
            cue.layers.append(l)
        }
        for cue in liveCues where !cue.finished && VideoOutput.wants(cue.outputs, id) {
            let ref = cue.layers.first
            cue.layers.append(view.addPreviewLayer(for: cue.session, opacity: ref?.opacity ?? 1, z: ref?.zPosition ?? 0))
        }

        if w is BorderlessOutputWindow { w.orderFrontRegardless() } else { w.orderFront(nil) }
    }

    private func removeSurface(_ s: OutputSurface) {
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        for cue in cues {
            let gone = cue.layers.filter { $0.superlayer === s.view.layer }
            gone.forEach { $0.removeFromSuperlayer() }
            cue.layers.removeAll { l in gone.contains { $0 === l } }
        }
        for cue in imageCues {
            let gone = cue.layers.filter { $0.superlayer === s.view.layer }
            gone.forEach { $0.removeFromSuperlayer() }
            cue.layers.removeAll { l in gone.contains { $0 === l } }
        }
        for cue in liveCues {
            let gone = cue.layers.filter { $0.superlayer === s.view.layer }
            gone.forEach { $0.removeFromSuperlayer() }
            cue.layers.removeAll { l in gone.contains { $0 === l } }
        }
        CATransaction.commit()
        surfaces.removeAll { $0 === s }
        s.window.delegate = nil
        s.window.orderOut(nil)
    }

    private static func wants(_ cueOutputs: Set<String>?, _ id: String) -> Bool {
        guard let cueOutputs else { return true }
        return cueOutputs.contains(id)
    }

    /// 素材を出す面：選んだ出力（素材ごとの指定があればその中だけ）とアプリ内モニター
    private func targetViews(_ cueOutputs: Set<String>?) -> [VideoSurfaceView] {
        surfaces.filter { VideoOutput.wants(cueOutputs, $0.id) }.map(\.view) + [monitorView, programView]
    }

    func windowWillClose(_ notification: Notification) {
        // 映像出力ウィンドウを閉じたら、ウィンドウ出力をやめる
        guard let w = notification.object as? NSWindow, let s = surfaces.first(where: { $0.window === w }) else { return }
        removeSurface(s)
        outputs.removeAll { $0 == s.id }
        if surfaces.isEmpty { isShowing = false }
    }

    /// 各ディスプレイに大きく番号を出して、どれが何番か分かるようにする
    func identifyScreens() {
        for (i, screen) in NSScreen.screens.enumerated() {
            let w = BorderlessOutputWindow(contentRect: screen.frame, styleMask: [.borderless], backing: .buffered, defer: false)
            w.setFrame(screen.frame, display: false)
            w.level = .screenSaver
            w.isOpaque = false
            w.backgroundColor = NSColor.black.withAlphaComponent(0.55)
            w.isReleasedWhenClosed = false
            w.ignoresMouseEvents = true
            let label = NSTextField(labelWithString: "\(i + 1)\n\(screen.localizedName)")
            label.alignment = .center
            label.font = .systemFont(ofSize: min(screen.frame.height * 0.3, 260), weight: .bold)
            label.textColor = .white
            label.maximumNumberOfLines = 2
            let sub = NSFont.systemFont(ofSize: 36, weight: .medium)
            let text = NSMutableAttributedString(string: "\(i + 1)\n", attributes: [.font: label.font!, .foregroundColor: NSColor.white])
            text.append(NSAttributedString(string: screen.localizedName + "（\(Int(screen.frame.width))×\(Int(screen.frame.height))）",
                                           attributes: [.font: sub, .foregroundColor: NSColor.white]))
            label.attributedStringValue = text
            label.frame = NSRect(origin: .zero, size: screen.frame.size)
            let container = NSView(frame: NSRect(origin: .zero, size: screen.frame.size))
            label.sizeToFit()
            label.setFrameOrigin(NSPoint(x: (screen.frame.width - label.frame.width) / 2, y: (screen.frame.height - label.frame.height) / 2))
            container.addSubview(label)
            w.contentView = container
            w.orderFrontRegardless()
            DispatchQueue.main.asyncAfter(deadline: .now() + 2.5) { w.orderOut(nil) }
        }
    }

    // MARK: - 再生

    private func makePlayer(_ url: URL) -> AVPlayer {
        let player = AVPlayer(playerItem: AVPlayerItem(url: url))
        player.automaticallyWaitsToMinimizeStalling = false
        player.actionAtItemEnd = .pause
        player.isMuted = true
        if let audioClock { player.sourceClock = audioClock }
        return player
    }

    /// すぐ鳴らせるようにプレーヤーを用意しておく
    func prepare(_ url: URL) {
        if let p = prepared[url.path], p.currentItem?.status != .failed { return }
        prepared[url.path] = makePlayer(url)
    }

    func forget(_ url: URL) { prepared[url.path] = nil }

    func applyAppearance() {
        monitorView.applyAppearance()
        programView.applyAppearance()
        for s in surfaces {
            s.view.applyAppearance()
            s.window.backgroundColor = Prefs.videoBackground == "white" ? .white : .black
        }
    }

    func resetPrepared() {
        let urls = prepared.keys.map { URL(fileURLWithPath: $0) }
        prepared.removeAll()
        urls.forEach(prepare)
    }

    /// onStart には映像が動き始める host time が渡される（音声をそこに合わせる）
    /// holdAtEnd なら終わっても最後のフレームを残して onEnd を呼ぶ（止めるのは呼び出し側）
    func play(url: URL, fadeIn: Double, loop: Bool, playerAudio: Bool, volume: Float, z: CGFloat,
              outputs cueOutputs: Set<String>? = nil,
              holdAtEnd: Bool = false, onEnd: (() -> Void)? = nil,
              onStart: @escaping (UInt64) -> Void, onFail: @escaping () -> Void) -> VideoCue {
        if !isShowing { show() }
        let player = prepared.removeValue(forKey: url.path) ?? makePlayer(url)
        prepare(url)

        player.isMuted = !playerAudio
        player.volume = playerAudio ? (fadeIn > 0 ? 0 : volume) : 0
        if playerAudio { player.audioOutputDeviceUniqueID = audioDeviceUID }

        let cue = VideoCue(player: player, loop: loop, usesPlayerAudio: playerAudio, volume: volume)
        if captureFrames { attachFrameOutput(cue) }
        let startOpacity: Float = fadeIn > 0 ? 0 : 1
        cue.outputs = cueOutputs
        cue.layers = targetViews(cueOutputs).map { $0.addPlayerLayer(for: player, opacity: startOpacity, z: z) }
        cues.append(cue)

        if let item = player.currentItem {
            cue.observers.append(NotificationCenter.default.addObserver(forName: .AVPlayerItemDidPlayToEndTime, object: item,
                                                                        queue: .main) { [weak self, weak cue] _ in
                guard let self, let cue else { return }
                if cue.loop && !cue.stopping {
                    cue.player.seek(to: .zero)
                    cue.player.play()
                } else if holdAtEnd {
                    onEnd?()
                } else {
                    self.finish(cue)
                    onEnd?()
                }
            })
        }

        let begin: () -> Void = { [weak cue] in
            guard let cue, !cue.started, !cue.stopping, !cue.finished else { return }
            cue.started = true
            let lead = 0.08
            let host = HostTime.now(plus: lead)
            cue.player.setRate(1, time: .zero, atHostTime: CMClockMakeHostTimeFromSystemUnits(host))
            onStart(host)
            if fadeIn > 0 {
                // フェードは映像・音が動き出すのと同時に始める
                VideoOutput.animate(cue.layers, to: 1, duration: fadeIn, delay: lead)
                if cue.usesPlayerAudio { VideoOutput.ramp(cue, to: cue.volume, duration: fadeIn) }
            }
        }

        switch player.currentItem?.status {
        case .readyToPlay:
            begin()
        case .failed, .none:
            finish(cue)
            onFail()
        default:
            cue.statusObservation = player.currentItem?.observe(\.status, options: [.new]) { [weak self, weak cue] item, _ in
                DispatchQueue.main.async {
                    guard let cue else { return }
                    if item.status == .readyToPlay {
                        cue.statusObservation = nil
                        begin()
                    } else if item.status == .failed {
                        cue.statusObservation = nil
                        self?.finish(cue)
                        onFail()
                    }
                }
            }
        }
        return cue
    }

    func stop(_ cue: VideoCue, fade: Double) {
        guard !cue.stopping, !cue.finished else { return }
        cue.stopping = true
        if !cue.started || fade <= 0 {
            finish(cue)
            return
        }
        VideoOutput.animate(cue.layers, to: 0, duration: fade)
        if cue.usesPlayerAudio { VideoOutput.ramp(cue, to: 0, duration: fade) }
        // フェードが終わったら必ず止めて片付ける（cue は強く持つ）
        DispatchQueue.main.asyncAfter(deadline: .now() + fade) { [weak self] in
            self?.finish(cue)
        }
    }

    /// seconds から hostTime に再生し直す（音声も同じ hostTime に合わせて鳴らす）。始まっていなければ false
    @discardableResult
    func seek(_ cue: VideoCue, to seconds: Double, atHostTime host: UInt64) -> Bool {
        guard cue.started, !cue.stopping, !cue.finished, cue.player.currentItem?.status == .readyToPlay else { return false }
        cue.player.setRate(1, time: CMTime(seconds: max(seconds, 0), preferredTimescale: 600),
                           atHostTime: CMClockMakeHostTimeFromSystemUnits(host))
        return true
    }

    func setVolume(_ cue: VideoCue, volume: Float) {
        cue.volume = volume
        if cue.usesPlayerAudio && !cue.stopping { cue.player.volume = volume }
    }

    func finish(_ cue: VideoCue) {
        guard !cue.finished else { return }
        cue.finished = true
        cue.player.pause()
        cue.volumeTimer?.invalidate()
        cue.statusObservation = nil
        cue.observers.forEach { NotificationCenter.default.removeObserver($0) }
        cue.observers.removeAll()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        cue.layers.forEach { $0.removeFromSuperlayer() }
        CATransaction.commit()
        cue.layers.removeAll()
        cues.removeAll { $0 === cue }
    }

    // MARK: - 画像

    /// load で画像を用意して、読み込めたらフェードインで出す
    func showImage(load: (@escaping (CGImage?) -> Void) -> Void, fadeIn: Double, z: CGFloat,
                   outputs cueOutputs: Set<String>? = nil) -> ImageCue {
        if !isShowing { show() }
        let cue = ImageCue()
        cue.outputs = cueOutputs
        cue.layers = targetViews(cueOutputs).map { $0.addImageLayer(opacity: 0, z: z) }
        imageCues.append(cue)
        load { [weak cue] image in
            guard let cue, !cue.finished else { return }
            CATransaction.begin()
            CATransaction.setDisableActions(true)
            cue.layers.forEach { $0.contents = image }
            CATransaction.commit()
            if !cue.stopping { VideoOutput.animate(cue.layers, to: 1, duration: fadeIn) }
        }
        return cue
    }

    /// 画面を 1 色で覆う（黒・白を経由する切り替え用）。止めるときは stop(_:fade:) で
    func showColor(_ color: CGColor, fadeIn: Double, z: CGFloat, outputs cueOutputs: Set<String>? = nil) -> ImageCue {
        if !isShowing { show() }
        let cue = ImageCue()
        cue.outputs = cueOutputs
        cue.layers = targetViews(cueOutputs).map { v in
            let l = v.addImageLayer(opacity: 0, z: z)
            l.backgroundColor = color
            return l
        }
        imageCues.append(cue)
        VideoOutput.animate(cue.layers, to: 1, duration: fadeIn)
        return cue
    }

    func stop(_ cue: ImageCue, fade: Double) {
        guard !cue.stopping, !cue.finished else { return }
        cue.stopping = true
        guard fade > 0 else { finish(cue); return }
        VideoOutput.animate(cue.layers, to: 0, duration: fade)
        // 呼び出し側が手放しても、フェードが終わったら必ず層を片付ける（cue は強く持つ）
        DispatchQueue.main.asyncAfter(deadline: .now() + fade) { [weak self] in
            self?.finish(cue)
        }
    }

    func finish(_ cue: ImageCue) {
        guard !cue.finished else { return }
        cue.finished = true
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        cue.layers.forEach { $0.removeFromSuperlayer() }
        CATransaction.commit()
        cue.layers.removeAll()
        imageCues.removeAll { $0 === cue }
    }

    // MARK: - ライブ入力

    /// カメラ・キャプチャ機器の映像を、選んでいる出力とモニターに出す
    func showLive(device: AVCaptureDevice, fadeIn: Double, z: CGFloat, outputs cueOutputs: Set<String>? = nil) -> LiveVideoCue? {
        guard let session = captureHub.acquire(device) else { return nil }
        if !isShowing { show() }
        let cue = LiveVideoCue(session: session, deviceID: device.uniqueID)
        cue.outputs = cueOutputs
        let start: Float = fadeIn > 0 ? 0 : 1
        cue.layers = targetViews(cueOutputs).map { $0.addPreviewLayer(for: session, opacity: start, z: z) }
        liveCues.append(cue)
        if fadeIn > 0 { VideoOutput.animate(cue.layers, to: 1, duration: fadeIn) }
        return cue
    }

    func stop(_ cue: LiveVideoCue, fade: Double) {
        guard !cue.stopping, !cue.finished else { return }
        cue.stopping = true
        guard fade > 0 else { finish(cue); return }
        VideoOutput.animate(cue.layers, to: 0, duration: fade)
        DispatchQueue.main.asyncAfter(deadline: .now() + fade) { [weak self] in self?.finish(cue) }
    }

    func finish(_ cue: LiveVideoCue) {
        guard !cue.finished else { return }
        cue.finished = true
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        cue.layers.forEach { $0.removeFromSuperlayer() }
        CATransaction.commit()
        cue.layers.removeAll()
        liveCues.removeAll { $0 === cue }
        captureHub.release(cue.deviceID)
    }

    /// 不透明度をフェードさせる。追加したばかりの層は暗黙のアニメーションが効かない（すぐ出てしまう）ので、明示的にアニメーションを付ける
    private static func animate(_ layers: [CALayer], to opacity: Float, duration: Double, delay: Double = 0) {
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        for l in layers {
            // フェードの途中なら、いま見えている濃さから続ける
            let from = l.presentation()?.opacity ?? l.opacity
            l.opacity = opacity
            l.removeAnimation(forKey: "fade")
            guard duration > 0, from != opacity else { continue }
            let a = CABasicAnimation(keyPath: "opacity")
            a.fromValue = from
            a.toValue = opacity
            a.duration = duration
            a.timingFunction = CAMediaTimingFunction(name: .linear)
            if delay > 0 {
                // 映像が動き出す時刻に合わせて始める（それまでは始めの濃さのまま）
                a.beginTime = CACurrentMediaTime() + delay
                a.fillMode = .backwards
            }
            l.add(a, forKey: "fade")
        }
        CATransaction.commit()
    }

    private static func ramp(_ cue: VideoCue, to target: Float, duration: Double) {
        cue.volumeTimer?.invalidate()
        let from = cue.player.volume
        let start = Date()
        cue.volumeTimer = Timer.scheduledTimer(withTimeInterval: 1.0 / 30, repeats: true) { [weak cue] timer in
            guard let cue else { timer.invalidate(); return }
            let t = min(Date().timeIntervalSince(start) / duration, 1)
            // 音のフェードと同じ等パワーのカーブ（上げるときは出だしからすぐ聞こえる）
            let shape = target >= from ? sin(t * .pi / 2) : 1 - cos(t * .pi / 2)
            cue.player.volume = from + (target - from) * Float(shape)
            if t >= 1 { timer.invalidate() }
        }
    }
}
