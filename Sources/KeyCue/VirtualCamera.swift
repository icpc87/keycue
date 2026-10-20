import AppKit
import CoreImage
import CoreMediaIO
import CoreVideo
import SystemExtensions

/// 仮想カメラ「KeyCue」。プログラムの絵を、Zoom・Teams・OBS などのカメラとして出す。
/// カメラ本体は KeyCue.app の中のシステム拡張（KeyCueCamera）。ここから拡張の入力（sink）にコマを入れる
final class VirtualCamera: NSObject, ObservableObject {
    enum State: Equatable {
        case off
        /// 入れる（有効にする）ところ。システム設定での許可待ちを含む
        case installing(String)
        case running
        case failed(String)
    }

    static let extensionID = "jp.ic2.keycue.camera"
    // 拡張と同じ値（Sources/KeyCueCamera/main.swift）
    static let deviceUID = "6B3E7C2A-4D1F-4E8B-9C55-2F7A1B0E9D41"
    static let width = 1920, height = 1080

    @Published private(set) var state = State.off
    private weak var feed: ProgramFeed?
    private var deviceID: CMIOObjectID = 0
    private var sinkID: CMIOStreamID = 0
    private var queue: CMSimpleQueue?
    private var retry: Timer?
    private let sendQueue = DispatchQueue(label: "KeyCue.VirtualCamera")
    private let context = CIContext(options: [.cacheIntermediates: false])
    private var pool: CVPixelBufferPool?
    private var format: CMFormatDescription?
    private var sentFrames = 0

    init(feed: ProgramFeed) {
        self.feed = feed
        super.init()
    }

    var isOn: Bool { state != .off }

    // MARK: 始める・止める

    func start() {
        if connect() {
            beginFeeding()
            return
        }
        let bundled = Bundle.main.bundleURL.appendingPathComponent("Contents/Library/SystemExtensions/\(VirtualCamera.extensionID).systemextension")
        guard FileManager.default.fileExists(atPath: bundled.path) else {
            state = .failed("この KeyCue には仮想カメラが入っていません")
            return
        }
        // カメラがまだない：拡張を入れる（初回はシステム設定で許可がいる）
        state = .installing("仮想カメラを入れています…")
        let r = OSSystemExtensionRequest.activationRequest(forExtensionWithIdentifier: VirtualCamera.extensionID, queue: .main)
        r.delegate = self
        OSSystemExtensionManager.shared.submitRequest(r)
        waitForDevice()
    }

    func stop() {
        retry?.invalidate()
        retry = nil
        feed?.onFrame = nil
        feed?.extraConsumer = false
        sendQueue.sync {
            if deviceID != 0 && sinkID != 0 { CMIODeviceStopStream(deviceID, sinkID) }
            queue = nil
            deviceID = 0
            sinkID = 0
        }
        state = .off
    }

    /// 仮想カメラを Mac から取り除く
    func uninstall() {
        stop()
        let r = OSSystemExtensionRequest.deactivationRequest(forExtensionWithIdentifier: VirtualCamera.extensionID, queue: .main)
        r.delegate = self
        OSSystemExtensionManager.shared.submitRequest(r)
    }

    private func waitForDevice() {
        retry?.invalidate()
        var tries = 0
        retry = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] t in
            guard let self else { t.invalidate(); return }
            tries += 1
            if self.connect() {
                t.invalidate()
                self.retry = nil
                self.beginFeeding()
            } else if tries > 300, case .installing = self.state {
                t.invalidate()
                self.state = .failed("仮想カメラが見つかりません。システム設定 › 一般 › ログイン項目と機能拡張 › カメラ機能拡張 で KeyCue をオンにしてください")
            }
        }
    }

    private func beginFeeding() {
        state = .running
        sentFrames = 0
        feed?.onFrame = { [weak self] pb in self?.send(pb) }
        feed?.extraConsumer = true
    }

    // MARK: カメラを探して、入力（sink）をつなぐ

    private func connect() -> Bool {
        guard let device = VirtualCamera.findDevice() else { return false }
        guard let sink = VirtualCamera.sinkStream(of: device) else { return false }
        var q: Unmanaged<CMSimpleQueue>?
        let pointer = Unmanaged.passUnretained(self).toOpaque()
        let err = CMIOStreamCopyBufferQueue(sink, { _, _, _ in }, pointer, &q)
        guard err == noErr, let q else { return false }
        let queue = q.takeRetainedValue()
        guard CMIODeviceStartStream(device, sink) == noErr else { return false }
        sendQueue.sync {
            self.deviceID = device
            self.sinkID = sink
            self.queue = queue
        }
        return true
    }

    static func findDevice() -> CMIOObjectID? {
        var addr = CMIOObjectPropertyAddress(mSelector: CMIOObjectPropertySelector(kCMIOHardwarePropertyDevices),
                                             mScope: CMIOObjectPropertyScope(kCMIOObjectPropertyScopeGlobal),
                                             mElement: CMIOObjectPropertyElement(kCMIOObjectPropertyElementMain))
        var size: UInt32 = 0
        guard CMIOObjectGetPropertyDataSize(CMIOObjectID(kCMIOObjectSystemObject), &addr, 0, nil, &size) == noErr, size > 0 else { return nil }
        var devices = [CMIOObjectID](repeating: 0, count: Int(size) / MemoryLayout<CMIOObjectID>.size)
        var used: UInt32 = 0
        guard CMIOObjectGetPropertyData(CMIOObjectID(kCMIOObjectSystemObject), &addr, 0, nil, size, &used, &devices) == noErr else { return nil }
        for d in devices {
            var a = CMIOObjectPropertyAddress(mSelector: CMIOObjectPropertySelector(kCMIODevicePropertyDeviceUID),
                                              mScope: CMIOObjectPropertyScope(kCMIOObjectPropertyScopeGlobal),
                                              mElement: CMIOObjectPropertyElement(kCMIOObjectPropertyElementMain))
            var uid: Unmanaged<CFString>?
            var s = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
            var u: UInt32 = 0
            if CMIOObjectGetPropertyData(d, &a, 0, nil, s, &u, &uid) == noErr, let v = uid?.takeRetainedValue() as String?,
               v.caseInsensitiveCompare(deviceUID) == .orderedSame {
                return d
            }
            s = 0
        }
        return nil
    }

    static func sinkStream(of device: CMIOObjectID) -> CMIOStreamID? {
        var addr = CMIOObjectPropertyAddress(mSelector: CMIOObjectPropertySelector(kCMIODevicePropertyStreams),
                                             mScope: CMIOObjectPropertyScope(kCMIOObjectPropertyScopeGlobal),
                                             mElement: CMIOObjectPropertyElement(kCMIOObjectPropertyElementMain))
        var size: UInt32 = 0
        guard CMIOObjectGetPropertyDataSize(device, &addr, 0, nil, &size) == noErr, size > 0 else { return nil }
        var streams = [CMIOStreamID](repeating: 0, count: Int(size) / MemoryLayout<CMIOStreamID>.size)
        var used: UInt32 = 0
        guard CMIOObjectGetPropertyData(device, &addr, 0, nil, size, &used, &streams) == noErr else { return nil }
        for s in streams {
            var a = CMIOObjectPropertyAddress(mSelector: CMIOObjectPropertySelector(kCMIOStreamPropertyDirection),
                                              mScope: CMIOObjectPropertyScope(kCMIOObjectPropertyScopeGlobal),
                                              mElement: CMIOObjectPropertyElement(kCMIOObjectPropertyElementMain))
            var direction: UInt32 = 0
            var u: UInt32 = 0
            // 0 = 出力（ほかのアプリが見る）、1 = 入力（こちらから入れる）
            if CMIOObjectGetPropertyData(s, &a, 0, nil, UInt32(MemoryLayout<UInt32>.size), &u, &direction) == noErr, direction == 1 {
                return s
            }
        }
        return nil
    }

    // MARK: コマを送る（合成のキューから）

    private func send(_ program: CVPixelBuffer) {
        sendQueue.sync {
            guard let queue else { return }
            // 受け取る側が読んでいなければ捨てる（たまらないように）
            if CMSimpleQueueGetCount(queue) >= CMSimpleQueueGetCapacity(queue) { return }
            guard let pb = fitted(program) else { return }
            if format == nil {
                CMVideoFormatDescriptionCreateForImageBuffer(allocator: kCFAllocatorDefault, imageBuffer: pb, formatDescriptionOut: &format)
            }
            guard let format else { return }
            var timing = CMSampleTimingInfo(duration: CMTime(value: 1, timescale: 30),
                                            presentationTimeStamp: CMClockGetTime(CMClockGetHostTimeClock()),
                                            decodeTimeStamp: .invalid)
            var sb: CMSampleBuffer?
            CMSampleBufferCreateForImageBuffer(allocator: kCFAllocatorDefault, imageBuffer: pb, dataReady: true, makeDataReadyCallback: nil,
                                               refcon: nil, formatDescription: format, sampleTiming: &timing, sampleBufferOut: &sb)
            guard let sb else { return }
            if CMSimpleQueueEnqueue(queue, element: Unmanaged.passRetained(sb).toOpaque()) != noErr {
                Unmanaged.passUnretained(sb).release()
            } else {
                sentFrames += 1
            }
        }
    }

    var framesSent: Int { sendQueue.sync { sentFrames } }

    /// カメラの大きさ（1920×1080）に合わせる。プログラムが同じ大きさならそのまま
    private func fitted(_ pb: CVPixelBuffer) -> CVPixelBuffer? {
        let w = CVPixelBufferGetWidth(pb), h = CVPixelBufferGetHeight(pb)
        if w == VirtualCamera.width && h == VirtualCamera.height { return pb }
        if pool == nil {
            let attrs: [String: Any] = [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
                                        kCVPixelBufferWidthKey as String: VirtualCamera.width, kCVPixelBufferHeightKey as String: VirtualCamera.height,
                                        kCVPixelBufferIOSurfacePropertiesKey as String: [:]]
            CVPixelBufferPoolCreate(nil, nil, attrs as CFDictionary, &pool)
        }
        guard let pool else { return nil }
        var out: CVPixelBuffer?
        CVPixelBufferPoolCreatePixelBuffer(nil, pool, &out)
        guard let out else { return nil }
        let s = min(Double(VirtualCamera.width) / Double(w), Double(VirtualCamera.height) / Double(h))
        let tx = (Double(VirtualCamera.width) - Double(w) * s) / 2, ty = (Double(VirtualCamera.height) - Double(h) * s) / 2
        let rect = CGRect(x: 0, y: 0, width: VirtualCamera.width, height: VirtualCamera.height)
        let image = CIImage(cvPixelBuffer: pb).transformed(by: CGAffineTransform(scaleX: s, y: s).concatenating(CGAffineTransform(translationX: tx, y: ty)))
        context.render(image.composited(over: CIImage(color: .black).cropped(to: rect)), to: out, bounds: rect, colorSpace: CGColorSpace(name: CGColorSpace.sRGB))
        return out
    }
}

extension VirtualCamera: OSSystemExtensionRequestDelegate {
    func request(_ request: OSSystemExtensionRequest, actionForReplacingExtension existing: OSSystemExtensionProperties,
                 withExtension ext: OSSystemExtensionProperties) -> OSSystemExtensionRequest.ReplacementAction {
        .replace
    }

    func requestNeedsUserApproval(_ request: OSSystemExtensionRequest) {
        state = .installing("システム設定で許可してください：一般 › ログイン項目と機能拡張 › カメラ機能拡張 で KeyCue をオン")
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.LoginItems-Settings.extension")!)
    }

    func request(_ request: OSSystemExtensionRequest, didFinishWithResult result: OSSystemExtensionRequest.Result) {
        if result == .willCompleteAfterReboot {
            state = .failed("Mac を再起動すると仮想カメラが使えるようになります")
        }
        // 入ったら waitForDevice がカメラを見つけてつなぐ
    }

    func request(_ request: OSSystemExtensionRequest, didFailWithError error: Error) {
        let e = error as NSError
        var message = e.localizedDescription
        if e.domain == OSSystemExtensionErrorDomain, e.code == OSSystemExtensionError.unsupportedParentBundleLocation.rawValue {
            message = "仮想カメラを入れるには、KeyCue を「アプリケーション」フォルダに入れてから起動してください"
        }
        retry?.invalidate()
        retry = nil
        if case .running = state { return }
        state = .failed(message)
    }
}
